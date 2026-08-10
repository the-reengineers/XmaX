#include "fan.h"

#include <algorithm>
#include <cmath>

FanController::FanController(Platform& platform)
    : platform_(platform)
{
}

auto FanController::set_mode(FanState::Mode mode) -> Result<void> {
    std::lock_guard lock(mutex_);

    // Write mode to EC
    uint8_t ec_mode = (mode == FanState::Mode::Auto) ? EC_MODE_AUTO : EC_MODE_MANUAL;
    auto result = platform_.ec_write(EC_FAN_MODE, ec_mode);
    if (!result) {
        return result;
    }

    mode_ = mode;
    return {};
}

auto FanController::mode() const -> FanState::Mode {
    std::lock_guard lock(mutex_);
    return mode_;
}

void FanController::set_curve(std::optional<FanCurve> curve) {
    std::lock_guard lock(mutex_);
    active_curve_ = std::move(curve);
}

auto FanController::active_curve() const -> std::optional<FanCurve> {
    std::lock_guard lock(mutex_);
    return active_curve_;
}

void FanController::tick(std::optional<int> cpu_temp, std::optional<int> gpu_temp) {
    std::lock_guard lock(mutex_);

    // No-op in Auto mode -- BIOS controls the fan
    if (mode_ != FanState::Mode::Curve) {
        return;
    }

    // Need a curve to interpolate
    if (!active_curve_.has_value()) {
        return;
    }

    // Temperature source: max(cpu_temp, gpu_temp)
    // If either is unavailable, use the other. If both unavailable, skip.
    std::optional<int> temp;
    if (cpu_temp.has_value() && gpu_temp.has_value()) {
        temp = std::max(cpu_temp.value(), gpu_temp.value());
    } else if (cpu_temp.has_value()) {
        temp = cpu_temp;
    } else if (gpu_temp.has_value()) {
        temp = gpu_temp;
    } else {
        return;  // No temperature data available
    }

    // Interpolate speed from curve
    int speed_pct = interpolate_fan_speed(active_curve_.value(), temp.value());
    last_speed_pct_ = static_cast<double>(speed_pct);

    // Convert to duty cycle and write to EC
    uint8_t duty = pct_to_duty(last_speed_pct_);
    (void)platform_.ec_write(EC_FAN_DUTY, duty);  // Ignore errors -- retry on next tick
}

auto FanController::read_state() -> FanState {
    FanState state;

    // Read mode from EC
    auto mode_result = platform_.ec_read(EC_FAN_MODE);
    if (mode_result) {
        state.mode = (mode_result.value() == EC_MODE_AUTO)
            ? FanState::Mode::Auto
            : FanState::Mode::Curve;
    }

    // Read duty cycle from EC
    auto duty_result = platform_.ec_read(EC_FAN_DUTY);
    if (duty_result) {
        state.speed_pct = duty_to_pct(duty_result.value());
    }

    // Read RPM from EC
    auto rpm_result = read_rpm();
    if (rpm_result) {
        state.rpm = rpm_result.value();
    }

    return state;
}

auto FanController::read_rpm() -> Result<uint16_t> {
    auto hi = platform_.ec_read(EC_FAN_RPM_HI);
    if (!hi) {
        return std::unexpected(hi.error());
    }

    auto lo = platform_.ec_read(EC_FAN_RPM_LO);
    if (!lo) {
        return std::unexpected(lo.error());
    }

    uint16_t rpm = (static_cast<uint16_t>(hi.value()) << 8) | static_cast<uint16_t>(lo.value());
    return rpm;
}

auto FanController::last_speed_pct() const -> double {
    std::lock_guard lock(mutex_);
    return last_speed_pct_;
}

auto FanController::pct_to_duty(double pct) -> uint8_t {
    // Clamp to [0, 100]
    pct = std::clamp(pct, 0.0, 100.0);
    // Convert to [0, 255]
    return static_cast<uint8_t>(std::round(pct * 255.0 / 100.0));
}

auto FanController::duty_to_pct(uint8_t duty) -> double {
    // Convert [0, 255] to [0, 100]
    return static_cast<double>(duty) * 100.0 / 255.0;
}
