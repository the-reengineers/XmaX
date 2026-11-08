// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 the-reengineers
#include "sio_ec.h"

#include "../../logger.h"
#include <atomic>
#include <cstdio>
#include <filesystem>
#include <string>

// The xmax_sio_ec kernel module exposes a write-only sysfs file at
// /sys/kernel/xmax_sio_ec/charge_limit.  Writing a value (75-100) to that
// file triggers an IT87 Super I/O EC RAM write to register 0x04A3, bypassing
// the ACPI EC command layer that silently discards charge-limit writes on the
// ONEXPLAYER SUPER X (and possibly other Strix Halo handhelds).
//
// The userspace counterpart (direct ioperm/outb) is blocked by kernel lockdown
// when Secure Boot is on — the kernel module solves that by running in kernel
// space where outb() is unrestricted.  It also avoids contention with other
// SIO users (e.g. the hwmon/it87 driver) because those use the same port
// region, and the kernel serialises port I/O access.
//
// This C++ shim probes for the sysfs file at startup and writes charge-limit
// values through it.  When the module is absent, available() returns false and
// the caller falls back to the standard (but likely ignored) ACPI EC write.

namespace xmax_sio {
namespace {

constexpr const char* kChargeLimitSysfs = "/sys/kernel/xmax_sio_ec/charge_limit";

std::atomic<bool> g_available{false};

}  // namespace

bool init() {
    if (g_available.load()) return true;

    if (std::filesystem::exists(kChargeLimitSysfs)) {
        log_info("[hw] SIO: xmax_sio_ec kernel module detected — "
                 "charge-limit bypass available");
        g_available.store(true);
        return true;
    }

    log_info("[hw] SIO: xmax_sio_ec module not loaded "
             "(no " + std::string(kChargeLimitSysfs) + ") — "
             "charge-limit bypass unavailable; ACPI EC path will be used");
    return false;
}

bool available() {
    // Re-check on every call in case the module was loaded after startup.
    if (!g_available.load()) {
        if (std::filesystem::exists(kChargeLimitSysfs)) {
            log_info("[hw] SIO: xmax_sio_ec module appeared at runtime");
            g_available.store(true);
            return true;
        }
        return false;
    }
    return true;
}

bool ecram_write(uint16_t addr, uint8_t value) {
    if (!available()) {
        return false;
    }

    // The kernel module currently only supports register 0x04A3 (charge limit).
    // When additional registers are needed, the module's sysfs interface must
    // be extended to accept an address parameter.
    if (addr != 0x04A3) {
        log_info("[hw] SIO: unsupported EC address 0x"
                 + std::to_string(addr) + " — only 0x04A3 is wired");
        return false;
    }

    // Open / write / close — the kernel module's store callback does the
    // IT87 config entry, EC RAM write, and exit atomically.
    FILE* f = fopen(kChargeLimitSysfs, "w");
    if (!f) {
        log_info("[hw] SIO: failed to open " + std::string(kChargeLimitSysfs));
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