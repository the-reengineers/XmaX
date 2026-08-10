#include "button.h"

ButtonMonitor::ButtonMonitor(Platform& platform)
    : platform_(platform)
{
}

ButtonMonitor::~ButtonMonitor() {
    stop();
}

void ButtonMonitor::start() {
    if (running_.load()) {
        return;
    }

    running_.store(true);
    monitor_thread_ = std::thread(&ButtonMonitor::monitor_loop, this);
}

void ButtonMonitor::stop() {
    if (!running_.load()) {
        return;
    }

    running_.store(false);
    if (monitor_thread_.joinable()) {
        monitor_thread_.join();
    }
}

auto ButtonMonitor::is_running() const -> bool {
    return running_.load();
}

auto ButtonMonitor::poll() -> bool {
    auto result = platform_.ec_read(EC_BUTTON);
    if (!result) {
        return false;
    }

    uint8_t current = result.value();

    std::lock_guard lock(mutex_);

    // First poll: establish baseline, no press detected
    if (!last_button_state_.has_value()) {
        last_button_state_ = current;
        return false;
    }

    // Edge detection: any state change means a press happened
    if (current != last_button_state_.value()) {
        last_button_state_ = current;

        // Toggle visibility
        fe_visible_ = !fe_visible_;
        bool visible = fe_visible_;

        // Call callback outside the lock to prevent deadlock
        // (callback may call toggle_visibility or other methods)
        if (visibility_callback_) {
            visibility_callback_(visible);
        }

        return true;
    }

    return false;
}

auto ButtonMonitor::is_visible() const -> bool {
    std::lock_guard lock(mutex_);
    return fe_visible_;
}

void ButtonMonitor::toggle_visibility() {
    bool visible;
    VisibilityCallback cb;

    {
        std::lock_guard lock(mutex_);
        fe_visible_ = !fe_visible_;
        visible = fe_visible_;
        cb = visibility_callback_;
    }

    // Call callback outside the lock
    if (cb) {
        cb(visible);
    }
}

void ButtonMonitor::set_visible(bool visible) {
    std::lock_guard lock(mutex_);
    fe_visible_ = visible;
}

void ButtonMonitor::on_visibility_change(VisibilityCallback callback) {
    std::lock_guard lock(mutex_);
    visibility_callback_ = std::move(callback);
}

void ButtonMonitor::init_app_fun_en() {
    // Enable the button function register
    // Write 0x01 to EC_APP_FUN_EN to enable button detection
    auto result = platform_.ec_write(EC_APP_FUN_EN, 0x01);
    (void)result;  // Best-effort -- button may work even if init fails
}

void ButtonMonitor::monitor_loop() {
    while (running_.load()) {
        poll();
        std::this_thread::sleep_for(POLL_INTERVAL);
    }
}
