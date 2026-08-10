#pragma once

#include "platform/platform.h"

#include <atomic>
#include <chrono>
#include <filesystem>
#include <functional>
#include <mutex>
#include <thread>

// ProcessManager -- manages frontend process lifecycle.
//
// Responsibilities:
//   - Spawn frontend process via Platform
//   - Show/hide frontend window
//   - Monitor frontend process in background thread
//   - Respawn frontend on unexpected exit (after 1s delay)
//   - Terminate frontend on shutdown
//
// Job Object (Windows):
//   Platform::spawn_frontend creates a Job Object with KILL_ON_JOB_CLOSE.
//   Both backend and frontend are assigned to the job. When the backend exits,
//   the OS automatically terminates the frontend -- no explicit cleanup needed.
//
// Thread safety: all public methods are safe to call from any thread.

class ProcessManager {
public:
    // Callback for unexpected frontend exit.
    // Parameter: exit code
    using CrashCallback = std::function<void(int)>;

    explicit ProcessManager(Platform& platform);
    ~ProcessManager();

    // Spawn frontend process.
    // Stores the exe path for respawn.
    auto spawn(const std::filesystem::path& exe_path) -> Result<void>;

    // Show or hide frontend window.
    auto show_window(bool visible) -> Result<void>;

    // Start monitoring frontend process (background thread).
    // Calls wait_for_process and respawn on unexpected exit.
    void start_monitor();

    // Stop monitoring and terminate frontend.
    void stop_monitor();

    // Check if frontend process is running.
    auto is_running() const -> bool;

    // Check if monitor thread is running.
    auto is_monitoring() const -> bool;

    // Get child process info.
    auto child() const -> ChildProcess;

    // Register callback for unexpected frontend exit.
    void on_crash(CrashCallback callback);

    // Terminate frontend process immediately.
    void terminate();

private:
    void monitor_loop();

    // Delay before respawning after crash
    static constexpr std::chrono::seconds RESPAWN_DELAY{1};

    Platform& platform_;

    mutable std::mutex mutex_;
    std::filesystem::path exe_path_;
    ChildProcess child_;
    bool has_child_ = false;

    // Monitor thread
    std::thread monitor_thread_;
    std::atomic<bool> running_{false};       // Backend is running
    std::atomic<bool> monitoring_{false};     // Monitor thread is active

    CrashCallback crash_callback_;
};
