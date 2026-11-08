// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 the-reengineers
#include "oxpec.h"

#include <atomic>
#include <filesystem>
#include <fstream>
#include <string>

namespace xmax_oxp {
namespace {

namespace fs = std::filesystem;

// XmaX-wide vendor register numbers (see fan.h / metrics.cpp / power.cpp).
constexpr uint16_t kFanModeReg  = 0x044A;  // 0 = auto, non-zero = manual
constexpr uint16_t kFanDutyReg  = 0x044B;  // 0-255 duty cycle
constexpr uint16_t kRpmHiReg    = 0x0476;  // RPM high byte
constexpr uint16_t kRpmLoReg    = 0x0477;  // RPM low byte
constexpr uint16_t kChargeReg   = 0x04A3;  // charge limit, percent

// Raw EC mode values (fan.h).
constexpr uint8_t kEcModeAuto   = 0x00;
constexpr uint8_t kEcModeManual = 0x01;

std::atomic<bool> g_available{false};
std::string g_hwmon_base;   // .../hwmonX for the "oxp_ec" chip
std::string g_charge_path;  // .../charge_control_end_threshold

std::optional<std::string> read_file(const fs::path& path) {
    std::ifstream file(path);
    if (!file.is_open()) return std::nullopt;
    std::string content;
    std::getline(file, content);
    return content;
}

bool write_file(const fs::path& path, const std::string& content) {
    std::ofstream file(path);
    if (!file.is_open()) return false;
    file << content << '\n';
    return file.good();
}

std::optional<long> read_int(const std::string& base, const char* node) {
    auto text = read_file(fs::path(base) / node);
    if (!text) return std::nullopt;
    try {
        return std::stol(*text);
    } catch (const std::exception&) {
        return std::nullopt;
    }
}

}  // namespace

void init() {
    // Find the hwmon chip created by the oxpec driver. Match on name rather
    // than hardcoding hwmonX — numbering shifts as chips come and go.
    for (const auto& entry : fs::directory_iterator("/sys/class/hwmon")) {
        auto name = read_file(entry.path() / "name");
        if (name && *name == "oxp_ec") {
            g_hwmon_base = entry.path().string();
            break;
        }
    }

    // Find a power_supply exposing charge_control_end_threshold — the oxpec
    // driver attaches its "oxp-charge-control" extension to the ACPI battery
    // (BAT0 here). Match on the attribute, not the name, so other vendors'
    // batteries with the same standard attribute would work too.
    for (const auto& entry : fs::directory_iterator("/sys/class/power_supply")) {
        const auto candidate = entry.path() / "charge_control_end_threshold";
        if (fs::exists(candidate)) {
            g_charge_path = candidate.string();
            break;
        }
    }

    g_available = !g_hwmon_base.empty();
}

bool available() {
    return g_available.load();
}

std::optional<uint8_t> read(uint16_t reg) {
    switch (reg) {
        case kFanModeReg: {
            if (g_hwmon_base.empty()) return std::nullopt;
            auto val = read_int(g_hwmon_base, "pwm1_enable");
            if (!val) return std::nullopt;
            // Reverse the driver's translation: 2 = auto; 1 = manual;
            // 0 = manual at full speed (both manual flavors -> kEcModeManual).
            return (*val == 2) ? kEcModeAuto : kEcModeManual;
        }
        case kFanDutyReg: {
            if (g_hwmon_base.empty()) return std::nullopt;
            auto val = read_int(g_hwmon_base, "pwm1");
            if (!val || *val < 0 || *val > 255) return std::nullopt;
            return static_cast<uint8_t>(*val);
        }
        case kRpmHiReg:
        case kRpmLoReg: {
            if (g_hwmon_base.empty()) return std::nullopt;
            auto val = read_int(g_hwmon_base, "fan1_input");
            if (!val || *val < 0) return std::nullopt;
            auto rpm = static_cast<uint16_t>(*val);
            return (reg == kRpmHiReg) ? static_cast<uint8_t>(rpm >> 8)
                                      : static_cast<uint8_t>(rpm & 0xFF);
        }
        case kChargeReg: {
            if (g_charge_path.empty()) return std::nullopt;
            auto text = read_file(g_charge_path);
            if (!text) return std::nullopt;
            try {
                long val = std::stol(*text);
                if (val < 0 || val > 100) return std::nullopt;
                return static_cast<uint8_t>(val);
            } catch (const std::exception&) {
                return std::nullopt;
            }
        }
        default:
            return std::nullopt;  // Not one of ours -> ec_sys fallback
    }
}

bool write(uint16_t reg, uint8_t value) {
    switch (reg) {
        case kFanModeReg:
            if (g_hwmon_base.empty()) return false;
            // Forward-translate to the driver's values: auto = 2, manual = 1.
            return write_file(fs::path(g_hwmon_base) / "pwm1_enable",
                              (value == kEcModeAuto) ? "2" : "1");
        case kFanDutyReg:
            if (g_hwmon_base.empty()) return false;
            return write_file(fs::path(g_hwmon_base) / "pwm1",
                              std::to_string(value));
        case kChargeReg: {
            if (g_charge_path.empty()) return false;
            // charge_control_end_threshold is a directory path, not base+node.
            return write_file(g_charge_path, std::to_string(value));
        }
        default:
            return false;  // Not one of ours -> ec_sys fallback
    }
}

}  // namespace xmax_oxp
