#pragma once

#include "shared.h"
#include "fan.h"
#include "tdp.h"
#include "platform/platform.h"

#include <atomic>
#include <chrono>
#include <mutex>
#include <thread>

// MetricsPoller -- background thread that polls all sensors and updates shared Metrics struct.
//
// Polls at 2000ms intervals:
//   - CPU: utilization, clock, temperature, package power
//   - GPU: utilization, clock, temperature, power, VRAM
//   - RAM: used, total, available, load
//   - Fan: mode, speed, RPM
//   - Power: source, battery %, charge limit
//
// Thread safety: get_metrics() is safe to call from any thread.
// The poller runs in a dedicated background thread.

class MetricsPoller {
public:
    MetricsPoller(Platform& platform, FanController& fan_ctrl, TdpController& tdp_ctrl);
    ~MetricsPoller();

    // Start polling in background thread.
    void start();

    // Stop polling and join thread.
    void stop();

    // Get current metrics snapshot (thread-safe).
    auto get_metrics() -> Metrics;

    // Check if poller is running.
    auto is_running() const -> bool;

private:
    // Main polling loop (runs in background thread).
    void poll_loop();

    // Poll individual sensor groups.
    void poll_cpu();
    void poll_gpu();
    void poll_ram();
    void poll_fan();
    void poll_power();

    Platform& platform_;
    FanController& fan_ctrl_;
    TdpController& tdp_ctrl_;

    std::thread poll_thread_;
    std::atomic<bool> running_{false};

    mutable std::mutex metrics_mutex_;
    Metrics current_metrics_;

    // Polling interval
    static constexpr std::chrono::milliseconds POLL_INTERVAL{2000};
};
