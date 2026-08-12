#include "transport.h"
#include <nlohmann/json.hpp>

#include <algorithm>
#include <chrono>
#include <iostream>
#include <vector>

using json = nlohmann::json;

// ===== Construction / Destruction =====

TransportService::TransportService(
    Platform& platform,
    MetricsPoller& metrics,
    FanController& fan,
    TdpController& tdp,
    PowerController& power,
    AdaptiveController& adaptive,
    ButtonMonitor& button,
    Config& config,
    ProfileStorage& profiles,
    std::filesystem::path config_path,
    std::filesystem::path profiles_path
)
    : platform_(platform)
    , metrics_(metrics)
    , fan_(fan)
    , tdp_(tdp)
    , power_(power)
    , adaptive_(adaptive)
    , button_(button)
    , config_(config)
    , profiles_(profiles)
    , config_path_(std::move(config_path))
    , profiles_path_(std::move(profiles_path))
{
}

TransportService::~TransportService() {
    stop();
}

// ===== Start / Stop =====

void TransportService::start() {
    if (running_.load()) {
        return;
    }

    running_.store(true);
    writer_thread_ = std::thread(&TransportService::write_loop, this);
    connection_thread_ = std::thread(&TransportService::connection_loop, this);
    metrics_push_thread_ = std::thread(&TransportService::metrics_push_loop, this);
}

void TransportService::stop() {
    if (!running_.load()) {
        return;
    }

    running_.store(false);
    event_cv_.notify_all();
    write_queue_cv_.notify_all();

    // Close the server to unblock accept_connection
    platform_.close_server(server_);

    if (connection_thread_.joinable()) {
        connection_thread_.join();
    }
    if (metrics_push_thread_.joinable()) {
        metrics_push_thread_.join();
    }
    if (writer_thread_.joinable()) {
        writer_thread_.join();
    }
}

auto TransportService::is_running() const -> bool {
    return running_.load();
}

// ===== Event sending =====

void TransportService::send_event(const Event& event) {
    // write_line enqueues for the writer thread — never blocks the caller.
    std::cout << "[transport] Sending event: " << event.event
              << " (connected=" << client_connected_ << ")" << std::endl;
    if (client_connected_) {
        send_event_immediate(event);
    }
}

void TransportService::send_event_immediate(const Event& event) {
    std::string line = serialize_event(event);
    write_line(line);
}

void TransportService::send_response(const Response& response) {
    std::string line = serialize_response(response);
    write_line(line);
}

void TransportService::send_error(const ErrorMessage& error) {
    std::string line = serialize_error(error);
    write_line(line);
}

// ===== Pipe I/O =====

auto TransportService::read_line() -> std::optional<std::string> {
    // Check if we already have a complete line in the buffer
    auto newline_pos = read_buffer_.find('\n');
    if (newline_pos != std::string::npos) {
        std::string line = read_buffer_.substr(0, newline_pos);
        read_buffer_.erase(0, newline_pos + 1);
        // Strip trailing \r (FE StreamWriter uses \r\n line endings)
        if (!line.empty() && line.back() == '\r') {
            line.pop_back();
        }
        return line;
    }

    // Read more data from the pipe
    char buffer[4096];
    auto result = platform_.pipe_read(server_, buffer, sizeof(buffer));
    if (!result) {
        return std::nullopt;  // Disconnected or error
    }

    size_t bytes_read = result.value();
    if (bytes_read == 0) {
        return std::nullopt;  // EOF
    }

    read_buffer_.append(buffer, bytes_read);

    // Try to extract a line again
    newline_pos = read_buffer_.find('\n');
    if (newline_pos != std::string::npos) {
        std::string line = read_buffer_.substr(0, newline_pos);
        read_buffer_.erase(0, newline_pos + 1);
        // Strip trailing \r (FE StreamWriter uses \r\n line endings)
        if (!line.empty() && line.back() == '\r') {
            line.pop_back();
        }
        return line;
    }

    // No complete line yet -- caller should retry
    return std::nullopt;
}

auto TransportService::write_line(const std::string& line) -> bool {
    // Enqueue for the dedicated writer thread — never blocks the caller.
    // The writer thread handles the actual pipe I/O (which may block on
    // WriteFile/FlushFileBuffers if the client's read buffer is full).
    if (!client_connected_) {
        return false;
    }
    {
        std::lock_guard lock(write_queue_mutex_);
        write_queue_.push(line);
    }
    write_queue_cv_.notify_one();
    return true;
}

void TransportService::write_loop() {
    std::cout << "[write_loop] Writer thread started" << std::endl;
    while (running_.load()) {
        // Collect all pending writes
        std::vector<std::string> batch;
        {
            std::unique_lock lock(write_queue_mutex_);
            write_queue_cv_.wait(lock, [this]() {
                return !write_queue_.empty() || !running_.load();
            });

            if (!running_.load() && write_queue_.empty()) break;

            while (!write_queue_.empty()) {
                batch.push_back(std::move(write_queue_.front()));
                write_queue_.pop();
            }
        }

        if (batch.empty()) continue;
        if (!client_connected_) continue;

        // Write all items in the batch.
        // Overlapped WriteFile delivers data to the pipe buffer immediately —
        // no FlushFileBuffers needed.
        for (const auto& line : batch) {
            auto result = platform_.pipe_write(server_, line.data(), line.size());
            if (!result.has_value()) {
                std::cerr << "[write_loop] pipe_write FAILED (" << line.size() << " bytes)" << std::endl;
                break;
            } else {
                std::cout << "[write_loop] Wrote " << line.size() << " bytes (first 80: "
                          << line.substr(0, std::min<size_t>(80, line.size())) << ")" << std::endl;
            }
        }
    }
}

// ===== Persist gate =====

auto TransportService::check_persist(const std::string& request_id) -> std::optional<Response> {
    bool session_persist;
    {
        std::lock_guard lock(state_mutex_);
        session_persist = config_.session_persist;
    }

    if (!session_persist) {
        Response resp;
        resp.id = request_id;
        resp.ok = false;
        resp.error = ErrorCode::PersistDisabled;
        return resp;
    }
    return std::nullopt;
}

// ===== Connection loop =====

void TransportService::connection_loop() {
    while (running_.load()) {
        // Create pipe
        auto listen_result = platform_.listen();
        if (!listen_result) {
            std::cerr << "[transport] listen() failed, retrying in 1s" << std::endl;
            std::this_thread::sleep_for(std::chrono::milliseconds(1000));
            continue;
        }
        server_ = listen_result.value();

        // Accept connection (1s timed wait — loops back to check running_ on timeout)
        auto accept_result = platform_.accept_connection(server_);
        if (!accept_result) {
            platform_.close_server(server_);
            continue;
        }
        std::cout << "[transport] Client connected (pid=" << accept_result.value().process_id << ")" << std::endl;

        // Bail out if shutdown was requested while waiting for a connection
        if (!running_.load()) {
            platform_.close_server(server_);
            break;
        }

        current_peer_ = accept_result.value();

        // Verify peer
        auto verify_result = platform_.verify_peer(current_peer_);
        if (!verify_result || !verify_result.value().verified) {
            std::cerr << "[transport] Peer verification FAILED — rejecting connection" << std::endl;
            platform_.close_server(server_);
            continue;
        }
        std::cout << "[transport] Peer verified, entering read loop" << std::endl;

        client_connected_ = true;
        read_buffer_.clear();

        // Reset metrics subscription for new connection
        {
            std::lock_guard lock(state_mutex_);
            metrics_subscribed_ = false;
        }
        std::cout << "[metrics] Subscription reset (new connection)" << std::endl;

        // Read loop
        while (running_.load() && client_connected_) {
            auto line = read_line();
            if (!line.has_value()) {
                // Check if we're stopping or client disconnected
                if (!running_.load()) break;
                // Could be partial read -- try again, or disconnected
                if (read_buffer_.empty()) {
                    std::cout << "[transport] Client disconnected (empty buffer)" << std::endl;
                    break;  // Client disconnected
                }
                continue;
            }

            if (line->empty()) {
                continue;  // Skip empty lines
            }

            std::cout << "[transport] Received: " << *line << std::endl;

            // Parse command
            auto cmd = parse_command(*line);
            if (!cmd.has_value()) {
                std::cerr << "[transport] Parse failed for: " << *line << std::endl;
                // Malformed JSON
                ErrorMessage err;
                err.error = ErrorCode::ParseError;
                send_error(err);
                continue;
            }

            // Dispatch and send response
            Response resp = dispatch(cmd.value());
            std::cout << "[transport] Sending response for " << cmd.value().method
                      << " (ok=" << resp.ok << ")" << std::endl;
            send_response(resp);
        }

        // Client disconnected - prevent new writes during cleanup
        {
            client_connected_ = false;
            {
                std::lock_guard state_lock(state_mutex_);
                metrics_subscribed_ = false;
            }
            std::cout << "[metrics] Subscription reset (client disconnected)" << std::endl;
            // Drain pending writes — they'll fail since client is gone
            {
                std::lock_guard wq_lock(write_queue_mutex_);
                std::queue<std::string> empty;
                write_queue_.swap(empty);
            }
            platform_.pipe_disconnect(server_);
            platform_.close_server(server_);
        }
    }
}

// ===== Metrics push loop =====

void TransportService::metrics_push_loop() {
    while (running_.load()) {
        bool subscribed;
        int interval_ms;

        // Wait for event or timeout (event_mutex_ held only during wait)
        {
            std::unique_lock lock(event_mutex_);

            {
                std::lock_guard state_lock(state_mutex_);
                subscribed = metrics_subscribed_;
                interval_ms = metrics_interval_ms_;
            }

            if (subscribed) {
                event_cv_.wait_for(lock, std::chrono::milliseconds(interval_ms));
            } else {
                // No subscription -- just wait for events (button press, etc.)
                event_cv_.wait_for(lock, std::chrono::milliseconds(1000));
            }
        }
        // event_mutex_ released — safe to do I/O and acquire other locks

        if (!running_.load()) break;

        // Send metrics if subscribed and client connected
        if (subscribed && client_connected_) {
            Metrics m = metrics_.get_metrics();
            Event evt;
            evt.event = "metrics";
            std::string metrics_json = serialize_metrics(m);
            if (!metrics_json.empty() && metrics_json.back() == '\n') {
                metrics_json.pop_back();
            }
            evt.data = metrics_json;
            send_event_immediate(evt);
        }

        // Drain event queue
        std::vector<Event> events;
        {
            std::lock_guard lock(event_mutex_);
            while (!event_queue_.empty()) {
                events.push_back(event_queue_.front());
                event_queue_.pop();
            }
        }
        for (const auto& evt : events) {
            if (client_connected_) {
                send_event_immediate(evt);
            }
        }
    }
}

// ===== Command dispatch =====

auto TransportService::dispatch(const Command& cmd) -> Response {
    // Route to handler
    if (cmd.method == "ping") return handle_ping(cmd);
    if (cmd.method == "get_metrics") return handle_get_metrics(cmd);
    if (cmd.method == "subscribe_metrics") return handle_subscribe_metrics(cmd);
    if (cmd.method == "unsubscribe_metrics") return handle_unsubscribe_metrics(cmd);
    if (cmd.method == "get_fan") return handle_get_fan(cmd);
    if (cmd.method == "set_fan") return handle_set_fan(cmd);
    if (cmd.method == "get_button") return handle_get_button(cmd);
    if (cmd.method == "get_power_mode") return handle_get_power_mode(cmd);
    if (cmd.method == "get_profiles") return handle_get_profiles(cmd);
    if (cmd.method == "set_profile") return handle_set_profile(cmd);
    if (cmd.method == "save_profile") return handle_save_profile(cmd);
    if (cmd.method == "delete_profile") return handle_delete_profile(cmd);
    if (cmd.method == "get_fan_curves") return handle_get_fan_curves(cmd);
    if (cmd.method == "save_fan_curve") return handle_save_fan_curve(cmd);
    if (cmd.method == "delete_fan_curve") return handle_delete_fan_curve(cmd);
    if (cmd.method == "get_power_profiles") return handle_get_power_profiles(cmd);
    if (cmd.method == "set_power_profile") return handle_set_power_profile(cmd);
    if (cmd.method == "get_charge_limit") return handle_get_charge_limit(cmd);
    if (cmd.method == "set_charge_limit") return handle_set_charge_limit(cmd);
    if (cmd.method == "get_auto_tune") return handle_get_auto_tune(cmd);
    if (cmd.method == "set_auto_tune") return handle_set_auto_tune(cmd);
    if (cmd.method == "get_config") return handle_get_config(cmd);
    if (cmd.method == "set_config") return handle_set_config(cmd);
    if (cmd.method == "set_session_persist") return handle_set_session_persist(cmd);
    if (cmd.method == "restore_defaults") return handle_restore_defaults(cmd);

    // Unknown command
    Response resp;
    resp.id = cmd.id;
    resp.ok = false;
    resp.error = ErrorCode::UnknownCommand;
    return resp;
}

// ===== Command handlers =====

auto TransportService::handle_ping(const Command& cmd) -> Response {
    Response resp;
    resp.id = cmd.id;
    resp.ok = true;
    resp.data = "{}";
    return resp;
}

auto TransportService::handle_get_metrics(const Command& cmd) -> Response {
    Metrics m = metrics_.get_metrics();
    std::string metrics_json = serialize_metrics(m);
    // Strip trailing newline for data field
    if (!metrics_json.empty() && metrics_json.back() == '\n') {
        metrics_json.pop_back();
    }

    Response resp;
    resp.id = cmd.id;
    resp.ok = true;
    resp.data = metrics_json;
    return resp;
}

auto TransportService::handle_subscribe_metrics(const Command& cmd) -> Response {
    int interval_ms = 2000;
    try {
        auto payload = json::parse(cmd.payload);
        interval_ms = payload.value("interval_ms", 2000);

        std::lock_guard lock(state_mutex_);
        metrics_subscribed_ = true;
        metrics_interval_ms_ = interval_ms;
    } catch (const json::exception&) {
        // Use default interval on parse error
        std::lock_guard lock(state_mutex_);
        metrics_subscribed_ = true;
        metrics_interval_ms_ = 2000;
    }

    std::cout << "[metrics] Subscribed (interval=" << interval_ms << "ms)" << std::endl;
    event_cv_.notify_one();

    Response resp;
    resp.id = cmd.id;
    resp.ok = true;
    resp.data = R"({"ok": true})";
    return resp;
}

auto TransportService::handle_unsubscribe_metrics(const Command& cmd) -> Response {
    {
        std::lock_guard lock(state_mutex_);
        metrics_subscribed_ = false;
    }

    std::cout << "[metrics] Unsubscribed" << std::endl;

    Response resp;
    resp.id = cmd.id;
    resp.ok = true;
    resp.data = R"({"ok": true})";
    return resp;
}

auto TransportService::handle_get_fan(const Command& cmd) -> Response {
    FanState state = fan_.read_state();

    std::string mode_str;
    switch (state.mode) {
        case FanState::Mode::Auto: mode_str = "auto"; break;
        case FanState::Mode::Manual: mode_str = "manual"; break;
        case FanState::Mode::Curve: mode_str = "curve"; break;
    }

    json data;
    data["mode"] = mode_str;
    data["speed_pct"] = state.speed_pct;
    data["rpm"] = state.rpm;

    Response resp;
    resp.id = cmd.id;
    resp.ok = true;
    resp.data = data.dump();
    return resp;
}

auto TransportService::handle_set_fan(const Command& cmd) -> Response {
    // Persist gate
    if (auto blocked = check_persist(cmd.id)) return blocked.value();

    try {
        auto payload = json::parse(cmd.payload);
        std::string mode_str = payload.value("mode", "auto");

        FanState::Mode mode;
        if (mode_str == "auto") {
            mode = FanState::Mode::Auto;
        } else if (mode_str == "curve") {
            mode = FanState::Mode::Curve;
        } else {
            Response resp;
            resp.id = cmd.id;
            resp.ok = false;
            resp.error = ErrorCode::FanSpeedInvalid;
            return resp;
        }

        auto result = fan_.set_mode(mode);
        if (!result) {
            Response resp;
            resp.id = cmd.id;
            resp.ok = false;
            resp.error = result.error();
            return resp;
        }

        // If curve mode and curve_id provided, set the curve
        if (mode == FanState::Mode::Curve && payload.contains("curve_id")) {
            std::string curve_id = payload["curve_id"].get<std::string>();
            std::lock_guard lock(state_mutex_);
            auto it = profiles_.fan_curves.find(curve_id);
            if (it != profiles_.fan_curves.end()) {
                fan_.set_curve(it->second);
            } else {
                Response resp;
                resp.id = cmd.id;
                resp.ok = false;
                resp.error = ErrorCode::FanCurveNotFound;
                return resp;
            }
        } else if (mode == FanState::Mode::Auto) {
            fan_.set_curve(std::nullopt);
        }

        json data;
        data["mode"] = mode_str;

        Response resp;
        resp.id = cmd.id;
        resp.ok = true;
        resp.data = data.dump();
        return resp;
    } catch (const json::exception&) {
        Response resp;
        resp.id = cmd.id;
        resp.ok = false;
        resp.error = ErrorCode::ParseError;
        return resp;
    }
}

auto TransportService::handle_get_button(const Command& cmd) -> Response {
    // Read current EC register value for button
    auto result = platform_.ec_read(0x0230);
    uint8_t value = result.value_or(0);

    json data;
    data["presses"] = value;

    Response resp;
    resp.id = cmd.id;
    resp.ok = true;
    resp.data = data.dump();
    return resp;
}

auto TransportService::handle_get_power_mode(const Command& cmd) -> Response {
    auto state = power_.current_state();

    std::string mode_str;
    std::string label;
    switch (state) {
        case PowerState::Source::Battery:
            mode_str = "battery";
            label = "Battery only";
            break;
        case PowerState::Source::UsbCSlow:
            mode_str = "usb_c_slow";
            label = "USB-C (65W class)";
            break;
        case PowerState::Source::UsbCFast:
            mode_str = "usb_c_fast";
            label = "USB-C (100W class)";
            break;
        case PowerState::Source::DcIn:
            mode_str = "dc_in";
            label = "DC-In (dedicated charger)";
            break;
        default:
            mode_str = "unknown";
            label = "Unknown";
            break;
    }

    json data;
    data["mode"] = mode_str;
    data["label"] = label;

    Response resp;
    resp.id = cmd.id;
    resp.ok = true;
    resp.data = data.dump();
    return resp;
}

auto TransportService::handle_get_profiles(const Command& cmd) -> Response {
    std::lock_guard lock(state_mutex_);

    json profiles_array = json::array();
    for (const auto& [id, profile] : profiles_.profiles) {
        json p;
        p["id"] = profile.id;
        p["name"] = profile.name;
        p["tdp"]["stapm"] = profile.stapm_w;
        p["tdp"]["fast"] = profile.fast_w;
        p["tdp"]["slow"] = profile.slow_w;
        if (profile.fan_curve.has_value()) {
            p["fan_curve"] = profile.fan_curve.value();
        } else {
            p["fan_curve"] = nullptr;
        }
        profiles_array.push_back(p);
    }

    json data;
    data["profiles"] = profiles_array;

    Response resp;
    resp.id = cmd.id;
    resp.ok = true;
    resp.data = data.dump();
    return resp;
}

auto TransportService::handle_set_profile(const Command& cmd) -> Response {
    // Persist gate
    if (auto blocked = check_persist(cmd.id)) return blocked.value();

    try {
        auto payload = json::parse(cmd.payload);
        std::string id = payload.value("id", "");

        Profile profile;
        {
            std::lock_guard lock(state_mutex_);
            auto it = profiles_.profiles.find(id);
            if (it == profiles_.profiles.end()) {
                Response resp;
                resp.id = cmd.id;
                resp.ok = false;
                resp.error = ErrorCode::ProfileNotFound;
                return resp;
            }
            profile = it->second;
        }

        // Apply TDP limits
        auto tdp_result = tdp_.write_tdp(profile.stapm_w, profile.fast_w, profile.slow_w);
        if (!tdp_result) {
            Response resp;
            resp.id = cmd.id;
            resp.ok = false;
            resp.error = tdp_result.error();
            return resp;
        }

        // Apply fan curve
        if (profile.fan_curve.has_value()) {
            std::lock_guard lock(state_mutex_);
            auto curve_it = profiles_.fan_curves.find(profile.fan_curve.value());
            if (curve_it != profiles_.fan_curves.end()) {
                (void)fan_.set_mode(FanState::Mode::Curve);
                fan_.set_curve(curve_it->second);
            } else {
                (void)fan_.set_mode(FanState::Mode::Auto);
                fan_.set_curve(std::nullopt);
            }
        } else {
            (void)fan_.set_mode(FanState::Mode::Auto);
            fan_.set_curve(std::nullopt);
        }

        // Deactivate adaptive controller (mutually exclusive)
        adaptive_.deactivate();

        json data;
        data["id"] = profile.id;
        data["name"] = profile.name;

        Response resp;
        resp.id = cmd.id;
        resp.ok = true;
        resp.data = data.dump();
        return resp;
    } catch (const json::exception&) {
        Response resp;
        resp.id = cmd.id;
        resp.ok = false;
        resp.error = ErrorCode::ParseError;
        return resp;
    }
}

auto TransportService::handle_save_profile(const Command& cmd) -> Response {
    try {
        auto payload = json::parse(cmd.payload);

        Profile profile;
        profile.id = payload.value("id", "");
        profile.name = payload.value("name", "");
        profile.stapm_w = payload.value("stapm", 25);
        profile.fast_w = payload.value("fast", 30);
        profile.slow_w = payload.value("slow", 25);

        if (payload.contains("fan_curve") && !payload["fan_curve"].is_null()) {
            profile.fan_curve = payload["fan_curve"].get<std::string>();
        }

        // Generate slug if id is empty
        if (profile.id.empty()) {
            std::lock_guard lock(state_mutex_);
            std::map<std::string, bool> existing;
            for (const auto& [k, v] : profiles_.profiles) {
                existing[k] = true;
            }
            profile.id = generate_slug(profile.name, existing);
        }

        // Save
        {
            std::lock_guard lock(state_mutex_);
            auto err = save_profile(profiles_, profile);
            if (err.has_value()) {
                Response resp;
                resp.id = cmd.id;
                resp.ok = false;
                resp.error = ErrorCode::ProfileNotFound;  // Generic error
                return resp;
            }
            save_profiles(profiles_path_, profiles_);
        }

        json data;
        data["id"] = profile.id;

        Response resp;
        resp.id = cmd.id;
        resp.ok = true;
        resp.data = data.dump();
        return resp;
    } catch (const json::exception&) {
        Response resp;
        resp.id = cmd.id;
        resp.ok = false;
        resp.error = ErrorCode::ParseError;
        return resp;
    }
}

auto TransportService::handle_delete_profile(const Command& cmd) -> Response {
    try {
        auto payload = json::parse(cmd.payload);
        std::string id = payload.value("id", "");

        // Check if profile is referenced by any power state
        {
            std::lock_guard lock(state_mutex_);
            const auto& psp = config_.power_state_profiles;
            if (psp.battery.profile == id || psp.usb_c_slow.profile == id ||
                psp.usb_c_fast.profile == id || psp.dc_in.profile == id) {
                Response resp;
                resp.id = cmd.id;
                resp.ok = false;
                resp.error = ErrorCode::ProfileInUse;
                return resp;
            }
        }

        // Delete
        {
            std::lock_guard lock(state_mutex_);
            auto err = delete_profile(profiles_, id);
            if (err.has_value()) {
                Response resp;
                resp.id = cmd.id;
                resp.ok = false;
                resp.error = ErrorCode::ProfileNotFound;
                return resp;
            }
            save_profiles(profiles_path_, profiles_);
        }

        Response resp;
        resp.id = cmd.id;
        resp.ok = true;
        resp.data = "{}";
        return resp;
    } catch (const json::exception&) {
        Response resp;
        resp.id = cmd.id;
        resp.ok = false;
        resp.error = ErrorCode::ParseError;
        return resp;
    }
}

auto TransportService::handle_get_fan_curves(const Command& cmd) -> Response {
    std::lock_guard lock(state_mutex_);

    json curves_array = json::array();
    for (const auto& [id, curve] : profiles_.fan_curves) {
        json c;
        c["id"] = curve.id;
        c["name"] = curve.name;
        json points = json::array();
        for (const auto& pt : curve.points) {
            json p;
            p["temp_c"] = pt.temp_c;
            p["speed_pct"] = pt.speed_pct;
            points.push_back(p);
        }
        c["points"] = points;
        curves_array.push_back(c);
    }

    json data;
    data["fan_curves"] = curves_array;

    Response resp;
    resp.id = cmd.id;
    resp.ok = true;
    resp.data = data.dump();
    return resp;
}

auto TransportService::handle_save_fan_curve(const Command& cmd) -> Response {
    try {
        auto payload = json::parse(cmd.payload);

        FanCurve curve;
        curve.id = payload.value("id", "");
        curve.name = payload.value("name", "");

        if (payload.contains("points") && payload["points"].is_array()) {
            for (const auto& pt : payload["points"]) {
                FanCurvePoint point;
                point.temp_c = pt.value("temp_c", 0);
                point.speed_pct = pt.value("speed_pct", 0);
                curve.points.push_back(point);
            }
        }

        // Generate slug if id is empty
        if (curve.id.empty()) {
            std::lock_guard lock(state_mutex_);
            std::map<std::string, bool> existing;
            for (const auto& [k, v] : profiles_.fan_curves) {
                existing[k] = true;
            }
            curve.id = generate_slug(curve.name, existing);
        }

        // Validate
        std::string validation_error;
        if (!validate_fan_curve(curve, validation_error)) {
            Response resp;
            resp.id = cmd.id;
            resp.ok = false;
            resp.error = ErrorCode::FanCurveInvalid;
            return resp;
        }

        // Save
        {
            std::lock_guard lock(state_mutex_);
            auto err = save_fan_curve(profiles_, curve);
            if (err.has_value()) {
                Response resp;
                resp.id = cmd.id;
                resp.ok = false;
                resp.error = ErrorCode::FanCurveInvalid;
                return resp;
            }
            save_profiles(profiles_path_, profiles_);
        }

        json data;
        data["id"] = curve.id;

        Response resp;
        resp.id = cmd.id;
        resp.ok = true;
        resp.data = data.dump();
        return resp;
    } catch (const json::exception&) {
        Response resp;
        resp.id = cmd.id;
        resp.ok = false;
        resp.error = ErrorCode::ParseError;
        return resp;
    }
}

auto TransportService::handle_delete_fan_curve(const Command& cmd) -> Response {
    try {
        auto payload = json::parse(cmd.payload);
        std::string id = payload.value("id", "");

        {
            std::lock_guard lock(state_mutex_);
            auto err = delete_fan_curve(profiles_, id);
            if (err.has_value()) {
                Response resp;
                resp.id = cmd.id;
                resp.ok = false;
                resp.error = ErrorCode::FanCurveInUse;
                return resp;
            }
            save_profiles(profiles_path_, profiles_);
        }

        Response resp;
        resp.id = cmd.id;
        resp.ok = true;
        resp.data = "{}";
        return resp;
    } catch (const json::exception&) {
        Response resp;
        resp.id = cmd.id;
        resp.ok = false;
        resp.error = ErrorCode::ParseError;
        return resp;
    }
}

auto TransportService::handle_get_power_profiles(const Command& cmd) -> Response {
    std::lock_guard lock(state_mutex_);

    json data;
    data["battery"]["profile"] = config_.power_state_profiles.battery.profile;
    data["battery"]["tdp_max_w"] = config_.power_state_profiles.battery.tdp_max_w;
    data["usb_c_slow"]["profile"] = config_.power_state_profiles.usb_c_slow.profile;
    data["usb_c_slow"]["tdp_max_w"] = config_.power_state_profiles.usb_c_slow.tdp_max_w;
    data["usb_c_fast"]["profile"] = config_.power_state_profiles.usb_c_fast.profile;
    data["usb_c_fast"]["tdp_max_w"] = config_.power_state_profiles.usb_c_fast.tdp_max_w;
    data["dc_in"]["profile"] = config_.power_state_profiles.dc_in.profile;
    data["dc_in"]["tdp_max_w"] = config_.power_state_profiles.dc_in.tdp_max_w;

    Response resp;
    resp.id = cmd.id;
    resp.ok = true;
    resp.data = data.dump();
    return resp;
}

auto TransportService::handle_set_power_profile(const Command& cmd) -> Response {
    try {
        auto payload = json::parse(cmd.payload);
        std::string state = payload.value("state", "");
        std::string profile = payload.value("profile", "");
        int tdp_max_w = payload.value("tdp_max_w", 25);

        {
            std::lock_guard lock(state_mutex_);

            if (state == "battery") {
                config_.power_state_profiles.battery.profile = profile;
                config_.power_state_profiles.battery.tdp_max_w = tdp_max_w;
            } else if (state == "usb_c_slow") {
                config_.power_state_profiles.usb_c_slow.profile = profile;
                config_.power_state_profiles.usb_c_slow.tdp_max_w = tdp_max_w;
            } else if (state == "usb_c_fast") {
                config_.power_state_profiles.usb_c_fast.profile = profile;
                config_.power_state_profiles.usb_c_fast.tdp_max_w = tdp_max_w;
            } else if (state == "dc_in") {
                config_.power_state_profiles.dc_in.profile = profile;
                config_.power_state_profiles.dc_in.tdp_max_w = tdp_max_w;
            } else {
                Response resp;
                resp.id = cmd.id;
                resp.ok = false;
                resp.error = ErrorCode::UnknownCommand;  // Invalid state
                return resp;
            }

            save_config(config_path_, config_);
        }

        Response resp;
        resp.id = cmd.id;
        resp.ok = true;
        resp.data = "{}";
        return resp;
    } catch (const json::exception&) {
        Response resp;
        resp.id = cmd.id;
        resp.ok = false;
        resp.error = ErrorCode::ParseError;
        return resp;
    }
}

auto TransportService::handle_get_charge_limit(const Command& cmd) -> Response {
    auto result = power_.read_charge_limit();
    if (!result) {
        // Fall back to last known value
        auto last = power_.last_charge_limit();
        if (last.has_value()) {
            json data;
            data["percent"] = last.value();

            Response resp;
            resp.id = cmd.id;
            resp.ok = true;
            resp.data = data.dump();
            return resp;
        }

        Response resp;
        resp.id = cmd.id;
        resp.ok = false;
        resp.error = result.error();
        return resp;
    }

    json data;
    data["percent"] = result.value();

    Response resp;
    resp.id = cmd.id;
    resp.ok = true;
    resp.data = data.dump();
    return resp;
}

auto TransportService::handle_set_charge_limit(const Command& cmd) -> Response {
    // Persist gate
    if (auto blocked = check_persist(cmd.id)) return blocked.value();

    try {
        auto payload = json::parse(cmd.payload);
        int percent = payload.value("percent", 100);

        if (percent < 75 || percent > 100) {
            Response resp;
            resp.id = cmd.id;
            resp.ok = false;
            resp.error = ErrorCode::ChargeLimitInvalid;
            return resp;
        }

        auto result = power_.write_charge_limit(static_cast<uint8_t>(percent));
        if (!result) {
            Response resp;
            resp.id = cmd.id;
            resp.ok = false;
            resp.error = ErrorCode::ChargeLimitWriteFail;
            return resp;
        }

        // Update config
        {
            std::lock_guard lock(state_mutex_);
            config_.charge_limit_pct = percent;
            save_config(config_path_, config_);
        }

        json data;
        data["percent"] = percent;

        Response resp;
        resp.id = cmd.id;
        resp.ok = true;
        resp.data = data.dump();
        return resp;
    } catch (const json::exception&) {
        Response resp;
        resp.id = cmd.id;
        resp.ok = false;
        resp.error = ErrorCode::ParseError;
        return resp;
    }
}

auto TransportService::handle_get_auto_tune(const Command& cmd) -> Response {
    auto config = adaptive_.config();

    json data;
    data["active"] = config.active;

    std::string tuning_str;
    switch (config.tuning) {
        case TuningPreset::Silent: tuning_str = "silent"; break;
        case TuningPreset::Default: tuning_str = "default"; break;
        case TuningPreset::Performance: tuning_str = "performance"; break;
    }
    data["tuning"] = tuning_str;
    data["target_temp_c"] = config.target_temp_c;
    data["tdp_max_w"] = config.tdp_max_w;
    data["effective_tdp_max_w"] = adaptive_.effective_tdp_max();
    data["fan_max_pct"] = config.fan_max_pct;

    Response resp;
    resp.id = cmd.id;
    resp.ok = true;
    resp.data = data.dump();
    return resp;
}

auto TransportService::handle_set_auto_tune(const Command& cmd) -> Response {
    // Persist gate
    if (auto blocked = check_persist(cmd.id)) return blocked.value();

    try {
        auto payload = json::parse(cmd.payload);

        std::string tuning_str = payload.value("tuning", "default");
        int target_temp_c = payload.value("target_temp_c", 85);
        int tdp_max_w = payload.value("tdp_max_w", 55);
        int fan_max_pct = payload.value("fan_max_pct", 100);

        TuningPreset preset;
        if (tuning_str == "silent") {
            preset = TuningPreset::Silent;
        } else if (tuning_str == "performance") {
            preset = TuningPreset::Performance;
        } else {
            preset = TuningPreset::Default;
        }

        adaptive_.activate(preset, target_temp_c, tdp_max_w, fan_max_pct);

        // Update config
        {
            std::lock_guard lock(state_mutex_);
            AutoTuneConfig atc;
            atc.enabled = true;
            atc.tuning = tuning_str;
            atc.target_temp_c = target_temp_c;
            atc.tdp_max_w = tdp_max_w;
            atc.fan_max_pct = fan_max_pct;
            config_.auto_tune = atc;
            save_config(config_path_, config_);
        }

        Response resp;
        resp.id = cmd.id;
        resp.ok = true;
        resp.data = "{}";
        return resp;
    } catch (const json::exception&) {
        Response resp;
        resp.id = cmd.id;
        resp.ok = false;
        resp.error = ErrorCode::ParseError;
        return resp;
    }
}

auto TransportService::handle_get_config(const Command& cmd) -> Response {
    std::lock_guard lock(state_mutex_);

    json data;
    data["language"] = config_.language;
    data["theme"] = config_.theme;
    data["persist"] = config_.persist;
    data["session_persist"] = config_.session_persist;
    data["charge_limit_pct"] = config_.charge_limit_pct;
    data["auto_start"] = config_.auto_start;

    if (config_.auto_tune.has_value()) {
        json at;
        at["enabled"] = config_.auto_tune->enabled;
        at["tuning"] = config_.auto_tune->tuning;
        at["target_temp_c"] = config_.auto_tune->target_temp_c;
        at["tdp_max_w"] = config_.auto_tune->tdp_max_w;
        at["fan_max_pct"] = config_.auto_tune->fan_max_pct;
        data["auto_tune"] = at;
    } else {
        data["auto_tune"] = nullptr;
    }

    json psp;
    psp["battery"]["profile"] = config_.power_state_profiles.battery.profile;
    psp["battery"]["tdp_max_w"] = config_.power_state_profiles.battery.tdp_max_w;
    psp["usb_c_slow"]["profile"] = config_.power_state_profiles.usb_c_slow.profile;
    psp["usb_c_slow"]["tdp_max_w"] = config_.power_state_profiles.usb_c_slow.tdp_max_w;
    psp["usb_c_fast"]["profile"] = config_.power_state_profiles.usb_c_fast.profile;
    psp["usb_c_fast"]["tdp_max_w"] = config_.power_state_profiles.usb_c_fast.tdp_max_w;
    psp["dc_in"]["profile"] = config_.power_state_profiles.dc_in.profile;
    psp["dc_in"]["tdp_max_w"] = config_.power_state_profiles.dc_in.tdp_max_w;
    data["power_state_profiles"] = psp;

    Response resp;
    resp.id = cmd.id;
    resp.ok = true;
    resp.data = data.dump();
    return resp;
}

auto TransportService::handle_set_config(const Command& cmd) -> Response {
    try {
        auto payload = json::parse(cmd.payload);

        {
            std::lock_guard lock(state_mutex_);

            // Partial update -- only update fields that are present
            if (payload.contains("language")) {
                config_.language = payload["language"].get<std::string>();
            }
            if (payload.contains("theme")) {
                config_.theme = payload["theme"].get<std::string>();
            }
            if (payload.contains("persist")) {
                config_.persist = payload["persist"].get<bool>();
            }
            if (payload.contains("charge_limit_pct")) {
                config_.charge_limit_pct = payload["charge_limit_pct"].get<int>();
            }
            if (payload.contains("auto_start")) {
                config_.auto_start = payload["auto_start"].get<bool>();
            }

            save_config(config_path_, config_);
        }

        // Return updated config
        return handle_get_config(cmd);
    } catch (const json::exception&) {
        Response resp;
        resp.id = cmd.id;
        resp.ok = false;
        resp.error = ErrorCode::ParseError;
        return resp;
    }
}

auto TransportService::handle_set_session_persist(const Command& cmd) -> Response {
    try {
        auto payload = json::parse(cmd.payload);

        if (!payload.contains("value") || !payload["value"].is_boolean()) {
            Response resp;
            resp.id = cmd.id;
            resp.ok = false;
            resp.error = ErrorCode::ParseError;
            return resp;
        }

        bool new_value = payload["value"].get<bool>();
        bool old_value;

        {
            std::lock_guard lock(state_mutex_);
            old_value = config_.session_persist;
            config_.session_persist = new_value;
        }

        // If transitioning from false to true, apply all settings to hardware
        if (!old_value && new_value) {
            apply_all_settings();
        }

        Response resp;
        resp.id = cmd.id;
        resp.ok = true;
        return resp;
    } catch (const json::exception&) {
        Response resp;
        resp.id = cmd.id;
        resp.ok = false;
        resp.error = ErrorCode::ParseError;
        return resp;
    }
}

auto TransportService::handle_restore_defaults(const Command& cmd) -> Response {
    {
        std::lock_guard lock(state_mutex_);

        // Reset config to defaults
        config_ = get_default_config();
        config_.session_persist = false;  // Reset session_persist too
        save_config(config_path_, config_);

        // Clear all profiles and fan curves
        profiles_.profiles.clear();
        profiles_.fan_curves.clear();
        save_profiles(profiles_path_, profiles_);
    }

    // Reset hardware to safe defaults
    (void)fan_.set_mode(FanState::Mode::Auto);
    // TDP and charge limit will be at BIOS defaults on next boot

    Response resp;
    resp.id = cmd.id;
    resp.ok = true;
    return resp;
}

void TransportService::apply_all_settings() {
    // Apply charge limit
    if (config_.charge_limit_pct >= 75 && config_.charge_limit_pct <= 100) {
        (void)power_.write_charge_limit(static_cast<uint8_t>(config_.charge_limit_pct));
    }

    // Get current power state
    power_.update_power_state();
    auto current_power_state = power_.current_state();

    // Get power state profile
    std::string profile_slug;
    switch (current_power_state) {
        case PowerState::Source::Battery:
            profile_slug = config_.power_state_profiles.battery.profile;
            break;
        case PowerState::Source::UsbCSlow:
            profile_slug = config_.power_state_profiles.usb_c_slow.profile;
            break;
        case PowerState::Source::UsbCFast:
            profile_slug = config_.power_state_profiles.usb_c_fast.profile;
            break;
        case PowerState::Source::DcIn:
            profile_slug = config_.power_state_profiles.dc_in.profile;
            break;
        default:
            break;
    }

    // Apply profile if configured
    if (!profile_slug.empty()) {
        auto it = profiles_.profiles.find(profile_slug);
        if (it != profiles_.profiles.end()) {
            const auto& profile = it->second;

            // Apply TDP limits
            (void)tdp_.write_tdp(profile.stapm_w, profile.fast_w, profile.slow_w);

            // Apply fan curve
            if (profile.fan_curve.has_value()) {
                auto curve_it = profiles_.fan_curves.find(profile.fan_curve.value());
                if (curve_it != profiles_.fan_curves.end()) {
                    (void)fan_.set_mode(FanState::Mode::Curve);
                    fan_.set_curve(curve_it->second);
                }
            } else {
                (void)fan_.set_mode(FanState::Mode::Auto);
            }
        }
    }

    // Restore adaptive controller if configured
    if (config_.auto_tune.has_value() && config_.auto_tune->enabled) {
        const auto& at = config_.auto_tune.value();
        TuningPreset preset = TuningPreset::Default;
        if (at.tuning == "silent") preset = TuningPreset::Silent;
        else if (at.tuning == "performance") preset = TuningPreset::Performance;

        adaptive_.activate(preset, at.target_temp_c, at.tdp_max_w, at.fan_max_pct);
    }
}
