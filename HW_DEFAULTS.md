# Hardware Defaults Comparison

This document compares our app's hard-coded hardware defaults with those used by OneXConsole.

## TDP Defaults by Power State

### Our App (XmaX)

| Power State | Default TDP | Source |
|-------------|-------------|--------|
| Battery | 25W | `config.cpp:16` |
| USB-C Slow (65W class) | 35W | `config.cpp:17` |
| USB-C Fast (100W class) | 45W | `config.cpp:18` |
| DC-In | 55W | `config.cpp:19` |

### OneXConsole

| Mode | Source | Max TDP | Max Boost TDP |
|------|--------|---------|---------------|
| 500 | Battery (low) | 20W | 20W |
| 501 | Battery (overheat) | 20W | 20W |
| 1 | Battery (normal) | 55W | 70W |
| 2/3 | USB-C 100W | 55W | 70W |
| 4/5 | DC-In | 80W | 100W |
| 8/9 | USB-C 65W | 20W | 20W |
| cooling | DC-In + water cooling | 120W | 140W |

*Source: `D:\Dev\Misc\onexconsole\HARDWARE.md` and `ANALYSIS.md`*

### Comparison

| Power State | Our Default | OneXConsole | Difference |
|-------------|-------------|-------------|------------|
| Battery | 25W | 55W (normal) / 20W (low) | +30W / -5W |
| USB-C Slow (65W) | 35W | 20W | -15W |
| USB-C Fast (100W) | 45W | 55W | +10W |
| DC-In | 55W | 80W | +25W |

**Key Observations:**
- OneXConsole uses significantly higher TDP limits for Battery (55W vs 25W) and DC-In (80W vs 55W)
- OneXConsole distinguishes between battery states: normal (55W) vs low/overheat (20W)
- OneXConsole has separate "Max TDP" and "Max Boost TDP" values — we only have one value per state
- OneXConsole detects water cooling mode (120W/140W) — we don't detect this
- Our USB-C Slow default (35W) is higher than OneXConsole's 65W class limit (20W)

## Other Hardware Defaults

### Auto-Tune Settings

| Setting | Default | Source |
|---------|---------|--------|
| TDP Max | 55W | `config.h:15` |
| Target Temperature | 85°C | `config.h:14` |
| Fan Max | 100% | `config.h:16` |

### Charge Limit

| Setting | Default | Source |
|---------|---------|--------|
| Charge Limit | 100% | `config.h:42` |

### TDP Validation Range

| Setting | Value | Source |
|---------|-------|--------|
| Minimum TDP | 6W | `tdp.h:38` |
| Maximum TDP | 120W | `tdp.h:39` |

### Home Layout

| Setting | Default | Source |
|---------|---------|--------|
| Columns | 3 | `config.h:34` |

## Missing Defaults

The following settable hardware values do **not** have configurable defaults:

| Setting | Current Behavior | Issue |
|---------|------------------|-------|
| Fan mode | BIOS default (Auto) | Not in config, only FanController constructor |
| Fan curve | None | No default curve defined |
| Profile per power state | Empty slug ("") | No profile applied by default |
| Auto-tune enabled | Disabled (`std::nullopt`) | Not enabled by default |

**Impact:** If a user never configures settings:
- TDP: App applies per-power-state defaults ✅
- Charge limit: App applies 100% ✅
- Fan: BIOS controls (Auto mode) ⚠️
- Fan curve: Not applied ❌
- Profile: No profile loaded ⚠️
- Auto-tune: Disabled ❌

## EC Registers

### Known Registers

| Register | Address | Purpose |
|----------|---------|---------|
| EC_POWER_STATE | 0x04FE | Power supply mode (1=Battery, 8/9=USB-C 65W, 2/3=USB-C 100W, 4/5/0x85=DC-In) |
| EC_CHARGE_LIMIT | 0x04A3 | Battery charge limit percentage (75-100) |
| EC_FAN_MODE | 0x044A | Fan mode (0=Auto, 1=Manual) |
| EC_FAN_DUTY | 0x044B | Fan duty cycle (0-255) |
| EC_FAN_RPM_HI | 0x0476 | Fan RPM high byte |
| EC_FAN_RPM_LO | 0x0477 | Fan RPM low byte |
| EC_CPU_TEMP | 0x0470 | CPU temperature |
| EC_BUTTON | 0x0230 | Button state |
| EC_APP_FUN_EN | 0x0231 | App function enable |

### OneXConsole TDP EC Registers (Undocumented in our app)

| Register | Address | Purpose | DC-In | Battery | USB-C 65W | USB-C 100W |
|----------|---------|---------|-------|---------|-----------|------------|
| TDP profile index | 0x0430 | Profile selector | 5 | 1 | 0 | varies |
| maxTdp | 0x0431 | Max TDP (W) | 120 | 55 | ~30 | varies |
| maxBoostTdp | 0x0432 | Max Boost TDP (W) | 140 | 70 | ~40 | varies |
| slowTdp | 0x0433 | Slow TDP (W) | 120 | 60 | ~30 | varies |

*Source: `D:\Dev\Misc\onexconsole\ANALYSIS.md`*

These registers provide BIOS-set TDP limits that dynamically adjust based on charger wattage.

## SMU Mailbox

TDP values are read/written via SMU mailbox, not EC registers:

| Operation | Message ID | Notes |
|-----------|------------|-------|
| Read STAPM | 0x00 (placeholder) | Sustained TDP limit |
| Read Fast | 0x00 (placeholder) | Fast boost limit (~10s) |
| Read Slow | 0x00 (placeholder) | Slow boost limit (~30s) |
| Write STAPM | 0x00 (placeholder) | Write via MP1 SMU |
| Write Fast | 0x00 (placeholder) | Write via MP1 SMU |
| Write Slow | 0x00 (placeholder) | Write via MP1 SMU |

**Note:** SMU message IDs are placeholders. Real opcodes require hardware documentation and UXTU-style dual-dispatch writes (MP1 + RSMU).

*Source: `D:\Dev\Misc\onexconsole\HARDWARE.md` Section 3*

## Recommendations

1. **Update TDP defaults** to match OneXConsole values for consistency
2. **Add boost TDP support** — separate max TDP and max boost TDP per power state
3. **Add battery state detection** — distinguish normal vs low/overheat battery
4. **Add water cooling detection** — enable 120W/140W mode when detected
5. **Add missing defaults** — fan mode, default fan curve, default profile
6. **Read BIOS TDP defaults** from EC registers 0x0431-0x0433 instead of hard-coding
7. **Document EC registers** — add 0x0430-0x0433 to our register map

## References

- OneXConsole hardware analysis: `D:\Dev\Misc\onexconsole\HARDWARE.md`
- OneXConsole implementation: `D:\Dev\Misc\onexconsole\ANALYSIS.md`
- Our config defaults: `backend/src/config.h`, `backend/src/config.cpp`
- Our EC registers: `backend/src/power.h`, `backend/src/fan.h`
- Our SMU interface: `backend/src/tdp.h`
