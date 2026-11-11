// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 the-reengineers
//
// oxp_ec — kernel module providing a sysfs interface for direct EC RAM
// read/write through the IT87 Super I/O chip (ports 0x4E/0x4F) on the
// ONEXPLAYER SUPER X (Strix Halo).  The standard ACPI EC path silently
// discards writes to battery charge-limit register 0x04A3 and does not
// expose CPU temperature register 0x0470; using the SIO's indexed-register
// protocol bypasses these limitations.
//
// Usage:
//   echo 85 > /sys/kernel/oxp_ec/charge_limit
//   cat /sys/kernel/oxp_ec/cpu_temp       # → raw EC register 0x0470 value (°C)
//
// This module is tiny on purpose: it does exactly what the ACPI EC path
// cannot do, and it runs in the kernel so that Secure Boot lockdown (which
// blocks userspace ioperm/outb) is not an obstacle.

#define pr_fmt(fmt) "oxp_ec: " fmt

#include <linux/init.h>
#include <linux/kernel.h>
#include <linux/kobject.h>
#include <linux/module.h>
#include <linux/mutex.h>
#include <linux/string.h>
#include <linux/sysfs.h>
#include <linux/io.h>
#include <linux/ioport.h>

// ── IT87 Super I/O constants ──────────────────────────────────────────
//
// The IT87 presents two port-pair aliases (0x2E/0x2F and 0x4E/0x4F) that
// access the same register space, but you MUST use the same pair for config
// entry AND all subsequent indexed access — mixing pairs is invalid.  We use
// 0x4E/0x4F because it is the primary address and is less likely to conflict
// with firmware that may use the 0x2E/0x2F alias during POST.
//
// Within the SIO register map, registers 0x2E and 0x2F form the LDN index/data
// window: write a LDN register number to 0x2E, then read/write its value via
// 0x2F.  The LDN registers (0x00-0x2F) are per-logical-device — you must
// select the EC LDN (0x0A) via register 0x07 before accessing registers
// 0x10/0x11/0x12.
#define SIO_INDEX     0x4E
#define SIO_DATA      0x4F
#define SIO_PORT_SPAN 2

// Config entry sequence (strobe to index port).
static const u8 cfg_enter[] = { 0x87, 0x01, 0x55, 0xAA };

// LDN register numbers (accessed through SIO registers 0x2E/0x2F).
#define LDN_EC       0x0A   // logical device: EC
#define LDN_ADDR_HI  0x11   // EC RAM address [15:8]
#define LDN_ADDR_LO  0x10   // EC RAM address [7:0]
#define LDN_DATA     0x12   // EC RAM data read/write

// No chip-ID probe: the vendor-specific 0x87/0x01/0x55/0xAA unlock used here
// does NOT put the chip into standard IT87 config mode — registers 0x20/0x21
// return 0x5571 (an echo of the unlock bytes), not an 0x87XX chip ID.  The
// unlock exposes only the EC-RAM indirect window via 0x2E/0x2F, so there is
// no reliable way to identify the chip from config space.  This module is
// hardware-specific by construction (hardcoded EC addresses for the OXP
// SUPER X); load it only on that device.  request_region still guards against
// other Linux drivers claiming the ports.
//
// Serializes all SIO indexed-register sequences.  The IT87 protocol is
// multi-step (enter → select LDN → addr hi → addr lo → data); interleaving
// two callers corrupts the address latch and can write a byte to an
// arbitrary EC RAM location.  This guards against our own concurrent sysfs
// accesses.  It cannot guard against firmware/AML touching the same ports.
static DEFINE_MUTEX(oxp_ec_lock);

// ── Low-level helpers ─────────────────────────────────────────────────

// Write `val` to a SIO global register `reg` (0x00-0x2F range).
static inline void sio_write(u8 reg, u8 val) {
	outb(reg, SIO_INDEX);
	outb(val, SIO_DATA);
}

// Read from a SIO global register `reg`.
static inline u8 sio_read(u8 reg) {
	outb(reg, SIO_INDEX);
	return inb(SIO_DATA);
}

// Write `val` to LDN register `reg`: select SIO 0x2E=reg, then 0x2F=val.
static void ldn_write(u8 reg, u8 val) {
	sio_write(0x2E, reg);
	sio_write(0x2F, val);
}

// Read a byte from LDN register `reg` via the 0x2E/0x2F window.
static u8 ldn_read(u8 reg) {
	sio_write(0x2E, reg);
	return sio_read(0x2F);
}

// Enter IT87 config mode by strobing the magic sequence to SIO_INDEX.
static void it87_enter(void) {
	for (int i = 0; i < ARRAY_SIZE(cfg_enter); i++)
		outb(cfg_enter[i], SIO_INDEX);
}

// Exit config mode is deliberately a no-op for port 0x4E on this hardware:
// the vendor-specific 0x87/0x01/0x55/0xAA unlock does not put the chip into
// standard IT87 config mode (the chip-ID registers echo the unlock bytes),
// so the standard 0x02 exit sequence does not apply.  Leaving the window
// open is harmless — each transaction re-runs it87_enter() before access,
// and this matches the field-verified 0.2.0 behaviour.  The mutex above
// serializes our own accesses; firmware/AML is not serialized either way.
static void it87_exit(void) {
	/* no-op for this port */
}

// Select the EC logical device (LDN 0x0A).
static void it87_select_ec_ldn(void) {
	ldn_write(0x07, LDN_EC);
}

// Read a byte from EC RAM at arbitrary 16-bit address.
static u8 ecram_read(u16 addr) {
	u8 hi = (u8)((addr >> 8) & 0xFF);
	u8 lo = (u8)(addr & 0xFF);

	ldn_write(LDN_ADDR_HI, hi);
	ldn_write(LDN_ADDR_LO, lo);
	return ldn_read(LDN_DATA);
}

// Write a byte to EC RAM at arbitrary 16-bit address.
static void ecram_write(u16 addr, u8 data) {
	u8 hi = (u8)((addr >> 8) & 0xFF);
	u8 lo = (u8)(addr & 0xFF);

	ldn_write(LDN_ADDR_HI, hi);
	ldn_write(LDN_ADDR_LO, lo);
	ldn_write(LDN_DATA,   data);
}

// ── Sysfs: charge_limit (write-only, 75-100) ─────────────────────────

static struct kobject *kobj;

static ssize_t charge_limit_store(struct kobject *kobj,
                                  struct kobj_attribute *attr,
                                  const char *buf, size_t count) {
	long pct;
	if (kstrtol(buf, 10, &pct) < 0)
		return -EINVAL;
	if (pct < 75 || pct > 100)
		return -EINVAL;

	guard(mutex)(&oxp_ec_lock);
	it87_enter();
	it87_select_ec_ldn();
	ecram_write(0x04A3, (u8)pct);
	it87_exit();

	pr_debug("wrote charge limit %ld%% to EC 0x04A3 via IT87 SIO\n", pct);
	return count;
}
static struct kobj_attribute charge_limit_attr =
	__ATTR(charge_limit, 0200, NULL, charge_limit_store);

// ── Sysfs: cpu_temp (read-only) ──────────────────────────────────────

static ssize_t cpu_temp_show(struct kobject *kobj,
                             struct kobj_attribute *attr,
                             char *buf) {
	u8 val;

	guard(mutex)(&oxp_ec_lock);
	it87_enter();
	it87_select_ec_ldn();
	val = ecram_read(0x0470);
	it87_exit();

	return sysfs_emit(buf, "%u\n", val);
}
static struct kobj_attribute cpu_temp_attr =
	__ATTR(cpu_temp, 0444, cpu_temp_show, NULL);

// ── Attribute group ───────────────────────────────────────────────────

static struct attribute *oxp_ec_attrs[] = {
	&charge_limit_attr.attr,
	&cpu_temp_attr.attr,
	NULL,
};
ATTRIBUTE_GROUPS(oxp_ec);

// ── Module lifecycle ──────────────────────────────────────────────────

static int __init oxp_ec_init(void) {
	int ret;

	// Claim the Super I/O port range.  This guards against other Linux
	// drivers (e.g. it87 hwmon) claiming 0x4E/0x4F; it cannot guard against
	// firmware/AML, which the mutex above also does not serialize.
	if (!request_region(SIO_INDEX, SIO_PORT_SPAN, "oxp_ec")) {
		pr_warn("ports 0x4E-0x4F in use — module not loaded\n");
		return -EBUSY;
	}

	kobj = kobject_create_and_add("oxp_ec", kernel_kobj);
	if (!kobj) {
		release_region(SIO_INDEX, SIO_PORT_SPAN);
		return -ENOMEM;
	}

	ret = sysfs_create_groups(kobj, oxp_ec_groups);
	if (ret) {
		kobject_put(kobj);
		release_region(SIO_INDEX, SIO_PORT_SPAN);
		return ret;
	}

	pr_info("loaded — %s\n", "/sys/kernel/oxp_ec/{charge_limit,cpu_temp}");
	return 0;
}

static void __exit oxp_ec_exit(void) {
	sysfs_remove_groups(kobj, oxp_ec_groups);
	kobject_put(kobj);
	release_region(SIO_INDEX, SIO_PORT_SPAN);
	pr_info("unloaded\n");
}

module_init(oxp_ec_init);
module_exit(oxp_ec_exit);

MODULE_LICENSE("GPL");
MODULE_AUTHOR("XmaX");
MODULE_DESCRIPTION("IT87 Super I/O EC RAM access for ONEXPLAYER SUPER X");
MODULE_VERSION("0.3.0");
