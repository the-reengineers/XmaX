## xmax_sio_ec — IT87 Super I/O EC RAM write kernel module

**Source:** Written for XmaX by Alex.

**Purpose:** Provides a kernel-level path for writing battery charge-limit
register 0x04A3 on the ONEXPLAYER SUPER X (and possibly other Strix Halo
handhelds) through the IT87 Super I/O chip's indexed register protocol.

**Why a kernel module:** The ACPI EC command layer silently discards writes
to register 0x04A3 on this hardware (confirmed on both Windows and Linux).
Userspace port I/O via `ioperm()`/`outb()` is blocked by kernel lockdown
(Secure Boot).  A tiny in-kernel driver solves both problems: it has
CAP_SYS_RAWIO from the kernel and bypasses the ACPI EC stack entirely by
writing directly to EC RAM through the IT87's logical-device registers.

**Sysfs interface:** `/sys/kernel/xmax_sio_ec/charge_limit` (write-only, 75-100)

**Size:** ~90 lines.  Single source file, no external dependencies.

**Updating:** Edit `xmax_sio_ec.c`, bump VERSION in `Makefile`.