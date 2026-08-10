# XmaX

<div align="center">
  <img src="assets/logo.png" alt="XmaX Logo" width="96" height="96">
  <p>Handheld PC optimization toolkit for the <b>OneXPlayer Super X</b> (AMD Halo Strix) on Windows (Linux coming soon).
  <br>Designed to be a bloat-free and fully functional alternative to the OneXConsole</p>
</div>

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

- **[nlohmann/json](https://github.com/nlohmann/json)** (MIT) - JSON parsing and serialization
- **[Google Test](https://github.com/google/googletest)** (BSD-3-Clause) - Unit testing framework
- **[AMD ADLX SDK](https://github.com/GPUOpen-LibrariesAndSDKs/ADLX)** (AMD SDK License) - Official GPU telemetry and control
- **[PawnIO.Modules](https://github.com/namazso/PawnIO.Modules)** (LGPL-2.1) - Signed kernel driver modules for SMU and Super I/O access

For detailed dependency information, licensing, and binary attribution, see [backend/lib/README.md](backend/lib/README.md).
