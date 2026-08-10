#pragma once

#include "shared.h"
#include "platform/platform.h"

#include <mutex>

// TdpController -- reads and writes TDP limits (STAPM, Fast, Slow) via SMU mailbox.
//
// TDP limits:
//   STAPM -- Skin Temperature Aware Power Management (sustained limit)
//   Fast  -- Fast boost limit (short-term, ~10s)
//   Slow  -- Slow boost limit (medium-term, ~30s)
//
// Typical relationship: STAPM <= Slow <= Fast
//
// Thread safety: all public methods are safe to call from any thread.

class TdpController {
public:
    explicit TdpController(Platform& platform);

    // Read current TDP limits from SMU.
    auto read_tdp() -> Result<TdpState>;

    // Write TDP limits to SMU.
    // All three values must be in range [TDP_MIN_W, TDP_MAX_W].
    auto write_tdp(uint32_t stapm_w, uint32_t fast_w, uint32_t slow_w) -> Result<void>;

    // Validate a single TDP value.
    static auto validate_tdp(uint32_t value) -> bool;

    // Get last known TDP state (from most recent read or write).
    auto last_state() const -> TdpState;

private:
    // TDP limits for Strix Halo (watts)
    static constexpr uint32_t TDP_MIN_W = 6;
    static constexpr uint32_t TDP_MAX_W = 120;

    // SMU message IDs for TDP operations
    // TODO: Fill in actual opcodes from tdp_test.cpp / hardware documentation
    // These are placeholder values -- the real opcodes are hardware-specific
    static constexpr uint32_t SMU_MSG_READ_STAPM  = 0x00;  // Read STAPM limit
    static constexpr uint32_t SMU_MSG_READ_FAST   = 0x00;  // Read Fast limit
    static constexpr uint32_t SMU_MSG_READ_SLOW   = 0x00;  // Read Slow limit
    static constexpr uint32_t SMU_MSG_WRITE_STAPM = 0x00;  // Write STAPM limit
    static constexpr uint32_t SMU_MSG_WRITE_FAST  = 0x00;  // Write Fast limit
    static constexpr uint32_t SMU_MSG_WRITE_SLOW  = 0x00;  // Write Slow limit

    Platform& platform_;
    mutable std::mutex mutex_;
    TdpState last_state_;
};
