#pragma once

#include <cstdint>
#include <optional>

// Embedded-controller (EC) byte read/write backend. Two implementations,
// selected once at init() time by a capability probe, in priority order:
//   1. ec_sys (primary): the kernel module's debugfs char node
//      /sys/kernel/debug/ec/ec0/io, addressed with pread/pwrite at the EC
//      register offset (requires root).
//   2. Super-I/O (fallback): raw port I/O on the ACPI EC command/data ports
//      (0x66 command / 0x62 data) via iopl(3) + inb/outb (requires CAP_SYS_RAWIO).
// Neither is a build-time dependency: when unavailable, read()/write() degrade
// to nullopt/false and the Platform layer surfaces SensorUnavailable.
namespace xmax_ec {

// Probe and select a backend (idempotent; call once at startup from the
// platform's init_hardware()).
void init();

// True iff a backend was successfully selected.
bool available();

// Read a byte from EC register `reg`. nullopt = unavailable or read failed.
std::optional<uint8_t> read(uint16_t reg);

// Write a byte to EC register `reg`. false = unavailable or write failed.
bool write(uint16_t reg, uint8_t value);

}  // namespace xmax_ec