#include "transport.h"
#include "logger.h"
#include <nlohmann/json.hpp>

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#endif

#include <algorithm>
#include <chrono>
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
    log_debug("[transport] Sending event: " + event.event
              + " (connected=" + std::to_string(client_connected_) + ")");
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
    log_debug("[write_loop] Writer thread started");
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
                log_error("[write_loop] pipe_write FAILED (" + std::to_string(line.size()) + " bytes)");
                break;
            } else {
                log_debug("[write_loop] Wrote " + std::to_string(line.size()) + " bytes");
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

auto TransportService::is_session_persist() const -> bool {
    std::lock_guard lock(state_mutex_);
    return config_.session_persist;
}

// ===== Connection loop =====

void TransportService::connection_loop() {
    while (running_.load()) {
        // Create pipe
        auto listen_result = platform_.listen();
        if (!listen_result) {
            log_warn("[transport] listen() failed, retrying in 1s");
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
        log_info("[transport] Client connected (pid=" + std::to_string(accept_result.value().process_id) + ")");

        // Bail out if shutdown was requested while waiting for a connection
        if (!running_.load()) {
            platform_.close_server(server_);
            break;
        }

        current_peer_ = accept_result.value();

        // Verify peer
        auto verify_result = platform_.verify_peer(current_peer_);
        if (!verify_result || !verify_result.value().verified) {
            log_error("[transport] Peer verification FAILED — rejecting connection");
            platform_.close_server(server_);
            continue;
        }
        log_info("[transport] Peer verified, entering read loop");

        client_connected_ = true;
        read_buffer_.clear();

        // Reset metrics subscription for new connection
        {
            std::lock_guard lock(state_mutex_);
            metrics_subscribed_ = false;
        }
        log_debug("[metrics] Subscription reset (new connection)");

        // Read loop
        while (running_.load() && client_connected_) {
            auto line = read_line();
            if (!line.has_value()) {
                // Check if we're stopping or client disconnected
                if (!running_.load()) break;
                // Could be partial read -- try again, or disconnected
                if (read_buffer_.empty()) {
                    log_info("[transport] Client disconnected (empty buffer)");
                    break;  // Client disconnected
                }
                continue;
            }

            if (line->empty()) {
                continue;  // Skip empty lines
            }

            log_debug("[transport] Received: " + *line);

            // Parse command
            auto cmd = parse_command(*line);
            if (!cmd.has_value()) {
                log_error("[transport] Parse failed for: " + *line);
                // Malformed JSON
                ErrorMessage err;
                err.error = ErrorCode::ParseError;
                send_error(err);
                continue;
            }

            // Dispatch and send response
            Response resp = dispatch(cmd.value());
            log_debug("[transport] Sending response for " + std::string(cmd.value().method)
                      + " (ok=" + std::to_string(resp.ok) + ")");
            send_response(resp);
        }

        // Client disconnected - prevent new writes during cleanup
        {
            client_connected_ = false;
            {
                std::lock_guard state_lock(state_mutex_);
                metrics_subscribed_ = false;
            }
            log_debug("[metrics] Subscription reset (client disconnected)");
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

            // Debug: Log metric values being sent
            std::string cpu_temp_str = m.cpu.temp_c.has_value() ? std::to_string(m.cpu.temp_c.value()) : "null";
            std::string gpu_temp_str = m.gpu.temp_c.has_value() ? std::to_string(m.gpu.temp_c.value()) : "null";
            std::string gpu_power_str = m.gpu.power_w.has_value() ? std::to_string(m.gpu.power_w.value()) + "W" : "null";
            log_debug("[metrics] Sending: cpu_util=" + std::to_string(m.cpu.util_pct)
                      + " cpu_temp=" + cpu_temp_str
                      + " gpu_util=" + std::to_string(m.gpu.util_pct)
                      + " gpu_clock=" + std::to_string(m.gpu.clock_mhz) + "MHz"
                      + " gpu_temp=" + gpu_temp_str
                      + " gpu_power=" + gpu_power_str
                      + " vram=" + std::to_string(m.gpu.vram_used_bytes.value_or(0)) + "/" + std::to_string(m.gpu.vram_total_bytes.value_or(0)) + "B"
                      + " ram_used=" + std::to_string(m.ram.used_bytes) + "B"
                      + " fan_rpm=" + std::to_string(m.fan.rpm)
                      + " power_mode=" + std::to_string(static_cast<int>(m.power.mode)));

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
    if (cmd.method == "get_charge_limit") return handle_get_charge_limit(cmd);
    if (cmd.method == "set_charge_limit") return handle_set_charge_limit(cmd);
    if (cmd.method == "get_config") return handle_get_config(cmd);
    if (cmd.method == "set_config") return handle_set_config(cmd);
    if (cmd.method == "set_session_persist") return handle_set_session_persist(cmd);
    if (cmd.method == "restore_defaults") return handle_restore_defaults(cmd);
    if (cmd.method == "get_uma_options") return handle_get_uma_options(cmd);
    if (cmd.method == "set_uma_option") return handle_set_uma_option(cmd);
    if (cmd.method == "reboot") return handle_reboot(cmd);

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

    log_info("[metrics] Subscribed (interval=" + std::to_string(interval_ms) + "ms)");
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

    log_info("[metrics] Unsubscribed");

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
        p["type"] = (profile.type == ProfileType::Adaptive) ? "adaptive" : "fixed";

        if (profile.power_state.has_value()) {
            switch (profile.power_state.value()) {
                case PowerState::Source::Battery:  p["power_state"] = "battery"; break;
                case PowerState::Source::UsbCSlow: p["power_state"] = "usb_c_slow"; break;
                case PowerState::Source::UsbCFast: p["power_state"] = "usb_c_fast"; break;
                case PowerState::Source::DcIn:     p["power_state"] = "dc_in"; break;
                default:                           p["power_state"] = nullptr; break;
            }
        } else {
            p["power_state"] = nullptr;
        }

        p["is_default"] = profile.is_default;

        if (profile.type == ProfileType::Fixed) {
            p["tdp"]["stapm"] = profile.stapm_w;
            p["tdp"]["fast"] = profile.fast_w;
            p["tdp"]["slow"] = profile.slow_w;
            p["fan_curve"] = profile.fan_curve.value_or("");
        } else {
            p["tuning"] = profile.tuning;
            p["target_temp_c"] = profile.target_temp_c;
            p["tdp_max_w"] = profile.tdp_max_w;
            p["fan_max_pct"] = profile.fan_max_pct;
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

        // Set power state ceiling from profile's assigned power state (or current state if unassigned)
        int ceiling = profile.power_state.has_value()
            ? power_state_max_tdp(profile.power_state.value())
            : power_state_max_tdp(power_.current_state());
        adaptive_.set_power_state_ceiling(ceiling);

        if (profile.type == ProfileType::Adaptive) {
            // Activate adaptive controller with profile's config
            TuningPreset preset = TuningPreset::Default;
            if (profile.tuning == "silent") preset = TuningPreset::Silent;
            else if (profile.tuning == "performance") preset = TuningPreset::Performance;

            adaptive_.activate(preset, profile.target_temp_c, profile.tdp_max_w, profile.fan_max_pct);
        } else {
            // Fixed profile: write TDP + fan curve, deactivate adaptive
            auto tdp_result = tdp_.write_tdp(profile.stapm_w, profile.fast_w, profile.slow_w);
            if (!tdp_result) {
                Response resp;
                resp.id = cmd.id;
                resp.ok = false;
                resp.error = tdp_result.error();
                return resp;
            }

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

            adaptive_.deactivate();
        }

        json data;
        data["id"] = profile.id;
        data["name"] = profile.name;
        data["type"] = (profile.type == ProfileType::Adaptive) ? "adaptive" : "fixed";

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

        // Parse type
        std::string type_str = payload.value("type", "fixed");
        profile.type = (type_str == "adaptive") ? ProfileType::Adaptive : ProfileType::Fixed;

        // Parse power_state (optional)
        if (payload.contains("power_state") && !payload["power_state"].is_null() && payload["power_state"].is_string()) {
            std::string ps = payload["power_state"].get<std::string>();
            if (ps == "battery") profile.power_state = PowerState::Source::Battery;
            else if (ps == "usb_c_slow") profile.power_state = PowerState::Source::UsbCSlow;
            else if (ps == "usb_c_fast") profile.power_state = PowerState::Source::UsbCFast;
            else if (ps == "dc_in") profile.power_state = PowerState::Source::DcIn;
        }

        // Parse is_default (optional — client can explicitly set the default for a power state)
        if (payload.contains("is_default") && payload["is_default"].is_boolean()) {
            profile.is_default = payload["is_default"].get<bool>();
        }

        if (profile.type == ProfileType::Fixed) {
            profile.stapm_w = payload.value("stapm", 25);
            profile.fast_w = payload.value("fast", 30);
            profile.slow_w = payload.value("slow", 25);
            if (payload.contains("fan_curve") && !payload["fan_curve"].is_null()) {
                profile.fan_curve = payload["fan_curve"].get<std::string>();
            }
        } else {
            profile.tuning = payload.value("tuning", "default");
            profile.target_temp_c = payload.value("target_temp_c", 85);
            profile.tdp_max_w = payload.value("tdp_max_w", 55);
            profile.fan_max_pct = payload.value("fan_max_pct", 100);
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
        c["builtin"] = profiles_.builtin_curves.count(id) > 0;
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
                resp.error = ErrorCode::BuiltinProtected;
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
            // Check builtin protection first
            if (is_builtin_curve(id, profiles_)) {
                Response resp;
                resp.id = cmd.id;
                resp.ok = false;
                resp.error = ErrorCode::BuiltinProtected;
                return resp;
            }

            auto err = delete_fan_curve(profiles_, id);
            if (err.has_value()) {
                Response resp;
                resp.id = cmd.id;
                resp.ok = false;
                resp.error = err->find("not found") != std::string::npos
                    ? ErrorCode::FanCurveNotFound
                    : ErrorCode::FanCurveInUse;
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

auto TransportService::handle_get_config(const Command& cmd) -> Response {
    std::lock_guard lock(state_mutex_);

    json data;
    data["language"] = config_.language;
    data["theme"] = config_.theme;
    data["persist"] = config_.persist;
    data["session_persist"] = config_.session_persist;
    data["charge_limit_pct"] = config_.charge_limit_pct;
    data["auto_start"] = config_.auto_start;

    // Home layout
    json widgets_array = json::array();
    for (const auto& w : config_.home_layout.widgets) {
        widgets_array.push_back({
            {"id", w.id},
            {"col_span", w.col_span},
            {"row_span", w.row_span}
        });
    }

    json hidden_widgets_array = json::array();
    for (const auto& id : config_.home_layout.hidden_widgets) {
        hidden_widgets_array.push_back(id);
    }

    data["home_layout"] = {
        {"widgets", widgets_array},
        {"hidden_widgets", hidden_widgets_array},
        {"columns", config_.home_layout.columns},
        {"column_width", config_.home_layout.column_width},
        {"window_height_rows", config_.home_layout.window_height_rows}
    };

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
            if (payload.contains("home_layout") && payload["home_layout"].is_object()) {
                auto& hl = payload["home_layout"];
                if (hl.contains("widgets") && hl["widgets"].is_array()) {
                    config_.home_layout.widgets.clear();
                    for (const auto& item : hl["widgets"]) {
                        if (item.is_object()) {
                            WidgetEntry entry;
                            if (item.contains("id") && item["id"].is_string()) {
                                entry.id = item["id"].get<std::string>();
                            }
                            if (item.contains("col_span") && item["col_span"].is_number_integer()) {
                                entry.col_span = item["col_span"].get<int>();
                            }
                            if (item.contains("row_span") && item["row_span"].is_number_integer()) {
                                entry.row_span = item["row_span"].get<int>();
                            }
                            if (!entry.id.empty()) {
                                config_.home_layout.widgets.push_back(entry);
                            }
                        }
                    }
                }
                if (hl.contains("columns") && hl["columns"].is_number_integer()) {
                    int cols = hl["columns"].get<int>();
                    if (cols == 3 || cols == 4) {
                        config_.home_layout.columns = cols;
                    }
                }
                if (hl.contains("column_width") && hl["column_width"].is_number_integer()) {
                    int cw = hl["column_width"].get<int>();
                    if (cw > 0) {
                        config_.home_layout.column_width = cw;
                    }
                }
                if (hl.contains("window_height_rows") && hl["window_height_rows"].is_number_integer()) {
                    int whr = hl["window_height_rows"].get<int>();
                    if (whr > 0) {
                        config_.home_layout.window_height_rows = whr;
                    }
                }
                if (hl.contains("hidden_widgets") && hl["hidden_widgets"].is_array()) {
                    config_.home_layout.hidden_widgets.clear();
                    for (const auto& item : hl["hidden_widgets"]) {
                        if (item.is_string()) {
                            config_.home_layout.hidden_widgets.push_back(item.get<std::string>());
                        }
                    }
                }
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

        // Clear all user profiles and user fan curves (builtins are preserved)
        profiles_.profiles.clear();
        for (auto it = profiles_.fan_curves.begin(); it != profiles_.fan_curves.end(); ) {
            if (profiles_.builtin_curves.count(it->first) == 0) {
                it = profiles_.fan_curves.erase(it);
            } else {
                ++it;
            }
        }
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

auto TransportService::handle_get_uma_options(const Command& cmd) -> Response {
    Response resp;
    resp.id = cmd.id;

    // Check if UMA is supported
    auto supported_result = platform_.uma_supported();
    if (!supported_result) {
        resp.ok = false;
        resp.error = ErrorCode::SensorUnavailable;
        return resp;
    }

    json result;
    result["supported"] = supported_result.value();

    if (supported_result.value()) {
        // Get available options
        auto options_result = platform_.uma_available_options();
        if (options_result) {
            json options_array = json::array();
            for (const auto& opt : options_result.value()) {
                json opt_json;
                opt_json["id"] = opt.id;
                opt_json["name"] = opt.name;
                opt_json["mode"] = (opt.mode == UmaOption::Mode::Custom) ? "custom" : "auto";
                opt_json["memory_carved_gb"] = opt.memory_carved_gb;
                opt_json["memory_remaining_gb"] = opt.memory_remaining_gb;
                options_array.push_back(opt_json);
            }
            result["available_options"] = options_array;
        }

        // Get current option
        auto current_result = platform_.uma_current_option();
        if (current_result) {
            const auto& current = current_result.value();
            json current_json;
            current_json["id"] = current.id;
            current_json["name"] = current.name;
            current_json["mode"] = (current.mode == UmaOption::Mode::Custom) ? "custom" : "auto";
            current_json["memory_carved_gb"] = current.memory_carved_gb;
            current_json["memory_remaining_gb"] = current.memory_remaining_gb;
            result["current_option"] = current_json;
        }
    }

    resp.ok = true;
    resp.data = result.dump();
    return resp;
}

auto TransportService::handle_set_uma_option(const Command& cmd) -> Response {
    Response resp;
    resp.id = cmd.id;

    // Parse payload
    json payload;
    try {
        payload = json::parse(cmd.payload);
    } catch (...) {
        resp.ok = false;
        resp.error = ErrorCode::ParseError;
        return resp;
    }

    if (!payload.contains("option_id") || !payload["option_id"].is_string()) {
        resp.ok = false;
        resp.error = ErrorCode::ParseError;
        return resp;
    }

    std::string option_id = payload["option_id"].get<std::string>();

    // Set the option
    auto result = platform_.uma_set_option(option_id);
    if (!result) {
        resp.ok = false;
        resp.error = result.error();
        return resp;
    }

    // Reboot immediately after setting UMA (2s delay allows response to be sent)
#ifdef _WIN32
    STARTUPINFOA si = {};
    si.cb = sizeof(si);
    PROCESS_INFORMATION pi = {};

    BOOL ok = CreateProcessA(
        nullptr,
        const_cast<char*>("shutdown.exe /r /t 2"),
        nullptr, nullptr, FALSE,
        CREATE_NO_WINDOW,
        nullptr, nullptr,
        &si, &pi);

    if (!ok) {
        log_error("[uma] CreateProcess for shutdown.exe failed");
        resp.ok = false;
        resp.error = ErrorCode::HardwareBusy;
        return resp;
    }

    CloseHandle(pi.hProcess);
    CloseHandle(pi.hThread);
    log_info("[uma] System reboot initiated via shutdown.exe (2s delay)");
#else
    log_warn("[uma] Reboot not implemented on this platform");
#endif

    resp.ok = true;
    return resp;
}

auto TransportService::handle_reboot(const Command& cmd) -> Response {
    Response resp;
    resp.id = cmd.id;

#ifdef _WIN32
    // Spawn a detached shutdown.exe process with a 2-second delay.
    // This gives the backend time to write the response to the pipe
    // before the system actually reboots.
    STARTUPINFOA si = {};
    si.cb = sizeof(si);
    PROCESS_INFORMATION pi = {};

    BOOL ok = CreateProcessA(
        nullptr,
        const_cast<char*>("shutdown.exe /r /t 2"),
        nullptr, nullptr, FALSE,
        CREATE_NO_WINDOW,
        nullptr, nullptr,
        &si, &pi);

    if (!ok) {
        log_error("[reboot] CreateProcess for shutdown.exe failed");
        resp.ok = false;
        resp.error = ErrorCode::HardwareBusy;
        return resp;
    }

    CloseHandle(pi.hProcess);
    CloseHandle(pi.hThread);

    log_info("[reboot] System reboot initiated via shutdown.exe (2s delay)");
    resp.ok = true;
#else
    log_warn("[reboot] Reboot not implemented on this platform");
    resp.ok = false;
    resp.error = ErrorCode::SensorUnavailable;
#endif
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

    // Set power state TDP ceiling from hardcoded max
    int tdp_ceiling = power_state_max_tdp(current_power_state);
    adaptive_.set_power_state_ceiling(tdp_ceiling);

    // Find default profile assigned to current power state
    const Profile* assigned = nullptr;
    for (const auto& [slug, profile] : profiles_.profiles) {
        if (profile.power_state.has_value() && profile.power_state.value() == current_power_state
            && profile.is_default) {
            assigned = &profile;
            break;
        }
    }

    if (assigned != nullptr) {
        if (assigned->type == ProfileType::Adaptive) {
            // Activate adaptive controller
            TuningPreset preset = TuningPreset::Default;
            if (assigned->tuning == "silent") preset = TuningPreset::Silent;
            else if (assigned->tuning == "performance") preset = TuningPreset::Performance;

            adaptive_.activate(preset, assigned->target_temp_c, assigned->tdp_max_w, assigned->fan_max_pct);
        } else {
            // Apply fixed profile
            (void)tdp_.write_tdp(assigned->stapm_w, assigned->fast_w, assigned->slow_w);

            if (assigned->fan_curve.has_value()) {
                auto curve_it = profiles_.fan_curves.find(assigned->fan_curve.value());
                if (curve_it != profiles_.fan_curves.end()) {
                    (void)fan_.set_mode(FanState::Mode::Curve);
                    fan_.set_curve(curve_it->second);
                }
            } else {
                (void)fan_.set_mode(FanState::Mode::Auto);
            }
        }
    }
}
