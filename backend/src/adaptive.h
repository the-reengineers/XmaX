#pragma once

#include "tdp.h"
#include "fan.h"
#include "shared.h"

#include <atomic>
#include <chrono>
#include <functional>
#include <mutex>
#include <thread>

// Adaptive controller -- PID-based dual-loop control for dynamic TDP and fan adjustment.
//
// Algorithm:
//   Inner loop: Fan PID controller maintains target temperature
//   Outer loop: TDP adjustment when fan runs out of headroom
//
// Tuning presets:
//   Silent      -- Hard fan ceiling, TDP is primary control
//   Default     -- Balanced fan and TDP
//   Performance -- Maximize TDP, allow high fan
//
// Features:
//   - Asymmetric temperature smoothing (fast rise, slow decay)
//   - Power state TDP ceiling clamping
//   - Safety overrides (critical temp, sensor failure)
//   - Mutual exclusivity with profiles (activating adaptive deactivates profile)
//
// Thread safety: all public methods are safe to call from any thread.

enum class TuningPreset {
    Silent,
    Default,
    Performance
};

struct AdaptiveConfig {
    bool active = false;
    TuningPreset tuning = TuningPreset::Default;
    int target_temp_c = 85;
    int tdp_max_w = 55;
    int fan_max_pct = 100;
};

class AdaptiveController {
public:
    // Callback for value changes (emit event to frontend)
    // Parameters: tdp_w, fan_pct, smoothed_temp_c, effective_tdp_max_w, reason
    using AdjustCallback = std::function<void(int, int, int, int, const std::string&)>;

    AdaptiveController(TdpController& tdp, FanController& fan);
    ~AdaptiveController();

    // Start control thread (1s tick rate).
    void start();

    // Stop control thread.
    void stop();

    // Activate adaptive controller with given config.
    void activate(TuningPreset preset, int target_temp_c, int tdp_max_w, int fan_max_pct);

    // Deactivate adaptive controller.
    void deactivate();

    // Check if adaptive is active.
    auto is_active() const -> bool;

    // Get current config.
    auto config() const -> AdaptiveConfig;

    // Set power state TDP ceiling (for clamping).
    void set_power_state_ceiling(int tdp_max_w);

    // Get effective TDP max (min of config and power state ceiling).
    auto effective_tdp_max() const -> int;

    // Tick -- called at 1s intervals with current temperatures.
    void tick(int cpu_temp, int gpu_temp);

    // Register callback for value changes.
    void on_adjust(AdjustCallback callback);

    // Get last computed values.
    auto last_tdp_w() const -> int;
    auto last_fan_pct() const -> int;
    auto last_smoothed_temp() const -> int;

private:
    // Tuning preset parameters
    struct PresetParams {
        double kp, ki, kd;           // PID gains
        int fan_min, fan_max;         // Fan speed range
        int tdp_ramp_down_rate;       // Watts per second to ramp down
        int tdp_ramp_up_rate;         // Watts per second to ramp up
        double alpha_rise, alpha_fall; // Smoothing factors
    };

    static auto get_preset_params(TuningPreset preset) -> PresetParams;

    void control_loop();
    void apply_safety_override(int temp);
    void emit_adjust(int tdp_w, int fan_pct, int smoothed_temp, int effective_max, const std::string& reason);

    // Internal helper -- assumes mutex is already held
    auto effective_tdp_max_locked() const -> int {
        return std::min(config_.tdp_max_w, power_state_ceiling_);
    }

    TdpController& tdp_ctrl_;
    FanController& fan_ctrl_;

    std::thread control_thread_;
    std::atomic<bool> running_{false};
    std::atomic<bool> active_{false};

    mutable std::mutex mutex_;
    AdaptiveConfig config_;

    // PID state
    double integral_ = 0.0;
    double prev_error_ = 0.0;
    double smoothed_temp_ = 0.0;
    bool temp_initialized_ = false;
    int current_tdp_ = 0;
    int current_fan_ = 0;

    // Power state ceiling
    int power_state_ceiling_ = TDP_MAX;

    // Safety tracking
    std::chrono::steady_clock::time_point last_sensor_time_;
    bool sensor_failure_ = false;

    AdjustCallback adjust_callback_;

    // Constants
    static constexpr int TDP_MIN = 6;
    static constexpr int TDP_MAX = 120;
    static constexpr int CRITICAL_TEMP = 95;
    static constexpr int FAN_SATURATION_THRESHOLD = 90;  // %
    static constexpr int FAN_LOW_THRESHOLD = 70;          // %
    static constexpr std::chrono::seconds SENSOR_TIMEOUT{5};
    static constexpr std::chrono::seconds TICK_INTERVAL{1};
    static constexpr double INTEGRAL_CLAMP = 100.0;
};
