#pragma once

#include <cstdint>
#include <optional>

// oxpec.h — OneXPlayer vendor EC registers via the in-tree oxpec driver.
//
// The vendor registers the backend uses (fan mode 0x044A, fan duty 0x044B,
// fan RPM 0x0476/77, charge limit 0x04A3) sit above the 256-byte ACPI EC
// window that the ec_sys/Super-I/O backends (ec.h) can reach. The mainline
// "oxpec" kernel driver (hwmon chip name "oxp_ec", plus a power_supply
// extension attached to the ACPI battery) exposes exactly those registers
// through standard sysfs:
//
//   /sys/class/hwmon/<chip>/pwm1            fan duty, 0-255   (EC 0x4B)
//   /sys/class/hwmon/<chip>/pwm1_enable     fan mode          (EC 0x4A),
//     TRANSLATED by the driver, not raw:
//       read:  2 = auto (EC 0x00), 1 = manual, 0 = manual at full speed
//       write: 1 = manual (EC 0x01), 2 = auto (EC 0x00), 0 = manual + 255
//   /sys/class/hwmon/<chip>/fan1_input      fan RPM, 16-bit   (EC 0x76/77)
//   /sys/class/power_supply/<psy>/charge_control_end_threshold
//                                             charge limit, %  (EC 0xA3)
//
// This shim maps XmaX's EC register numbers onto those nodes so the generic
// FanController and charge-limit code work unchanged on Linux. Registers the
// shim does not know about fall through to the ec_sys backend. Nothing here
// is a build-time dependency: when the driver is absent, read() returns
// nullopt and write() returns false.
namespace xmax_oxp {

// Locate the sysfs nodes once at startup (idempotent; call from the
// platform's init_hardware()).
void init();

// True iff the oxpec hwmon chip was found.
bool available();

// Read a vendor register (0x044A/0x044B/0x0476/0x0477/0x04A3).
// nullopt = driver absent or read failed.
std::optional<uint8_t> read(uint16_t reg);

// Write a vendor register. false = driver absent or write failed.
bool write(uint16_t reg, uint8_t value);

}  // namespace xmax_oxp
