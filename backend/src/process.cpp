#include "process.h"

ProcessManager::ProcessManager(Platform& platform)
    : platform_(platform)
{
}

ProcessManager::~ProcessManager() {
    stop_monitor();
}

auto ProcessManager::spawn(const std::filesystem::path& exe_path, bool debug) -> Result<void> {
    auto result = platform_.spawn_frontend(exe_path, debug);
    if (!result) {
        return std::unexpected(result.error());
    }

    std::lock_guard lock(mutex_);
    exe_path_ = exe_path;
    debug_ = debug;
    child_ = result.value();
    has_child_ = true;

    return {};
}

auto ProcessManager::show_window(bool visible) -> Result<void> {
    std::lock_guard lock(mutex_);
    // Even without a child process, try to find window by title (platform fallback)
    ChildProcess dummy{};
    auto& target = has_child_ ? child_ : dummy;
    return platform_.show_window(target, visible);
}

void ProcessManager::start_monitor() {
    if (monitoring_.load()) {
        return;
    }

    running_.store(true);
    monitoring_.store(true);
    monitor_thread_ = std::thread(&ProcessManager::monitor_loop, this);
}

void ProcessManager::stop_monitor() {
    if (!monitoring_.load()) {
        return;
    }

    running_.store(false);

    // Terminate child to unblock wait_for_process
    {
        std::lock_guard lock(mutex_);
        if (has_child_) {
            platform_.terminate_process(child_);
            has_child_ = false;
        }
    }

    if (monitor_thread_.joinable()) {
        monitor_thread_.join();
    }

    monitoring_.store(false);
}

auto ProcessManager::is_running() const -> bool {
    std::lock_guard lock(mutex_);
    return has_child_;
}

auto ProcessManager::is_monitoring() const -> bool {
    return monitoring_.load();
}

auto ProcessManager::child() const -> ChildProcess {
    std::lock_guard lock(mutex_);
    return child_;
}

void ProcessManager::on_crash(CrashCallback callback) {
    std::lock_guard lock(mutex_);
    crash_callback_ = std::move(callback);
}

void ProcessManager::terminate() {
    std::lock_guard lock(mutex_);
    if (has_child_) {
        platform_.terminate_process(child_);
        has_child_ = false;
    }
}

void ProcessManager::monitor_loop() {
    while (running_.load()) {
        ChildProcess current_child;
        {
            std::lock_guard lock(mutex_);
            if (!has_child_) {
                break;
            }
            current_child = child_;
        }

        // Wait for process to exit (blocks until exit or handle closed)
        auto result = platform_.wait_for_process(current_child);

        if (!running_.load()) {
            break;  // Stopping cleanly
        }

        if (result) {
            int exit_code = result.value();

            // Unexpected exit -- call crash callback
            {
                std::lock_guard lock(mutex_);
                has_child_ = false;
                if (crash_callback_) {
                    crash_callback_(exit_code);
                }
            }

            // Wait before respawning
            std::this_thread::sleep_for(RESPAWN_DELAY);

            if (!running_.load()) {
                break;
            }

            // Respawn
            std::filesystem::path path;
            {
                std::lock_guard lock(mutex_);
                path = exe_path_;
            }

            auto spawn_result = platform_.spawn_frontend(path, debug_);
            if (spawn_result) {
                std::lock_guard lock(mutex_);
                child_ = spawn_result.value();
                has_child_ = true;
            }
        } else {
            // Error waiting -- process handle may be invalid
            std::lock_guard lock(mutex_);
            has_child_ = false;
            break;
        }
    }
}
