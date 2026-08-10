#include "power.h"

PowerController::PowerController(Platform& platform)
    : platform_(platform)
{
}

auto PowerController::read_power_state() -> PowerState::Source {
    auto result = platform_.ec_read(EC_POWER_STATE);
    if (!result) {
        return PowerState::Source::Unknown;
    }

    return decode_power_state(result.value());
}

auto PowerController::current_state() const -> PowerState::Source {
    std::lock_guard lock(mutex_);
    return current_state_;
}

auto PowerController::update_power_state() -> bool {
    auto new_state = read_power_state();

    std::lock_guard lock(mutex_);

    if (new_state == current_state_) {
        return false;  // No change
    }

    auto old_state = current_state_;
    current_state_ = new_state;

    // Call callback if registered
    if (state_change_callback_) {
        state_change_callback_(new_state, old_state);
    }

    return true;
}

void PowerController::on_state_change(StateChangeCallback callback) {
    std::lock_guard lock(mutex_);
    state_change_callback_ = std::move(callback);
}

auto PowerController::read_charge_limit() -> Result<uint8_t> {
    auto result = platform_.ec_read(EC_CHARGE_LIMIT);
    if (!result) {
        return std::unexpected(result.error());
    }

    uint8_t percent = result.value();

    std::lock_guard lock(mutex_);
    last_charge_limit_ = percent;

    return percent;
}

auto PowerController::write_charge_limit(uint8_t percent) -> Result<void> {
    if (!validate_charge_limit(percent)) {
        return std::unexpected(ErrorCode::ChargeLimitInvalid);
    }

    // Write via Super I/O path (PawnIO + LpcIO.bin)
    // This is handled by Platform::charge_limit_write
    auto result = platform_.charge_limit_write(percent);
    if (!result) {
        return result;
    }

    std::lock_guard lock(mutex_);
    last_charge_limit_ = percent;

    return {};
}

auto PowerController::validate_charge_limit(uint8_t percent) -> bool {
    return percent >= CHARGE_LIMIT_MIN && percent <= CHARGE_LIMIT_MAX;
}

auto PowerController::last_charge_limit() const -> std::optional<uint8_t> {
    std::lock_guard lock(mutex_);
    return last_charge_limit_;
}

auto PowerController::decode_power_state(uint8_t ec_value) -> PowerState::Source {
    switch (ec_value) {
        case POWER_STATE_BATTERY:
            return PowerState::Source::Battery;

        case POWER_STATE_USB_C_SLOW:
        case POWER_STATE_USB_C_SLOW2:
            return PowerState::Source::UsbCSlow;

        case POWER_STATE_USB_C_FAST:
        case POWER_STATE_USB_C_FAST2:
            return PowerState::Source::UsbCFast;

        case POWER_STATE_DC_IN:
        case POWER_STATE_DC_IN2:
        case POWER_STATE_DC_IN3:
            return PowerState::Source::DcIn;

        default:
            return PowerState::Source::Unknown;
    }
}
