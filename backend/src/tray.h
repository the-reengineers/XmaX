#pragma once

#include "shared.h"
#include "platform/platform.h"

#include <functional>
#include <mutex>
#include <string>

// TrayManager -- manages system tray icon, tooltip, and context menu.
//
// Responsibilities:
//   - Create tray icon via Platform
//   - Update tooltip at 1Hz with current metrics
//   - Handle left-click (toggle frontend visibility)
//   - Handle right-click (context menu: Show, Restart, Quit)
//
// Tooltip format: "45W | 79°C | Gaming" (TDP, CPU temp, active profile)
//
// Thread safety: all public methods are safe to call from any thread.

class TrayManager {
public:
    // Callbacks for tray actions
    using ToggleCallback = std::function<void()>;    // Left-click: toggle visibility
    using ShowCallback = std::function<void()>;      // Context menu: Show Frontend
    using RestartCallback = std::function<void()>;   // Context menu: Restart Frontend
    using QuitCallback = std::function<void()>;      // Context menu: Quit

    explicit TrayManager(Platform& platform);
    ~TrayManager();

    // Create tray icon.
    auto start() -> Result<void>;

    // Remove tray icon.
    void stop();

    // Check if tray icon is active.
    auto is_active() const -> bool;

    // Update tooltip with current metrics.
    // Format: "45W | 79°C | Gaming" (TDP, CPU temp, active profile)
    void update_tooltip(const Metrics& metrics, const std::string& profile_name = "");

    // Set callbacks for tray actions.
    void on_toggle(ToggleCallback callback);
    void on_show(ShowCallback callback);
    void on_restart(RestartCallback callback);
    void on_quit(QuitCallback callback);

private:
    // Format tooltip string from metrics
    static auto format_tooltip(const Metrics& metrics, const std::string& profile_name) -> std::string;

    Platform& platform_;

    mutable std::mutex mutex_;
    ::TrayHandle handle_{};
    bool active_ = false;

    ToggleCallback toggle_callback_;
    ShowCallback show_callback_;
    RestartCallback restart_callback_;
    QuitCallback quit_callback_;
};
