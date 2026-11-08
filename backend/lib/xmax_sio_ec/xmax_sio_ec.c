// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 the-reengineers
//
// xmax_sio_ec — kernel module providing a sysfs interface for direct EC RAM
// writes through the IT87 Super I/O chip (ports 0x4E/0x4F).  The standard
// ACPI EC path silently discards writes to battery charge-limit register
// 0x04A3 on the ONEXPLAYER SUPER X; writing through the SIO's indexed
// register protocol bypasses this protection.
//
// Usage:
//   echo 85 > /sys/kernel/xmax_sio_ec/charge_limit
//
// This module is tiny on purpose: it does one thing and does it from the
// kernel so that Secure Boot lockdown (which blocks userspace ioperm/outb)
// is not an obstacle.

#define pr_fmt(fmt) "xmax_sio_ec: " fmt

#include <linux/init.h>
#include <linux/kernel.h>
#include <linux/kobject.h>
#include <linux/module.h>
#include <linux/string.h>
#include <linux/sysfs.h>
#include <linux/io.h>
#include <linux/ioport.h>

// ── IT87 Super I/O constants ──────────────────────────────────────────
#define SIO_INDEX     0x4E
#define SIO_DATA      0x4F
#define SIO_PORT_SPAN 2

// Config entry sequence (strobe to index port).
static const u8 cfg_enter[] = { 0x87, 0x01, 0x55, 0xAA };

// Logical-device registers for EC RAM access.
#define LDN_ADDR_HI  0x11
#define LDN_ADDR_LO  0x10
#define LDN_DATA     0x12

// ── Low-level helpers ─────────────────────────────────────────────────

static inline void sio_write(u8 reg, u8 val) {
	outb(reg, SIO_INDEX);
	outb(val, SIO_DATA);
}

// Write `val` to the LDN register `reg`: select 0x2E=reg, then 0x2F=val.
static void sio_write_ldn(u8 reg, u8 val) {
	sio_write(0x2E, reg);
	sio_write(0x2F, val);
}

// Enter IT87 config mode.
static void it87_enter(void) {
	for (int i = 0; i < ARRAY_SIZE(cfg_enter); i++)
		outb(cfg_enter[i], SIO_INDEX);
}

// Write a byte to EC RAM at arbitrary 16-bit address.
static void ecram_write(u16 addr, u8 data) {
	u8 hi = (u8)((addr >> 8) & 0xFF);
	u8 lo = (u8)(addr & 0xFF);

	sio_write_ldn(LDN_ADDR_HI, hi);
	sio_write_ldn(LDN_ADDR_LO, lo);
	sio_write_ldn(LDN_DATA,   data);
}

// ── Sysfs: charge_limit ───────────────────────────────────────────────

static struct kobject *kobj;

static ssize_t charge_limit_store(struct kobject *kobj,
                                  struct kobj_attribute *attr,
                                  const char *buf, size_t count) {
	long pct;
	if (kstrtol(buf, 10, &pct) < 0)
		return -EINVAL;
	if (pct < 75 || pct > 100)
		return -EINVAL;

	it87_enter();
	ecram_write(0x04A3, (u8)pct);
	// Exit config mode is a no-op for this port; leaving it open is
	// harmless — the kernel will re-enter when needed.

	pr_info("wrote charge limit %ld%% to EC 0x04A3 via IT87 SIO\n", pct);
	return count;
}
static struct kobj_attribute charge_limit_attr =
	__ATTR(charge_limit, 0200, NULL, charge_limit_store);

static struct attribute *xmax_sio_ec_attrs[] = {
	&charge_limit_attr.attr,
	NULL,
};
ATTRIBUTE_GROUPS(xmax_sio_ec);

// ── Module lifecycle ──────────────────────────────────────────────────

static int __init xmax_sio_ec_init(void) {
	int ret;

	// Claim the Super I/O port range.
	if (!request_region(SIO_INDEX, SIO_PORT_SPAN, "xmax_sio_ec")) {
		pr_warn("ports 0x4E-0x4F in use — module not loaded\n");
		return -EBUSY;
	}

	kobj = kobject_create_and_add("xmax_sio_ec", kernel_kobj);
	if (!kobj) {
		release_region(SIO_INDEX, SIO_PORT_SPAN);
		return -ENOMEM;
	}

	ret = sysfs_create_groups(kobj, xmax_sio_ec_groups);
	if (ret) {
		kobject_put(kobj);
		release_region(SIO_INDEX, SIO_PORT_SPAN);
		return ret;
	}

	pr_info("loaded — IT87 EC RAM write path at %s\n",
	        "/sys/kernel/xmax_sio_ec/charge_limit");
	return 0;
}

static void __exit xmax_sio_ec_exit(void) {
	sysfs_remove_groups(kobj, xmax_sio_ec_groups);
	kobject_put(kobj);
	release_region(SIO_INDEX, SIO_PORT_SPAN);
	pr_info("unloaded\n");
}

module_init(xmax_sio_ec_init);
module_exit(xmax_sio_ec_exit);

MODULE_LICENSE("GPL");
MODULE_AUTHOR("XmaX");
MODULE_DESCRIPTION("IT87 Super I/O EC RAM write bypass for charge limit");