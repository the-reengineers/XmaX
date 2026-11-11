// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 the-reengineers
#include "sio_ec.h"

#include "../../logger.h"
#include <atomic>
#include <cstdio>
#include <filesystem>
#include <string>

// The oxp_ec kernel module exposes sysfs files at /sys/kernel/oxp_ec/:
//
//   charge_limit  — write-only (75-100) → IT87 SIO EC RAM write to 0x04A3
//   cpu_temp      — read-only           → IT87 SIO EC RAM read  from 0x0470
//
// The module bypasses the ACPI EC command layer (which silently discards
// charge-limit writes on the ONEXPLAYER SUPER X) and runs in kernel space
// so that outb() works even under Secure Boot lockdown.
//
// This C++ shim probes for the sysfs files at startup and reads/writes
// values through them.  When the module is absent, available() returns
// false and the caller falls back to the standard (but limited) ACPI EC path.

namespace xmax_sio {
namespace {

constexpr const char* kChargeLimitSysfs = "/sys/kernel/oxp_ec/charge_limit";
constexpr const char* kCpuTempSysfs     = "/sys/kernel/oxp_ec/cpu_temp";

std::atomic<bool> g_available{false};

}  // namespace

bool init() {
    if (g_available.load()) return true;

    if (std::filesystem::exists(kChargeLimitSysfs)) {
        log_info("[hw] SIO: oxp_ec kernel module detected — "
                 "EC RAM access available");
        g_available.store(true);
        return true;
    }

    log_info("[hw] SIO: oxp_ec module not loaded "
             "(no " + std::string(kChargeLimitSysfs) + ") — "
             "EC RAM access unavailable; ACPI EC path will be used");
    return false;
}

bool available() {
    // Re-check on every call in case the module was loaded after startup.
    if (!g_available.load()) {
        if (std::filesystem::exists(kChargeLimitSysfs)) {
            log_info("[hw] SIO: oxp_ec module appeared at runtime");
            g_available.store(true);
            return true;
        }
        return false;
    }
    return true;
}

std::optional<uint8_t> ecram_read(uint16_t addr) {
    if (!available()) {
        return std::nullopt;
    }

    // The kernel module currently exposes only register 0x0470 (cpu_temp)
    // for reading.  Per-register sysfs files keep the module simple.
    if (addr == 0x0470) {
        FILE* f = fopen(kCpuTempSysfs, "r");
        if (!f) {
            log_info("[hw] SIO: failed to open "
                     + std::string(kCpuTempSysfs));
            return std::nullopt;
        }

        int val = -1;
        if (fscanf(f, "%d", &val) != 1 || val < 0 || val > 255) {
            log_info("[hw] SIO: failed to parse cpu_temp");
            fclose(f);
            return std::nullopt;
        }

        fclose(f);
        return static_cast<uint8_t>(val);
    }

    log_info("[hw] SIO: unsupported EC read address 0x"
             + std::to_string(addr) + " — only 0x0470 is wired");
    return std::nullopt;
}

bool ecram_write(uint16_t addr, uint8_t value) {
    if (!available()) {
        return false;
    }

    // The kernel module currently only supports register 0x04A3 (charge limit).
    if (addr != 0x04A3) {
        log_info("[hw] SIO: unsupported EC write address 0x"
                 + std::to_string(addr) + " — only 0x04A3 is wired");
        return false;
    }

    FILE* f = fopen(kChargeLimitSysfs, "w");
    if (!f) {
        log_info("[hw] SIO: failed to open "
                 + std::string(kChargeLimitSysfs));
        return false;
    }

    if (fprintf(f, "%d\n", static_cast<int>(value)) < 0) {
        log_info("[hw] SIO: write to " + std::string(kChargeLimitSysfs)
                 + " failed");
        fclose(f);
        return false;
    }

    fclose(f);
    return true;
}

}  // namespace xmax_sio