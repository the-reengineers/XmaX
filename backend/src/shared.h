// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 the-reengineers
#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <vector>

// Platform-neutral shared types
// No HANDLE, DWORD, HWND, pid_t, uid_t, or any OS-specific types

struct CpuMetrics {
    double util_pct = 0.0;
    uint32_t clock_mhz = 0;

    // CPU temperature — platform-dependent:
    //   Linux:   nullopt (AMD has no CPU-core-only sensor; use apu.hotspot_c instead)
    //   Windows: EC register 0x0470 via vendor WMI (provenance unconfirmed)
    std::optional<int> temp_c;
    std::optional<double> package_watts;
    // SmartShift: -100 (all CPU) to +100 (all GPU), 0 = balanced
    std::optional<int> smart_shift;
    // PowerDistribution: APU power shift value and limit
    std::optional<int> apu_shift_value;
    std::optional<int> apu_shift_limit;
};

struct GpuMetrics {
    double util_pct = 0.0;
    uint32_t clock_mhz = 0;
    // GPU edge temperature (the canonical GPU temp):
    //   Linux:   amdgpu hwmon temp1 (edge)
    //   Windows: ADLX GPUTemperature() (edge)
    std::optional<int> temp_c;
    // GPU hotspot/junction temperature:
    //   Linux:   nullopt (amdgpu on Strix Halo doesn't expose junction via hwmon)
    //   Windows: ADLX GPUHotspotTemperature()
    std::optional<int> temp_hotspot_c;
    std::optional<double> power_w;
    std::optional<uint64_t> vram_used_bytes;
    std::optional<uint64_t> vram_total_bytes;
};

// ApuMetrics — die-wide / package-level metrics for the full APU (CPU+GPU+I/O die).
// Only populated on platforms that expose a die-wide thermal sensor.
struct ApuMetrics {
    // Die-wide hotspot temperature (the hottest junction across the entire APU die):
    //   Linux:   k10temp Tctl (the primary thermal control temperature)
    //   Windows: nullopt (k10temp is a Linux kernel driver; no equivalent exists)
    std::optional<int> temp_hotspot_c;
};

struct RamMetrics {
    uint64_t used_bytes = 0;
    uint64_t total_bytes = 0;
    uint64_t avail_bytes = 0;
    double load_pct = 0.0;
};

struct FanState {
    enum class Mode { Auto, Manual, Curve };
    Mode mode = Mode::Auto;
    double speed_pct = 0.0;  // 0-100
    uint16_t rpm = 0;
};

struct TdpState {
    std::optional<uint32_t> stapm_w;
    std::optional<uint32_t> fast_w;
    std::optional<uint32_t> slow_w;
};

struct PowerState {
    enum class Source { Battery, UsbCSlow, UsbCFast, DcIn, Unknown };
    Source mode = Source::Unknown;
    std::string label;
    std::optional<int> battery_pct;
    std::optional<int> charge_limit_pct;
};

// UMA (Unified Memory Architecture) option for Variable Graphics Memory
// Represents a preset for RAM/VRAM split on APUs
struct UmaOption {
    enum class Mode { Auto, Custom };
    std::string id;             // Unique identifier: "<mode>:<memory_carved_gb>"
    std::string name;           // Human-readable name (e.g. "Auto", "Custom")
    Mode mode = Mode::Auto;     // Auto or Custom mode
    double memory_carved_gb = 0.0;   // System RAM dedicated to VRAM (GB)
    double memory_remaining_gb = 0.0; // Remaining system RAM (GB)
};

// Frost Bay cooling-device sensor snapshot.
// All read-only sensor fields come from the device's BLE status buffer (≥41 bytes,
// parsed at fixed offsets). Fields with defaults are writable
// via read-modify-write; the defaults are zero/inactive.
struct FrostBayState {
    bool connected = false;

    // ---- Read-only sensors ----
    int mode = 0;                    // 0=Off, 254=IntelliFan, 255=FixedSpeed
    uint16_t fan_speed_rpm = 0;      // raw fan RPM (big-endian on the wire)
    uint16_t pump_speed_rpm = 0;     // raw pump RPM
    uint16_t water_flow = 0;         // flow-rate sensor
    int in_water_temp_c = 0;         // inlet coolant temperature (°C)
    int out_water_temp_c = 0;        // outlet coolant temperature (°C)
    int status_flag = 0;             // bitfield: 1=running, 2/64=fault
    int fw_version = 0;              // firmware version

    // ---- Writable fields (last-known values, default to zero/inactive) ----
    int fan_speed_percent = 0;       // only honored in mode 255 (Fixed Speed)
    int pump_speed_percent = 0;      // pump duty % (honored in modes 254 and 255)
    int rgb_mode = 0;                // 254 = static color (r/g/b below);
                                     // 2-14 = presets (`xmaxctl cooling help`)
    bool rgb_enable = false;         // RGB on/off
    int rgb_level = 0;               // brightness step 0-4 (4 = max)
    int rgb_r = 0, rgb_g = 0, rgb_b = 0;  // static color (mode 254)

    // ---- 9-point IntelliFan curve (only active in mode 254) ----
    // Pairs stored as (temp_c, fan_pct). The 3 factory curves share
    // t1..t9 = 30,32,33,34,35,36,37,38,40 with different fan arrays.
    struct CurvePoint { int temp_c; int speed_pct; };
    std::vector<CurvePoint> curve;

    // ---- Derived ----
    bool is_running = false;  // pumpSpeed>1 && statusFlag not in {2,64}
};

struct Metrics {
    CpuMetrics cpu;
    GpuMetrics gpu;
    ApuMetrics apu;
    RamMetrics ram;
    FanState fan;
    PowerState power;
    FrostBayState cooling;
    int64_t ts = 0;  // Unix timestamp
};

// Error codes (single source of truth in errors.json, code-generated)
enum class ErrorCode : uint32_t {
    TdpOutOfRange = 1001,
    FanSpeedInvalid = 1002,
    ChargeLimitInvalid = 1003,
    FanCurveInvalid = 1004,
    UnknownCommand = 2001,
    ParseError = 2002,
    HardwareBusy = 3001,
    SensorUnavailable = 3002,
    ChargeLimitWriteFail = 3003,
    ProfileNotFound = 4001,
    FanCurveNotFound = 4003,
    FanCurveInUse = 4004,
    ProfileInUse = 4005,
    PersistDisabled = 4006,
    BuiltinProtected = 4008
};

std::string error_code_to_string(ErrorCode code);

// Hardcoded maximum TDP per power state (not user-configurable).
// These represent the absolute maximum TDP the hardware can draw from each power source.
inline int power_state_max_tdp(PowerState::Source source) {
    switch (source) {
        case PowerState::Source::Battery:  return 55;   // Battery: 55W
        case PowerState::Source::UsbCSlow: return 20;   // USB-C 65W: 20W
        case PowerState::Source::UsbCFast: return 55;   // USB-C 100W: 55W
        case PowerState::Source::DcIn:     return 80;   // DC-In: 80W (120W with Frost Bay cooling)
        default:                           return 55;   // Safe default for Unknown
    }
}

// Runtime TDP ceiling: the hardware max for the power source, raised to 120W
// on DC-In while the Frost Bay external cooler is connected. Profiles of BOTH
// types may be saved with TDP values up to 120W for DC-In; every apply path
// must clamp through this ceiling (adaptive profiles via
// AdaptiveController::set_power_state_ceiling, fixed profiles at each
// write_tdp call site).
inline int runtime_tdp_ceiling(PowerState::Source source, bool frost_bay_connected) {
    if (source == PowerState::Source::DcIn && frost_bay_connected) {
        return 120;
    }
    return power_state_max_tdp(source);
}
