#include "adaptive.h"

#include <algorithm>
#include <cmath>

AdaptiveController::AdaptiveController(TdpController& tdp, FanController& fan)
    : tdp_ctrl_(tdp)
    , fan_ctrl_(fan)
    , last_sensor_time_(std::chrono::steady_clock::now())
{
}

AdaptiveController::~AdaptiveController() {
    stop();
}

void AdaptiveController::start() {
    if (running_.load()) {
        return;
    }

    running_.store(true);
    control_thread_ = std::thread(&AdaptiveController::control_loop, this);
}

void AdaptiveController::stop() {
    if (!running_.load()) {
        return;
    }

    running_.store(false);
    if (control_thread_.joinable()) {
        control_thread_.join();
    }
}

void AdaptiveController::activate(TuningPreset preset, int target_temp_c, int tdp_max_w, int fan_max_pct) {
    std::lock_guard lock(mutex_);

    config_.active = true;
    config_.tuning = preset;
    config_.target_temp_c = target_temp_c;
    config_.tdp_max_w = tdp_max_w;
    config_.fan_max_pct = fan_max_pct;

    // Reset PID state
    integral_ = 0.0;
    prev_error_ = 0.0;
    smoothed_temp_ = 0.0;
    temp_initialized_ = false;
    current_tdp_ = tdp_max_w;
    current_fan_ = 0;

    active_.store(true);
}

void AdaptiveController::deactivate() {
    std::lock_guard lock(mutex_);
    config_.active = false;
    active_.store(false);
}

auto AdaptiveController::is_active() const -> bool {
    return active_.load();
}

auto AdaptiveController::config() const -> AdaptiveConfig {
    std::lock_guard lock(mutex_);
    return config_;
}

void AdaptiveController::set_power_state_ceiling(int tdp_max_w) {
    std::lock_guard lock(mutex_);
    power_state_ceiling_ = tdp_max_w;
}

auto AdaptiveController::effective_tdp_max() const -> int {
    std::lock_guard lock(mutex_);
    return std::min(config_.tdp_max_w, power_state_ceiling_);
}

void AdaptiveController::tick(int cpu_temp, int gpu_temp) {
    if (!active_.load()) {
        return;
    }

    std::lock_guard lock(mutex_);

    // Use max of CPU and GPU temp
    int temp = std::max(cpu_temp, gpu_temp);

    // Check for sensor failure
    auto now = std::chrono::steady_clock::now();
    if (temp <= 0) {
        if (now - last_sensor_time_ > SENSOR_TIMEOUT) {
            sensor_failure_ = true;
            // Thermal safety fallback
            emit_adjust(TDP_MIN, 100, 0, effective_tdp_max_locked(), "sensor_failure");
            (void)tdp_ctrl_.write_tdp(TDP_MIN, TDP_MIN, TDP_MIN);  // Ignore errors -- retry on next tick
            (void)fan_ctrl_.set_mode(FanState::Mode::Curve);
            // Set fan to 100%
            FanCurve emergency_curve;
            emergency_curve.points = {{0, 100}, {100, 100}};
            fan_ctrl_.set_curve(emergency_curve);
            return;
        }
    } else {
        last_sensor_time_ = now;
        sensor_failure_ = false;
    }

    // Safety override for critical temperature
    if (temp >= CRITICAL_TEMP) {
        apply_safety_override(temp);
        return;
    }

    // Get preset parameters
    auto params = get_preset_params(config_.tuning);

    // Asymmetric smoothing
    // Initialize with first reading to avoid starting from 0
    if (!temp_initialized_) {
        smoothed_temp_ = static_cast<double>(temp);
        temp_initialized_ = true;
    } else {
        double alpha = (temp > smoothed_temp_) ? params.alpha_rise : params.alpha_fall;
        smoothed_temp_ = alpha * temp + (1.0 - alpha) * smoothed_temp_;
    }

    int smoothed_temp_int = static_cast<int>(std::round(smoothed_temp_));

    // PID control for fan
    double error = smoothed_temp_ - config_.target_temp_c;
    integral_ += error;
    integral_ = std::clamp(integral_, -INTEGRAL_CLAMP, INTEGRAL_CLAMP);

    double derivative = error - prev_error_;
    prev_error_ = error;

    double fan_output = params.kp * error + params.ki * integral_ + params.kd * derivative;

    // Clamp fan to preset range and config max
    int fan_min = params.fan_min;
    int fan_max = std::min(params.fan_max, config_.fan_max_pct);
    int fan_pct = static_cast<int>(std::clamp(fan_output, static_cast<double>(fan_min), static_cast<double>(fan_max)));

    // Outer loop: TDP adjustment
    int effective_max = effective_tdp_max_locked();
    int new_tdp = current_tdp_;

    // Ramp down if fan is saturated AND temp > target
    if (fan_pct >= FAN_SATURATION_THRESHOLD && smoothed_temp_ > config_.target_temp_c) {
        new_tdp = std::max(TDP_MIN, current_tdp_ - params.tdp_ramp_down_rate);
    }
    // Ramp up if fan is low AND temp < target
    else if (fan_pct < FAN_LOW_THRESHOLD && smoothed_temp_ < config_.target_temp_c) {
        new_tdp = std::min(effective_max, current_tdp_ + params.tdp_ramp_up_rate);
    }

    // Clamp TDP
    new_tdp = std::clamp(new_tdp, TDP_MIN, effective_max);

    // Apply changes if they changed
    bool changed = (new_tdp != current_tdp_ || fan_pct != current_fan_);

    current_tdp_ = new_tdp;
    current_fan_ = fan_pct;

    // Write to hardware
    (void)fan_ctrl_.set_mode(FanState::Mode::Curve);  // Ignore errors -- retry on next tick
    FanCurve curve;
    curve.points = {{0, fan_pct}, {100, fan_pct}};  // Constant speed
    fan_ctrl_.set_curve(curve);

    // Tick fan controller to apply the curve (writes to EC)
    fan_ctrl_.tick(cpu_temp, gpu_temp);

    (void)tdp_ctrl_.write_tdp(new_tdp, new_tdp, new_tdp);  // Ignore errors -- retry on next tick

    if (changed) {
        emit_adjust(new_tdp, fan_pct, smoothed_temp_int, effective_max, "pid_adjust");
    }
}

void AdaptiveController::on_adjust(AdjustCallback callback) {
    std::lock_guard lock(mutex_);
    adjust_callback_ = std::move(callback);
}

auto AdaptiveController::last_tdp_w() const -> int {
    std::lock_guard lock(mutex_);
    return current_tdp_;
}

auto AdaptiveController::last_fan_pct() const -> int {
    std::lock_guard lock(mutex_);
    return current_fan_;
}

auto AdaptiveController::last_smoothed_temp() const -> int {
    std::lock_guard lock(mutex_);
    return static_cast<int>(std::round(smoothed_temp_));
}

auto AdaptiveController::get_preset_params(TuningPreset preset) -> PresetParams {
    switch (preset) {
        case TuningPreset::Silent:
            return {
                .kp = 2.0,
                .ki = 0.1,
                .kd = 0.5,
                .fan_min = 20,
                .fan_max = 40,  // Hard ceiling
                .tdp_ramp_down_rate = 2,
                .tdp_ramp_up_rate = 1,
                .alpha_rise = 0.5,
                .alpha_fall = 0.05
            };

        case TuningPreset::Default:
            return {
                .kp = 3.0,
                .ki = 0.15,
                .kd = 0.8,
                .fan_min = 25,
                .fan_max = 80,
                .tdp_ramp_down_rate = 3,
                .tdp_ramp_up_rate = 2,
                .alpha_rise = 0.5,
                .alpha_fall = 0.05
            };

        case TuningPreset::Performance:
            return {
                .kp = 4.0,
                .ki = 0.2,
                .kd = 1.0,
                .fan_min = 30,
                .fan_max = 100,
                .tdp_ramp_down_rate = 4,
                .tdp_ramp_up_rate = 3,
                .alpha_rise = 0.5,
                .alpha_fall = 0.05
            };
    }

    // Default fallback
    return get_preset_params(TuningPreset::Default);
}

void AdaptiveController::control_loop() {
    while (running_.load()) {
        // In a real implementation, this would poll temperatures and call tick()
        // For now, the tick() method is called externally (e.g., from MetricsPoller)
        std::this_thread::sleep_for(TICK_INTERVAL);
    }
}

void AdaptiveController::apply_safety_override(int temp) {
    // Critical temperature: max fan, min TDP immediately
    emit_adjust(TDP_MIN, 100, temp, effective_tdp_max_locked(), "critical_temp");

    (void)tdp_ctrl_.write_tdp(TDP_MIN, TDP_MIN, TDP_MIN);  // Ignore errors
    (void)fan_ctrl_.set_mode(FanState::Mode::Curve);
    FanCurve emergency_curve;
    emergency_curve.points = {{0, 100}, {100, 100}};
    fan_ctrl_.set_curve(emergency_curve);

    current_tdp_ = TDP_MIN;
    current_fan_ = 100;
}

void AdaptiveController::emit_adjust(int tdp_w, int fan_pct, int smoothed_temp, int effective_max, const std::string& reason) {
    if (adjust_callback_) {
        adjust_callback_(tdp_w, fan_pct, smoothed_temp, effective_max, reason);
    }
}
