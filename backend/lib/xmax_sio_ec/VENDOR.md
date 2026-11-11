## oxp_ec — IT87 Super I/O EC RAM access kernel module

**Source:** Written for XmaX by Alex.

**Purpose:** Provides a kernel-level path for reading and writing EC RAM
registers on the ONEXPLAYER SUPER X (Strix Halo) through the IT87 Super I/O
chip's indexed register protocol.  The ACPI EC command layer silently discards
writes to charge-limit register 0x04A3 and does not expose CPU temperature
register 0x0470; the SIO path bypasses both limitations.

**Why a kernel module:** Userspace port I/O via `ioperm()`/`outb()` is blocked
by kernel lockdown (Secure Boot).  A tiny in-kernel driver solves that: it
has CAP_SYS_RAWIO from the kernel and bypasses the ACPI EC stack entirely by
reading/writing directly to EC RAM through the IT87's logical-device registers.

**Sysfs interface:**
- `/sys/kernel/oxp_ec/charge_limit` — write-only, 75-100 (EC 0x04A3)
- `/sys/kernel/oxp_ec/cpu_temp` — read-only, °C (EC 0x0470)

**Size:** ~140 lines.  Single source file, no external dependencies.

**Updating:** Edit `oxp_ec.c`, bump VERSION in `Makefile`.