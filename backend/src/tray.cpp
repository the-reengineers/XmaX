#include "tray.h"

#include <sstream>

TrayManager::TrayManager(Platform& platform)
    : platform_(platform)
{
}

TrayManager::~TrayManager() {
    stop();
}

auto TrayManager::start() -> Result<void> {
    std::lock_guard lock(mutex_);

    if (active_) {
        return {};
    }

    // Create tray config with callbacks
    TrayConfig config;
    config.icon_path = "";  // Use default icon
    config.tooltip = "XmaX";

    // Wire up callbacks
    config.on_left_click = [this]() {
        std::lock_guard lock(mutex_);
        if (toggle_callback_) {
            toggle_callback_();
        }
    };

    config.on_right_click = [this]() {
        std::lock_guard lock(mutex_);
        if (show_callback_) {
            show_callback_();
        }
    };

    auto result = platform_.tray_icon(config);
    if (!result) {
        return std::unexpected(result.error());
    }

    handle_ = result.value();
    active_ = true;

    return {};
}

void TrayManager::stop() {
    std::lock_guard lock(mutex_);

    if (!active_) {
        return;
    }

    platform_.remove_tray_icon(handle_);
    active_ = false;
}

auto TrayManager::is_active() const -> bool {
    std::lock_guard lock(mutex_);
    return active_;
}

void TrayManager::update_tooltip(const Metrics& metrics, const std::string& profile_name) {
    std::lock_guard lock(mutex_);

    if (!active_) {
        return;
    }

    std::string tooltip = format_tooltip(metrics, profile_name);
    (void)platform_.update_tray_tooltip(handle_, tooltip);  // Best-effort update
}

void TrayManager::on_toggle(ToggleCallback callback) {
    std::lock_guard lock(mutex_);
    toggle_callback_ = std::move(callback);
}

void TrayManager::on_show(ShowCallback callback) {
    std::lock_guard lock(mutex_);
    show_callback_ = std::move(callback);
}

void TrayManager::on_restart(RestartCallback callback) {
    std::lock_guard lock(mutex_);
    restart_callback_ = std::move(callback);
}

void TrayManager::on_quit(QuitCallback callback) {
    std::lock_guard lock(mutex_);
    quit_callback_ = std::move(callback);
}

auto TrayManager::format_tooltip(const Metrics& metrics, const std::string& profile_name) -> std::string {
    std::ostringstream ss;

    // TDP (CPU package power)
    if (metrics.cpu.package_watts.has_value()) {
        ss << static_cast<int>(metrics.cpu.package_watts.value()) << "W";
    } else {
        ss << "?W";
    }

    ss << " | ";

    // CPU temperature
    if (metrics.cpu.temp_c.has_value()) {
        ss << metrics.cpu.temp_c.value() << "°C";
    } else {
        ss << "?°C";
    }

    // Active profile (if any)
    if (!profile_name.empty()) {
        ss << " | " << profile_name;
    }

    return ss.str();
}
