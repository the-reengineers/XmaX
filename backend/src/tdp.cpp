#include "tdp.h"

TdpController::TdpController(Platform& platform)
    : platform_(platform)
{
}

auto TdpController::read_tdp() -> Result<TdpState> {
    std::lock_guard lock(mutex_);

    TdpState state;

    // Read STAPM limit from SMU
    auto stapm_result = platform_.smu_send(SMU_MSG_READ_STAPM, 0);
    if (stapm_result) {
        state.stapm_w = stapm_result.value();
    }

    // Read Fast limit from SMU
    auto fast_result = platform_.smu_send(SMU_MSG_READ_FAST, 0);
    if (fast_result) {
        state.fast_w = fast_result.value();
    }

    // Read Slow limit from SMU
    auto slow_result = platform_.smu_send(SMU_MSG_READ_SLOW, 0);
    if (slow_result) {
        state.slow_w = slow_result.value();
    }

    last_state_ = state;
    return state;
}

auto TdpController::write_tdp(uint32_t stapm_w, uint32_t fast_w, uint32_t slow_w) -> Result<void> {
    // Validate all values first
    if (!validate_tdp(stapm_w)) {
        return std::unexpected(ErrorCode::TdpOutOfRange);
    }
    if (!validate_tdp(fast_w)) {
        return std::unexpected(ErrorCode::TdpOutOfRange);
    }
    if (!validate_tdp(slow_w)) {
        return std::unexpected(ErrorCode::TdpOutOfRange);
    }

    std::lock_guard lock(mutex_);

    // Write STAPM limit to SMU
    // TODO: Implement dual-dispatch write sequence if required by hardware
    // (some AMD SMUs require a prepare/unlock command before the actual write)
    auto stapm_result = platform_.smu_send(SMU_MSG_WRITE_STAPM, stapm_w);
    if (!stapm_result) {
        return std::unexpected(stapm_result.error());
    }

    // Write Fast limit to SMU
    auto fast_result = platform_.smu_send(SMU_MSG_WRITE_FAST, fast_w);
    if (!fast_result) {
        return std::unexpected(fast_result.error());
    }

    // Write Slow limit to SMU
    auto slow_result = platform_.smu_send(SMU_MSG_WRITE_SLOW, slow_w);
    if (!slow_result) {
        return std::unexpected(slow_result.error());
    }

    // Update last known state
    last_state_.stapm_w = stapm_w;
    last_state_.fast_w = fast_w;
    last_state_.slow_w = slow_w;

    return {};
}

auto TdpController::validate_tdp(uint32_t value) -> bool {
    return value >= TDP_MIN_W && value <= TDP_MAX_W;
}

auto TdpController::last_state() const -> TdpState {
    std::lock_guard lock(mutex_);
    return last_state_;
}
