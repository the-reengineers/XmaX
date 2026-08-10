#pragma once

#include "platform/platform.h"

#include <atomic>
#include <chrono>
#include <functional>
#include <mutex>
#include <optional>
#include <thread>

// ButtonMonitor -- polls hardware button and toggles frontend visibility.
//
// Responsibilities:
//   - Poll EC register 0x0230 at 100ms intervals
//   - Edge detection: any state change means a button press occurred
//   - The register value itself is discarded -- only the transition matters
//   - Toggle internal visibility state (bool fe_visible)
//   - Notify via callback when visibility changes
//
// Visibility state:
//   The ButtonMonitor tracks fe_visible as the source of truth for frontend
//   window visibility. Both the hardware button and tray icon toggle the same
//   state. The EC register values (0x00↔0x06) are NOT the visibility state --
//   they only signal that a press happened.
//
// Initialization:
//   APP_FUN_EN must be initialized before the monitor can detect presses.
//   Call init_app_fun_en() once before starting the monitor thread.
//
// Thread safety: all public methods are safe to call from any thread.

class ButtonMonitor {
public:
    // Callback for visibility changes.
    // Parameter: new visibility state (true = visible, false = hidden)
    using VisibilityCallback = std::function<void(bool)>;

    explicit ButtonMonitor(Platform& platform);
    ~ButtonMonitor();

    // Start monitor thread (100ms poll rate).
    void start();

    // Stop monitor thread.
    void stop();

    // Check if monitor thread is running.
    auto is_running() const -> bool;

    // Manual poll -- read EC register and detect state change.
    // Returns true if a button press was detected.
    // Thread-safe; can be called from any thread.
    auto poll() -> bool;

    // Get current visibility state.
    auto is_visible() const -> bool;

    // Toggle visibility manually (e.g., from tray icon click).
    // Calls the visibility callback if registered.
    void toggle_visibility();

    // Set initial visibility state without triggering callback.
    // Used on startup/reconnect to sync state.
    void set_visible(bool visible);

    // Register callback for visibility changes.
    void on_visibility_change(VisibilityCallback callback);

    // Initialize APP_FUN_EN register.
    // Must be called once before starting the monitor.
    void init_app_fun_en();

private:
    void monitor_loop();

    // EC register addresses
    static constexpr uint16_t EC_BUTTON     = 0x0230;
    static constexpr uint16_t EC_APP_FUN_EN = 0x0231;  // APP_FUN_EN register

    // Poll interval
    static constexpr std::chrono::milliseconds POLL_INTERVAL{100};

    Platform& platform_;
    std::thread monitor_thread_;
    std::atomic<bool> running_{false};

    mutable std::mutex mutex_;
    bool fe_visible_ = false;
    std::optional<uint8_t> last_button_state_;
    VisibilityCallback visibility_callback_;
};
