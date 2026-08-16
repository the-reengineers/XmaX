#pragma once

#include <cstdint>
#include <optional>
#include <string>

// Platform-neutral shared types
// No HANDLE, DWORD, HWND, pid_t, uid_t, or any OS-specific types

struct CpuMetrics {
    double util_pct = 0.0;
    uint32_t clock_mhz = 0;
    std::optional<int> temp_c;
    std::optional<double> package_watts;
};

struct GpuMetrics {
    double util_pct = 0.0;
    uint32_t clock_mhz = 0;
    std::optional<int> temp_c;
    std::optional<double> power_w;
    std::optional<uint32_t> vram_used_mb;
    std::optional<uint32_t> vram_total_mb;
};

struct RamMetrics {
    double used_gb = 0.0;
    double total_gb = 0.0;
    double avail_gb = 0.0;
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

struct Metrics {
    CpuMetrics cpu;
    GpuMetrics gpu;
    RamMetrics ram;
    FanState fan;
    PowerState power;
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
        case PowerState::Source::DcIn:     return 80;   // DC-In: 80W
        default:                           return 55;   // Safe default for Unknown
    }
}
