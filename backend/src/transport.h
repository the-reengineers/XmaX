#pragma once

#include "shared.h"
#include "protocol.h"
#include "config.h"
#include "profiles.h"
#include "platform/platform.h"
#include "metrics.h"
#include "fan.h"
#include "tdp.h"
#include "power.h"
#include "adaptive.h"
#include "button.h"

#include <atomic>
#include <condition_variable>
#include <filesystem>
#include <functional>
#include <mutex>
#include <queue>
#include <string>
#include <thread>

// TransportService -- IPC transport layer.
//
// Responsibilities:
//   - Accept frontend connections via Named Pipe
//   - Perform security handshake (verify peer)
//   - Read JSON lines from client, parse commands
//   - Dispatch commands to appropriate handlers
//   - Send responses with request ID correlation
//   - Send unsolicited events (button_press, metrics push, etc.)
//   - Handle metrics subscription (periodic push at interval)
//   - Handle disconnection and cleanup
//
// Thread model:
//   - Connection thread: accepts connections, reads commands, dispatches
//   - Metrics push thread: sends metrics events at subscribed interval
//   - Event queue: thread-safe queue for unsolicited events
//
// Persist gating:
//   Hardware write commands (set_fan, set_profile, set_charge_limit)
//   are rejected when config.persist is false. Read-only and disk-write commands
//   are always allowed.
//
// Thread safety: all public methods are safe to call from any thread.

class TransportService {
public:
    TransportService(
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
    );
    ~TransportService();

    // Start transport server (connection thread + metrics push thread).
    void start();

    // Stop transport server and join threads.
    void stop();

    // Check if server is running.
    auto is_running() const -> bool;

    // Send unsolicited event to connected client.
    // Thread-safe -- queues the event for delivery.
    void send_event(const Event& event);

    // Dispatch a command and return response.
    // Thread-safe -- can be called from any thread (used for testing).
    auto dispatch(const Command& cmd) -> Response;

private:
    // Connection loop (runs in background thread)
    void connection_loop();

    // Metrics push loop (runs in background thread)
    void metrics_push_loop();

    // Read a newline-delimited JSON line from the pipe
    auto read_line() -> std::optional<std::string>;

    // Write a line to the pipe (thread-safe)
    auto write_line(const std::string& line) -> bool;

    // Send a response to the connected client
    void send_response(const Response& response);

    // Send an event to the connected client
    void send_event_immediate(const Event& event);

    // Send an error message to the connected client
    void send_error(const ErrorMessage& error);

    // Check persist gate -- returns error response if persist is false
    auto check_persist(const std::string& request_id) -> std::optional<Response>;

    // ===== Command handlers =====

    auto handle_ping(const Command& cmd) -> Response;
    auto handle_get_metrics(const Command& cmd) -> Response;
    auto handle_subscribe_metrics(const Command& cmd) -> Response;
    auto handle_unsubscribe_metrics(const Command& cmd) -> Response;
    auto handle_get_fan(const Command& cmd) -> Response;
    auto handle_set_fan(const Command& cmd) -> Response;
    auto handle_get_button(const Command& cmd) -> Response;
    auto handle_get_power_mode(const Command& cmd) -> Response;
    auto handle_get_profiles(const Command& cmd) -> Response;
    auto handle_set_profile(const Command& cmd) -> Response;
    auto handle_save_profile(const Command& cmd) -> Response;
    auto handle_delete_profile(const Command& cmd) -> Response;
    auto handle_get_fan_curves(const Command& cmd) -> Response;
    auto handle_save_fan_curve(const Command& cmd) -> Response;
    auto handle_delete_fan_curve(const Command& cmd) -> Response;
    auto handle_get_charge_limit(const Command& cmd) -> Response;
    auto handle_set_charge_limit(const Command& cmd) -> Response;
    auto handle_get_config(const Command& cmd) -> Response;
    auto handle_set_config(const Command& cmd) -> Response;
    auto handle_set_session_persist(const Command& cmd) -> Response;
    auto handle_restore_defaults(const Command& cmd) -> Response;
    auto handle_get_uma_options(const Command& cmd) -> Response;
    auto handle_set_uma_option(const Command& cmd) -> Response;
    auto handle_reboot(const Command& cmd) -> Response;

    // Apply all configured settings to hardware (used when session_persist transitions to true)
    void apply_all_settings();

public:
    // Check if session_persist is enabled (thread-safe)
    auto is_session_persist() const -> bool;

private:
    // References to subsystems
    Platform& platform_;
    MetricsPoller& metrics_;
    FanController& fan_;
    TdpController& tdp_;
    PowerController& power_;
    AdaptiveController& adaptive_;
    ButtonMonitor& button_;

    // Config and profiles (mutable, shared state)
    Config& config_;
    ProfileStorage& profiles_;
    std::filesystem::path config_path_;
    std::filesystem::path profiles_path_;

    // Threading
    std::thread connection_thread_;
    std::thread metrics_push_thread_;
    std::thread writer_thread_;
    std::atomic<bool> running_{false};

    // Pipe state (uses ::TransportServer struct from platform.h)
    ::TransportServer server_{};
    bool client_connected_ = false;
    PeerId current_peer_;

    // Read buffer for line-based reading
    std::string read_buffer_;

    // Metrics subscription
    bool metrics_subscribed_ = false;
    int metrics_interval_ms_ = 2000;

    // Event queue
    std::queue<Event> event_queue_;
    std::mutex event_mutex_;
    std::condition_variable event_cv_;

    // Write queue -- dedicated writer thread drains this so callers never block on pipe I/O
    std::queue<std::string> write_queue_;
    std::mutex write_queue_mutex_;
    std::condition_variable write_queue_cv_;

    // Write loop (runs in writer_thread_)
    void write_loop();

    // Config/profiles mutex
    mutable std::mutex state_mutex_;
};
