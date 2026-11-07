# Backend TODOs and Placeholders

## Reference Implementation

All command IDs and sequences are documented in:
`D:\Dev\Misc\onexconsole\TEST\tdp_test.cpp`

---

## Completed Items

### 1. SMU Command IDs - COMPLETED
**File:** `backend/src/tdp.h`

**After (real values from tdp_test.cpp):**
```cpp
// MP1 opcodes (Strix Halo)
static constexpr uint32_t SMU_MSG_READ_STAPM  = 0x14;
static constexpr uint32_t SMU_MSG_READ_FAST   = 0x15;
static constexpr uint32_t SMU_MSG_READ_SLOW   = 0x16;
static constexpr uint32_t SMU_MSG_WRITE_STAPM = 0x14;
static constexpr uint32_t SMU_MSG_WRITE_FAST  = 0x15;
static constexpr uint32_t SMU_MSG_WRITE_SLOW  = 0x16;

// RSMU opcodes for dual-dispatch (UXTU-style)
static constexpr uint32_t RSMU_SET_STAPM = 0x31;
static constexpr uint32_t RSMU_SET_FAST  = 0x32;
static constexpr uint32_t RSMU_SET_SLOW  = 0x33;
```

---

### 2. TDP Write Implementation - COMPLETED
**File:** `backend/src/tdp.cpp`

**Changes:**
- Added milliwatts conversion (watts × 1000) before sending to SMU
- Values sent via MP1 mailbox (HC-style)

---

### 3. Safe SMU Read Method - COMPLETED
**Files:** `backend/src/platform/platform.h`, `backend/src/platform/platform_win32.cpp`, `backend/tests/test_main.cpp`

**Added:** `smu_read_safe()` method that uses `ioctl_send_smu_command` (blob's internal PM table handler) for safe reads.

**Why:** Mailbox sends are write-only on Strix Halo - using SET opcodes with arg=0 will SET TDP to 0mW.

---

### 4. TDP Read Implementation - COMPLETED
**File:** `backend/src/tdp.cpp`

**Changes:**
- Changed from `smu_send()` to `smu_read_safe()` for safe reads
- Added milliwatts to watts conversion (results / 1000)

---

### 5. IPC Stub Comments - CLEANED UP
**File:** `backend/src/platform/platform_win32.cpp`

**Changes:** Updated misleading "placeholder" comments to clarify these are unused stubs.

---

## Remaining TODOs

### 1. CPU Clock Metric - COMPLETED
**Files:** `backend/src/platform/platform.h`, `backend/src/platform/platform_win32.cpp`, `backend/src/metrics.cpp`, `backend/tests/test_main.cpp`
**Reference:** `D:\Dev\Misc\onexconsole\TEST\driverfree_test.cpp` lines 284-307

**Implementation:**
- Added `cpu_clock_mhz()` method to Platform interface
- Implemented via WMI `ROOT\CIMV2` query: `SELECT CurrentClockSpeed FROM Win32_Processor`
- Driver-free, uses standard Windows APIs
- Returns 0 if unavailable

**Status:** ✅ COMPLETED

---

### 2. CPU Package Power Metric - NOT YET WIRED (Windows blocked; Linux path exists)
**File:** `backend/src/metrics.cpp` (`poll_cpu`, inline TODO), `backend/src/platform/linux/ryzenadj.cpp`

`Metrics.cpu.package_watts` is never assigned on either platform — it stays `nullopt`. The inline TODO in `poll_cpu` now documents the per-platform situation.

**Windows — no driver-free method (all tested):**

| Source | Method | Status |
|--------|--------|--------|
| EC Registers | 0x0431-0x0433 contain TDP **LIMITS** (not actual power) | ❌ Not power draw |
| PawnIO blob | `ioctl_send_smu_command` reads PM table but command ID unknown | ❌ No reference |
| ryzenadj.dll | `get_stapm_value()`, `get_fast_value()`, `get_slow_value()` | ❌ Requires driver |
| WMI BatteryStatus | `DischargeRate` shows battery discharge, not CPU power | ❌ Not CPU power |
| **DPTC commands** | `SendDptcCmdRead` - **TESTED: All commands return 0** | ❌ Not implemented by OEM |

**DPTC Test Results (dptc_test.ps1 run as admin):**
```
Cmd 0x14 (STAPM limit): 0x00000000
Cmd 0x15 (PPT FAST):    0x00000000
Cmd 0x16 (PPT SLOW):    0x00000000
Cmd 0x20 (Current power): 0x00000000
```
The DPTC interface exists but returns no data - OEM ACPI doesn't implement it for Strix Halo.

**Conclusion (Windows):** CPU package power can only be read via kernel driver (ryzenadj.dll or equivalent). No driver-free method works.

**Linux — achievable, not yet wired:**
libryzenadj exposes `get_cpu_power` (actual package power draw, in mW — distinct from the STAPM/Fast/Slow *limit* getters already bound). XmaX's `dlopen` binding (`backend/src/platform/linux/ryzenadj.cpp`) currently resolves only `init_ryzenadj` + `set/get_stapm|fast|slow_limit`; loading `get_cpu_power` (and converting mW → W) would populate `package_watts` wherever the daemon runs as root with `libryzenadj.so` available. This is the one metric where Linux can do strictly better than the Windows driver-free path.

**Reference files checked (Windows investigation):**
- `driverfree_test.cpp` - No CPU power implementation
- `tdp_test.cpp` - Only TDP limits, not power draw
- `power_scan.ps1` - EC 0x0431-0x0433 are limits, not actual power
- `dptc_test.ps1` - **TESTED**: All commands return 0
- `readjustService.ps1` - Uses ryzenadj (requires kernel driver)

**TODO:** extend `ryzenadj.{h,cpp}` to resolve `get_cpu_power`, wire it into `poll_cpu()` behind `#ifndef _WIN32`.

---

### 6. SmartShift / PowerDistribution Metrics via ADLX - COMPLETED
**Files:** `backend/src/platform/platform.h`, `backend/src/platform/platform_win32.cpp`, `backend/src/shared.h`, `backend/src/metrics.cpp`, `backend/tests/test_main.cpp`

**Changes:**
- Added `SystemTelemetry` struct with SmartShift and PowerDistribution fields
- Added `system_metrics()` virtual method to Platform interface
- Implemented via ADLX `IADLXSystemMetrics` (SmartShift) and `IADLXSystemMetrics1` (PowerDistribution)
- Added `smart_shift`, `apu_shift_value`, `apu_shift_limit` to `CpuMetrics`
- Wired into `poll_cpu()` in metrics.cpp

**Metrics provided:**
- `smart_shift`: -100 (all CPU) to +100 (all GPU), 0 = balanced
- `apu_shift_value` / `gpu_shift_value`: Current power shift values
- `apu_shift_limit` / `gpu_shift_limit` / `total_shift_limit`: Power shift limits

---

## Additional MP1/RSMU Opcodes (for future features)

**MP1 opcodes:**
```cpp
CMD_SET_STAPM_TIME        = 0x18;
CMD_SET_TCTL_TEMP         = 0x19;
CMD_SET_VRM_CURRENT       = 0x1A;
CMD_SET_VRMSOC_CURRENT    = 0x1B;
CMD_SET_VRMMAX_CURRENT    = 0x1C;
CMD_SET_VRMSOCMAX_CURRENT = 0x1D;
CMD_SET_GPU_PSM           = 0x1F;
CMD_SET_CO_PER            = 0x4B;
CMD_SET_CO_ALL            = 0x4C;
CMD_SET_CHTC_TEMP         = 0x63;
CMD_GET_GPU_PSM           = 0xD7;
CMD_SET_GFX_CLK           = 0x89;
```

**RSMU opcodes:**
```cpp
RSMU_SET_SLOW_TIME        = 0x35;
RSMU_SET_STAPM_TIME       = 0x36;
RSMU_SET_CHTC_TEMP        = 0x37;
RSMU_SET_VRM_CURRENT      = 0x38;
RSMU_SET_VRMSOC_CURRENT   = 0x39;
RSMU_SET_VRMMAX_CURRENT   = 0x3A;
RSMU_SET_VRMSOCMAX_CURRENT = 0x3B;
```

---

## Summary

| Item | Status | Notes |
|------|--------|-------|
| SMU command IDs | **COMPLETED** | Critical fix - was causing hardware_busy |
| TDP write (mW conversion) | **COMPLETED** | Values now sent in milliwatts |
| Safe SMU read | **COMPLETED** | Added smu_read_safe() method |
| TDP read | **COMPLETED** | Uses safe read, converts mW→W |
| IPC stub comments | **CLEANED UP** | Clarified as unused stubs |
| SmartShift/PowerDistribution | **COMPLETED** | Via ADLX IADLXSystemMetrics1 |
| CPU clock metric | **COMPLETED** | Via WMI Win32_Processor.CurrentClockSpeed |
| Battery percentage | **COMPLETED** | Via WMI Win32_Battery.EstimatedChargeRemaining |
| CPU package power | ❌ NOT WIRED | Windows: no driver-free method (DPTC/LHM return 0 on Strix Halo). Linux: ryzenadj `get_cpu_power` path exists but isn't bound yet. |

---

## Build Verification

Backend compiles successfully with all changes (warnings only, no errors).
Backend test suite: **176/176 pass on Linux** (Fedora 44 / GCC 16, including the
17 fixture-driven `Procfs*` parsers and the 3 `SetTdp*` transport tests added
with the Linux transport/D-Bus work). The Windows test target builds clean but
its runtime count is not tracked in this document.
