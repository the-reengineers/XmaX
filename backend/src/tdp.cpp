#include "tdp.h"

TdpController::TdpController(Platform& platform)
    : platform_(platform)
{
}

auto TdpController::read_tdp() -> Result<TdpState> {
    std::lock_guard lock(mutex_);

    TdpState state;

    // Read STAPM limit via safe blob handler (returns milliwatts)
    // WARNING: Do NOT use smu_send for reads - SET opcodes with arg=0 will set TDP to 0mW
    auto stapm_result = platform_.smu_read_safe(SMU_MSG_READ_STAPM);
    if (stapm_result) {
        state.stapm_w = stapm_result.value() / 1000;  // Convert mW to W
    }

    // Read Fast limit via safe blob handler
    auto fast_result = platform_.smu_read_safe(SMU_MSG_READ_FAST);
    if (fast_result) {
        state.fast_w = fast_result.value() / 1000;  // Convert mW to W
    }

    // Read Slow limit via safe blob handler
    auto slow_result = platform_.smu_read_safe(SMU_MSG_READ_SLOW);
    if (slow_result) {
        state.slow_w = slow_result.value() / 1000;  // Convert mW to W
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

    // Convert watts to milliwatts (SMU expects mW)
    uint32_t stapm_mW = stapm_w * 1000;
    uint32_t fast_mW = fast_w * 1000;
    uint32_t slow_mW = slow_w * 1000;

    // Write STAPM limit to SMU via MP1 mailbox (HC-style)
    auto stapm_result = platform_.smu_send(SMU_MSG_WRITE_STAPM, stapm_mW);
    if (!stapm_result) {
        return std::unexpected(stapm_result.error());
    }

    // Write Fast limit to SMU via MP1 mailbox
    auto fast_result = platform_.smu_send(SMU_MSG_WRITE_FAST, fast_mW);
    if (!fast_result) {
        return std::unexpected(fast_result.error());
    }

    // Write Slow limit to SMU via MP1 mailbox
    auto slow_result = platform_.smu_send(SMU_MSG_WRITE_SLOW, slow_mW);
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
