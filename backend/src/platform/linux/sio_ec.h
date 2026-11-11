// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 the-reengineers
#pragma once

#include <cstdint>
#include <optional>

namespace xmax_sio {

// Initialise: check whether the oxp_ec kernel module is loaded by looking
// for its sysfs interface (/sys/kernel/oxp_ec/{charge_limit,cpu_temp}).
// Returns true when the module is present and the EC RAM read/write path
// is available.
bool init();

// Whether init() succeeded (the kernel module is loaded).
bool available();

// Read a byte directly from EC RAM through the oxp_ec kernel module's
// sysfs interface.  The module does the IT87 Super I/O indexed-register
// protocol from kernel space.
//
// addr: 16-bit EC RAM address (e.g. 0x0470 for CPU temperature).
//
// Returns the byte value on success, or std::nullopt when the module is
// absent or the read fails.
std::optional<uint8_t> ecram_read(uint16_t addr);

// Write a byte directly to EC RAM through the oxp_ec kernel module's
// sysfs interface.  The module does the IT87 Super I/O indexed-register
// protocol from kernel space, bypassing both kernel lockdown (Secure Boot)
// and the ACPI EC command layer that silently discards charge-limit writes.
//
// addr: 16-bit EC RAM address (e.g. 0x04A3 for charge limit).
// value: byte to write.
//
// Returns true when the write was dispatched to the kernel module.
bool ecram_write(uint16_t addr, uint8_t value);

}  // namespace xmax_sio