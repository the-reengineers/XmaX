# Vendored: ryzen_smu kernel module

- Upstream: https://github.com/amkillam/ryzen_smu (maintained fork of
  https://github.com/leogx9r/ryzen_smu)
- Pinned commit: `d2983668300dd2a598e5a7dc40e71ce0678cc270`
  ("Fix cpuid include on 7.2+ kernels" — required for kernel 7.2+)
- License: GPL-2.0 (see LICENSE). Vendored source only — we never build or
  ship binaries from here; `scripts/linux/install-smu-driver.sh` copies this
  source to /usr/src and DKMS builds + signs it locally on each machine.
  xmaxd talks to the resulting module over sysfs
  (`/sys/kernel/ryzen_smu_drv/`), so nothing in the backend links GPL code.

## Why a kernel module?

libryzenadj can reach the SMU two ways: raw `/dev/mem` + PCI (blocked under
Secure Boot kernel lockdown, and grants full physical-memory access even
where it works) or through this module's narrow sysfs interface, where the
driver arbitrates every SMU mailbox access in kernel space. The module is
the required path on Secure Boot systems and the strictly safer path
everywhere else, so it is the only path we install.

## Contents

Only the files `make dkms-install` needs (it copies them to
/usr/src/ryzen_smu-<version>): `dkms.conf`, `Makefile`, `drv.c`, `smu.c`,
`smu.h`, plus `LICENSE` and `README.md` kept for compliance/context. The
upstream `userspace/`, `lib/`, `scripts/`, `docs/` trees are not needed.

## Updating the pin

Re-pin deliberately: `git log` the upstream fork, verify the commit still
supports the CPU families we ship for (family 0x1A incl. Strix Halo is in
`smu.c`), then copy the files listed above and update the commit hash in
this file. Re-run `scripts/linux/install-smu-driver.sh` (it replaces the
DKMS registration) and `just verify-fe` afterwards.
