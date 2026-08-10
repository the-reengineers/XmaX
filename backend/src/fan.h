#pragma once

#include "shared.h"
#include "profiles.h"
#include "platform/platform.h"

#include <mutex>
#include <optional>

// FanController -- manages fan mode (auto/curve), curve interpolation, and EC register I/O.
//
// Modes:
//   Auto  -- BIOS controls the fan. No EC writes for speed.
//   Curve -- Backend runs the interpolation loop. Each tick() computes speed from
//           the active curve and writes it to the EC.
//
// Thread safety: all public methods are safe to call from any thread.
// The tick() method is designed to be called at 1s intervals from a background thread.

class FanController {
public:
    explicit FanController(Platform& platform);

    // Set fan mode. In Curve mode, a curve must be set via set_curve() before
    // tick() will produce speed updates.
    auto set_mode(FanState::Mode mode) -> Result<void>;

    // Get current fan mode.
    auto mode() const -> FanState::Mode;

    // Set the active fan curve for curve mode.
    void set_curve(std::optional<FanCurve> curve);

    // Get the active fan curve (nullopt if none set).
    auto active_curve() const -> std::optional<FanCurve>;

    // Tick -- called at 1s intervals. Computes fan speed from the active curve
    // using max(cpu_temp, gpu_temp) as the temperature source.
    // Writes the new speed to EC if in Curve mode and a curve is active.
    // No-op in Auto mode.
    void tick(std::optional<int> cpu_temp, std::optional<int> gpu_temp);

    // Read current fan state from EC registers.
    // Returns the hardware state (mode, duty cycle, RPM).
    auto read_state() -> FanState;

    // Read fan RPM from EC registers.
    auto read_rpm() -> Result<uint16_t>;

    // Get last computed speed percentage (from most recent tick).
    auto last_speed_pct() const -> double;

private:
    // EC register addresses
    static constexpr uint16_t EC_FAN_MODE   = 0x044A;  // 0 = auto, non-zero = manual
    static constexpr uint16_t EC_FAN_DUTY   = 0x044B;  // 0-255 duty cycle
    static constexpr uint16_t EC_FAN_RPM_HI = 0x0476;  // RPM high byte
    static constexpr uint16_t EC_FAN_RPM_LO = 0x0477;  // RPM low byte

    // EC mode values
    static constexpr uint8_t EC_MODE_AUTO   = 0x00;
    static constexpr uint8_t EC_MODE_MANUAL = 0x01;

    // Convert speed percentage (0-100) to EC duty cycle (0-255)
    static auto pct_to_duty(double pct) -> uint8_t;

    // Convert EC duty cycle (0-255) to speed percentage (0-100)
    static auto duty_to_pct(uint8_t duty) -> double;

    Platform& platform_;
    mutable std::mutex mutex_;
    FanState::Mode mode_ = FanState::Mode::Auto;
    std::optional<FanCurve> active_curve_;
    double last_speed_pct_ = 0.0;
};
