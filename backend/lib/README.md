# Vendored Dependencies

Binary blobs and vendored source from open-source hardware control projects,
required for low-level hardware access. Linux additionally uses two DKMS
kernel modules: one vendored (ryzen_smu) and one first-party (xmax_sio_ec).

## Windows Binary Blobs

These files are from open-source hardware control projects. They are signed kernel driver blobs required for low-level hardware access on Windows.

| File | Size | Source | Purpose |
|------|------|--------|---------|
| **RyzenSMU.bin** | 39K | [PawnIO.Modules](https://github.com/namazso/PawnIO.Modules) | SMU mailbox for AMD Strix Halo TDP control |
| **LpcIO.bin** | 18K | [PawnIO.Modules](https://github.com/namazso/PawnIO.Modules) | Super I/O for EC RAM writes (charge limit) |

## Licensing

### PawnIO.Modules (RyzenSMU.bin, LpcIO.bin)
```
Copyright (c) namazso and contributors
Licensed under LGPL-2.1 (GNU Lesser General Public License v2.1)
https://github.com/namazso/PawnIO.Modules
```

**LGPL-2.1 is FOSS-friendly:**
- ✅ Allows commercial and non-commercial use
- ✅ Allows linking with code under different licenses
- ✅ Only requires modifications to these files remain open source
- ✅ No copyleft restrictions on your main codebase

**License file:** `PawnIO/COPYING.LGPL-2.1` (included as required by LGPL-2.1)

## GPU Metrics (ADLX)

GPU telemetry is retrieved via the **official AMD ADLX SDK** using COM interfaces provided by the AMD driver:
- No binary dependency (no DLL required)
- SDK uses **local-first** strategy: local copy in `backend/lib/ADLX/` with FetchContent fallback
- Licensed under AMD SDK License Agreement (royalty-free for use with AMD hardware)
- COM interfaces are provided by the AMD graphics driver itself

**Benefits:**
- ✅ No third-party wrapper DLL
- ✅ Official AMD SDK with full documentation
- ✅ Royalty-free license
- ✅ Commercial use allowed
- ✅ Direct from AMD (no third-party dependency)
- ✅ Offline builds work (local SDK copy)
- ✅ Automatic fallback to FetchContent if local files missing

## Updates

**PawnIO.Modules files (LGPL-2.1):**
These binaries are stable and rarely need updates. To update:
1. Download from: `https://github.com/namazso/PawnIO.Modules`
   - `RyzenSMU.bin`
   - `LpcIO.bin`
2. Replace files in `backend/lib/PawnIO/`
3. Commit with message: "Update PawnIO binaries"

**ADLX SDK (local-first with FetchContent fallback):**
Local copy stored in `backend/lib/ADLX/`. To update:

1. Download from: `https://github.com/GPUOpen-LibrariesAndSDKs/ADLX`
2. Replace contents of `backend/lib/ADLX/`
3. Commit with message: "Update ADLX SDK"

If local files are missing, CMake will automatically download from GitHub (FetchContent fallback). To pin to a specific version, edit `backend/CMakeLists.txt`:
```cmake
FetchContent_Declare(
    adlx
    GIT_REPOSITORY https://github.com/GPUOpen-LibrariesAndSDKs/ADLX.git
    GIT_TAG main  # Or use a specific tag/commit hash
)
```

## Build Strategy: Local-First with FetchContent Fallback

The PawnIO binary blobs use a **local-first** approach with **FetchContent fallback**:

1. **Primary: Local copies** (`backend/lib/PawnIO/`)
   - Fast, offline builds
   - Explicit LGPL compliance (files + license in repo)
   - Version controlled with the codebase
   - No network dependency

2. **Fallback: FetchContent** (automatic download)
   - Triggered when local files are missing (e.g., fresh clone without binaries)
   - Downloads from `https://github.com/namazso/PawnIO.Modules`
   - Cached by CMake after first download
   - Binaries copied to build output directory

**Why this approach?**
- Binary blobs rarely change (signed kernel drivers are stable)
- 57KB total size is negligible for git storage
- Offline builds work out of the box
- Fresh clones still build successfully
- LGPL compliance is explicit and auditable

The ADLX SDK uses FetchContent (source-only) because it's compiled source code with stable release tags on GitHub.

## RyzenAdj (Linux SMU / TDP control)

AMD SMU (TDP: STAPM/Fast/Slow) is controlled on Linux via **RyzenAdj**, vendored as
source in `backend/lib/ryzenadj/`:

- **Vendored source** (not a binary): upstream
  [FlyGoat/RyzenAdj](https://github.com/FlyGoat/RyzenAdj) `v0.19.0` (`ea71739`),
  Linux-only subset — the top-level `win32/` driver binaries and `.github/` are removed.
- **Not a build dependency**: xmaxd does not link ryzenadj. It `dlopen`s
  `libryzenadj.so` at runtime (`backend/src/platform/linux/ryzenadj.cpp`) and
  degrades to `SensorUnavailable` when the lib is absent.
- **Built/installed at setup time**: `scripts/linux/build-ryzenadj.sh` compiles the
  vendored source into `libryzenadj.so` and installs it (`/usr/local/lib` by default,
  honouring `PREFIX` / `XMAX_RYZENADJ_LIB`).

**Benefits:**
- ✅ Offline, reproducible build from source (no clone, no network)
- ✅ Linux-only footprint (Windows driver blobs excluded)
- ✅ Version-pinned with the codebase
- ✅ LGPL-3.0 compliant via dynamic `dlopen` linking (no link-time copyleft)

**License file:** `ryzenadj/LICENSE` (LGPL-3.0, included as required).

## ryzen_smu (Linux SMU kernel module)

SMU mailbox access (TDP control) on Linux goes through the **ryzen_smu**
kernel module, vendored as source in `backend/lib/ryzen_smu/`:

- **Vendored source**: upstream fork
  [amkillam/ryzen_smu](https://github.com/amkillam/ryzen_smu) (maintained fork
  of leogx9r/ryzen_smu), pinned at `d2983668300dd2a598e5a7dc40e71ce0678cc270`
  ("Fix cpuid include on 7.2+ kernels"). See `ryzen_smu/VENDOR.md` for the
  pin/update procedure.
- **Not a build dependency**: nothing in the backend links GPL code. xmaxd
  talks to the module over sysfs (`/sys/kernel/ryzen_smu_drv/`), so the GPL
  stays confined to the module itself.
- **Installed via DKMS at setup time**:
  `scripts/linux/install-smu-driver.sh` copies the source to `/usr/src`,
  registers it with DKMS, and builds + signs it locally on each machine
  (`just install-smu`). Never built or shipped from this repo as binaries.
- **Why required**: the alternative userspace path (raw `/dev/mem` + PCI) is
  blocked under Secure Boot kernel lockdown and grants full physical-memory
  access even where it works.

**License file:** `ryzen_smu/LICENSE` (GPL-2.0, included as required).

## xmax_sio_ec (first-party Super I/O module)

**Not a third-party dependency** — ~90 lines written for XmaX, licensed
GPL-2.0-or-later (GPLv2+ is the kernel-module convention and stays compatible
with the repo's GPL-3.0). Vendored at `backend/lib/xmax_sio_ec/`:

- **Purpose**: writes the battery charge-limit EC RAM register (0x04A3)
  through the IT87 Super I/O indexed-register protocol, bypassing the ACPI EC
  stack (which silently discards writes to this register on this hardware)
  and kernel lockdown (which blocks userspace port I/O under Secure Boot).
- **Sysfs interface**: `/sys/kernel/xmax_sio_ec/charge_limit` (write-only,
  75–100).
- **Installed via DKMS at setup time**: `scripts/linux/install-sio-driver.sh`
  (`just install-sio`). Reuses the MOK key enrolled for ryzen_smu for module
  signing.
- See `xmax_sio_ec/VENDOR.md` for design details and the update procedure.
