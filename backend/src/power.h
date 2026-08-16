#pragma once

#include "shared.h"
#include "config.h"
#include "profiles.h"
#include "platform/platform.h"

#include <functional>
#include <mutex>
#include <optional>

// PowerController -- detects power source changes and manages charge limit.
//
// Responsibilities:
//   - Poll EC register 0x04FE for power state (battery, USB-C, DC-In)
//   - Track power state changes (edge detection)
//   - Read/write charge limit via EC 0x04A3 and Super I/O
//   - Trigger auto-profile switching on power state change
//
// Auto-profile switching:
//   When power state changes and persist=true:
//   - Find profile assigned to new power state and apply it
//   - Set adaptive TDP ceiling from hardcoded power state max
//   - Emit power_mode_change event to frontend
//
// Thread safety: all public methods are safe to call from any thread.

class PowerController {
public:
    // Callback for power state changes
    // Parameters: new state, old state
    using StateChangeCallback = std::function<void(PowerState::Source, PowerState::Source)>;

    explicit PowerController(Platform& platform);

    // Read current power state from EC register 0x04FE.
    auto read_power_state() -> PowerState::Source;

    // Get the last known power state.
    auto current_state() const -> PowerState::Source;

    // Check and update power state. Returns true if state changed.
    // Calls the state change callback if registered and state changed.
    auto update_power_state() -> bool;

    // Register a callback for power state changes.
    void on_state_change(StateChangeCallback callback);

    // Read charge limit from EC register 0x04A3.
    auto read_charge_limit() -> Result<uint8_t>;

    // Write charge limit via Super I/O (PawnIO + LpcIO.bin).
    // Value must be in range [75, 100].
    auto write_charge_limit(uint8_t percent) -> Result<void>;

    // Validate charge limit value.
    static auto validate_charge_limit(uint8_t percent) -> bool;

    // Get last known charge limit.
    auto last_charge_limit() const -> std::optional<uint8_t>;

private:
    // EC register addresses
    static constexpr uint16_t EC_POWER_STATE   = 0x04FE;
    static constexpr uint16_t EC_CHARGE_LIMIT  = 0x04A3;

    // Power state decode values
    static constexpr uint8_t POWER_STATE_BATTERY     = 1;
    static constexpr uint8_t POWER_STATE_USB_C_SLOW  = 8;
    static constexpr uint8_t POWER_STATE_USB_C_SLOW2 = 9;
    static constexpr uint8_t POWER_STATE_USB_C_FAST  = 2;
    static constexpr uint8_t POWER_STATE_USB_C_FAST2 = 3;
    static constexpr uint8_t POWER_STATE_DC_IN       = 4;
    static constexpr uint8_t POWER_STATE_DC_IN2      = 5;
    static constexpr uint8_t POWER_STATE_DC_IN3      = 0x85;

    // Charge limit range
    static constexpr uint8_t CHARGE_LIMIT_MIN = 75;
    static constexpr uint8_t CHARGE_LIMIT_MAX = 100;

    // Decode EC register value to power state enum
    static auto decode_power_state(uint8_t ec_value) -> PowerState::Source;

    Platform& platform_;
    mutable std::mutex mutex_;
    PowerState::Source current_state_ = PowerState::Source::Unknown;
    std::optional<uint8_t> last_charge_limit_;
    StateChangeCallback state_change_callback_;
};
