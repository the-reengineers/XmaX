# XmaX

<div align="center">
  <img src="shared/assets/logo.png" alt="XmaX Logo" width="96" height="96">
  <p>Optimization toolkit for the <b>OneXPlayer Super X</b> (AMD Halo Strix) on Windows and Fedora Linux.
  <br>Designed to be a bloat-free and fully functional alternative to the OneXConsole</p>
</div>

**WARNING: This is still in development and not in any state for use.**

## How It Works
### Backend

The backend (`backend/`) is a C++ service that provides hardware control and telemetry for AMD-based handheld **OneXPlayer Super X**. It runs as a background process that:

- Controls TDP (thermal design power) limits via SMU mailbox commands
- Manages fan curves through embedded controller (EC) register writes
- Monitors GPU metrics using the official AMD ADLX SDK
- Adjusts battery charge limits via Super I/O interface
- Exposes an IPC transport layer for frontend communication

Built with CMake and C++23, the service uses Windows-specific APIs (WMI, PawnIO driver, GDI+) for low-level hardware access. It supports adaptive tuning with PID-based control loops for dynamic thermal and fan management.

### Dependencies

The backend relies on the following open-source projects:

**Cross-platform:**
- **[nlohmann/json](https://github.com/nlohmann/json)** (MIT) - JSON parsing and serialization
- **[Google Test](https://github.com/google/googletest)** (BSD-3-Clause) - Unit testing framework

**Windows:**
- **[AMD ADLX SDK](https://github.com/GPUOpen-LibrariesAndSDKs/ADLX)** (AMD SDK License) - Official GPU telemetry and control
- **[PawnIO.Modules](https://github.com/namazso/PawnIO.Modules)** (LGPL-2.1) - Signed kernel driver modules for SMU and Super I/O access

**Linux:**
- **[ryzen_smu](https://github.com/amkillam/ryzen_smu)** (GPL-2.0) - Kernel module for SMU mailbox access (TDP control), vendored and DKMS-installed by `scripts/linux/install-smu-driver.sh`
- **[RyzenAdj](https://github.com/FlyGoat/RyzenAdj)** (LGPL-3.0) - TDP limit library, vendored source built at setup and loaded at runtime via `dlopen`

Linux also ships a first-party kernel module (`xmax_sio_ec`, Super I/O charge-limit writes) built from this repo.

For detailed dependency information, licensing, and binary attribution, see [backend/lib/README.md](backend/lib/README.md).

### License

XmaX is licensed under the [GPL-3.0](LICENSE). Copyright (C) 2026 the-reengineers.

This software controls low-level hardware (fan, TDP, battery charge limits). It is
distributed in the hope that it will be useful, but **WITHOUT ANY WARRANTY**; without
even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
Use at your own risk — see sections 15–16 of the License.

Everyone may use, study, modify, and redistribute it — with attribution preserved.
Derivative works that are distributed must be released under the GPL-3.0 as well.
Vendored third-party components keep their own licenses: ryzen_smu (GPL-2.0),
RyzenAdj (LGPL-3.0), PawnIO modules (LGPL-2.1), and the AMD ADLX SDK (AMD SDK
License). Contributions are accepted under the project license (GPL-3.0-or-later,
per each file's SPDX header).
