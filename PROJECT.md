# XmaX — Project Structure

## Overview

A two-process architecture replacing the current OneXConsole (Electron) + CompatLayerCT stack.

**Current focus:** Windows implementation. Linux support is planned — the architecture is designed for it, but only Windows is being built now.

### Binary names

| | Windows | Linux |
|--|---------|-------|
| Backend | `xmaxsvc.exe` | `xmaxd` |
| Frontend | `xmax.exe` | `xmax` |

Each platform follows its own naming convention — `-svc` suffix for Windows services, `-d` suffix for Unix daemons. The frontend is simply `xmax` on both.

## Platform Architecture

The two-process model and IPC protocol are shared across platforms. The transport layer, hardware backends, and frontend UI are platform-specific.

```
┌─────────────────────────────────────────────────┐
│  Shared (both platforms)                         │
│  ─ IPC protocol (JSON commands/events/metrics)   │
│  ─ Command dispatch, state management, profiles  │
│  ─ Config storage format (JSON)                  │
└─────────────────────────────────────────────────┘
         │                    │
┌────────▼────────┐  ┌───────▼────────┐
│  Windows (now)   │  │  Linux (later) │
├─────────────────┤  ├────────────────┤
│ Named Pipes      │  │ Unix sockets   │
│ WMI / ADLX       │  │ sysfs / hwmon  │
│ PawnIO           │  │ /dev/cpu/msr   │
│ WinUI 3 (C#)     │  │ GTK4/Qt (C++)  │
│ Job Object       │  │ Process groups │
│ Shell_NotifyIcon │  │ AppIndicator   │
│ Task Scheduler   │  │ systemd unit   │
└─────────────────┘  └────────────────┘
```

The backend C++ core (protocol, state, profiles) is ~80% shareable. Platform-specific code lives behind thin abstraction interfaces so neither platform is "main" and the other "ported."

### Windows architecture (current)

```
┌──────────────────────────────────────────────────────────────┐
│  xmaxsvc.exe  (C++ background service)                       │
│                                                              │
│  ┌─────────────┐  ┌─────────────┐  ┌──────────────────────┐ │
│  │ Pipe server  │  │ Metrics     │  │ Button monitor       │ │
│  │ (accepts     │  │ poller      │  │ (EC 0x0230 @ 100ms)  │ │
│  │  frontend    │  │ (2000ms)    │  │                      │ │
│  │  connections)│  │             │  │ On press → show/hide │ │
│  └──────┬──────┘  └──────┬──────┘  │ frontend window      │ │
│         │                │         └──────────────────────┘ │
│         │                │                                   │
│  ┌──────┴────────────────┴──────────────────────────────┐   │
│  │ Shared state (mutex-protected)                                         │   │
│  │  metrics, tdp_state, fan_state, button_state, power_state, charge_limit│   │
│  └──────────────────────────────────────────────────────┘   │
│                    ▲                                         │
│                    │ Named Pipe (JSON)                        │
│                    │ \\.pipe\xmaxsvc                          │
└────────────────────┼─────────────────────────────────────────┘
                     │
┌────────────────────┼─────────────────────────────────────────┐
│  xmax.exe          │ (WinUI 3 / C# frontend)                 │
│                    ▼                                         │
│  ┌─────────────┐  ┌─────────────┐  ┌──────────────────────┐ │
│  │ TDP sliders  │  │ Fan control  │  │ Metrics dashboard   │ │
│  └─────────────┘  └─────────────┘  └──────────────────────┘ │
│                                                              │
│  ┌─────────────┐  ┌─────────────┐  ┌──────────────────────┐ │
│  │ Profiles     │  │ Charge limit │  │ Settings            │ │
│  └─────────────┘  └─────────────┘  └──────────────────────┘ │
└──────────────────────────────────────────────────────────────┘
```

Linux will follow the same two-process shape with `xmaxd`/`xmax`, Unix domain sockets, and a GTK4/Qt frontend.

## Cross-Platform Code Conventions

These rules exist to prevent one OS from becoming the default and the other an afterthought. Both platforms are equal targets — the codebase must not imply otherwise.

### 1. No `#ifdef` in shared code

Shared code (`main.cpp`, `transport.cpp`, `metrics.cpp`, `tdp.cpp`, `fan.cpp`, `button.cpp`, `process.cpp`, `shared.h`, `protocol.h`) must never contain `#ifdef _WIN32` or `#ifdef __linux__`. Platform branching lives exclusively in `platform/platform_win32.cpp` and `platform/platform_linux.cpp`.

If shared code needs to branch on platform, the abstract interface is missing a method — add it to `platform.h` instead.

### 2. Platform-neutral types in shared headers

`shared.h` and `protocol.h` use only standard C++ and project-defined types. No `HANDLE`, `DWORD`, `HWND`, `pid_t`, `uid_t`, or any OS-specific type.

```cpp
// ✅ shared.h — neutral
struct FanState {
    enum class Mode { Auto, Manual, Curve };
    Mode mode;
    uint8_t speed;       // 0–255
    uint16_t rpm;
};

// ❌ shared.h — leaks platform
struct FanState {
    DWORD mode;          // Windows type
    BYTE speed;
    HANDLE ec_handle;    // Windows type
};
```

Platform implementations convert to/from neutral types at the boundary.

### 3. Abstract platform interface

`platform/platform.h` defines the contract. Shared code depends only on this interface. CMake selects which implementation file to compile — not `#ifdef`.

```cpp
class Platform {
public:
    virtual ~Platform() = default;

    // Transport
    virtual auto listen() -> Result<TransportServer> = 0;
    virtual auto verify_peer(PeerId) -> Result<PeerInfo> = 0;

    // Hardware
    virtual auto ec_read(uint16_t reg) -> Result<uint8_t> = 0;
    virtual auto ec_write(uint16_t reg, uint8_t val) -> Result<void> = 0;
    virtual auto smu_send(uint32_t msg, uint32_t arg) -> Result<uint32_t> = 0;
    virtual auto charge_limit_write(uint8_t percent) -> Result<void> = 0;  // Super I/O path (PawnIO + IT87)

    // GPU telemetry
    virtual auto gpu_metrics() -> Result<GpuMetrics> = 0;

    // Process management
    virtual auto spawn_frontend() -> Result<ChildProcess> = 0;
    virtual auto show_window(ChildProcess&, bool visible) -> Result<void> = 0;

    // System
    virtual auto tray_icon(TrayConfig) -> Result<TrayHandle> = 0;
    virtual auto data_dir() -> std::filesystem::path = 0;
    virtual auto single_instance_lock() -> Result<InstanceLock> = 0;
};
```

### 4. CMake selects platform at configure time

```cmake
# Binary name follows platform convention
if(WIN32)
    set(BACKEND_TARGET xmaxsvc)
    target_sources(${BACKEND_TARGET} PRIVATE src/platform/platform_win32.cpp)
elseif(UNIX)
    set(BACKEND_TARGET xmaxd)
    target_sources(${BACKEND_TARGET} PRIVATE src/platform/platform_linux.cpp)
endif()
```

No platform code is compiled into shared modules. The build for each platform is a first-class configuration — not a cross-compile from the "main" platform.

### 5. Tests run on both platforms

Tests for shared logic (protocol parsing, state management, profile serialization) must compile and pass on both platforms. Platform-specific tests live alongside their implementation files.

### 6. Documentation describes behavior, not implementation

Document what happens, not how one platform does it. Platform-specific details go under labeled sub-sections, never as the default description.

```
❌ "The backend listens on Named Pipe \\.\pipe\xmaxsvc"
✅ "The backend listens on a local IPC transport"
   → Windows: Named Pipe at \\.\pipe\xmaxsvc
   → Linux: Unix socket at $XDG_RUNTIME_DIR/xmaxd.sock
```

## Process Relationship

The backend is the parent. It spawns the frontend as a child process.

- Frontend cannot run without the backend — no standalone mode
- Backend respawns the frontend if it crashes unexpectedly (monitors process handle)

### Windows (current)

`xmaxsvc.exe` spawns `xmax.exe`.

- Backend creates a **Job Object** with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`
- Both processes are assigned to the job
- When `xmaxsvc.exe` exits (crash, kill, clean shutdown), the OS terminates all processes in the job — including the frontend

### Linux (future)

`xmaxd` spawns `xmax`.

- Backend sets frontend into the same process group (`setpgid`)
- On exit, backend sends `SIGTERM` to the group; a watchdog or `prctl(PR_SET_PDEATHSIG)` ensures cleanup if the parent dies unexpectedly

### Single instance

- **Windows:** Named mutex `Global\XmaX_SingleInstance`. If it already exists, the new instance shows a notification ("XmaX is already running") via `MessageBoxW` and exits.
- **Linux:** PID file at `$XDG_RUNTIME_DIR/xmaxd.pid` with `flock`. If the lock is held, the new instance shows a notification via `libnotify` (`notify-send`) and exits.

## IPC Protocol

The protocol (commands, events, metrics JSON) is **platform-agnostic** — identical bytes on the wire for both Windows and Linux. Only the transport layer differs.

**Format:** Newline-delimited JSON (`\n` terminated). JSON is used over binary formats because payloads are small (~400 bytes), infrequent (2s intervals), and local — overhead is negligible. JSON provides forward-compatible schema evolution (add fields freely) and trivial debugging without requiring schema definitions or version negotiation.

### Transport

| | Windows (current) | Linux (future) |
|--|-------------------|----------------|
| Mechanism | Named Pipe (`\\.\pipe\xmaxsvc`) | Unix domain socket (`$XDG_RUNTIME_DIR/xmaxd.sock`) |
| Security | Pipe ACL (current user SID) | Socket file permissions (`0700`) |
| Peer ID | `GetNamedPipeClientProcessId` | `SO_PEERCRED` (PID, UID, GID) |

### Connection (Windows)

- Pipe name: `\\.\pipe\xmaxsvc`
- Direction: bidirectional (duplex)
- Format: newline-delimited JSON (`\n` terminated)
- Security: Level 1 (process path verification) + Level 2 (pipe ACL restricted to current user SID)

### Security handshake (Windows)

On each new connection:

1. Backend calls `GetNamedPipeClientProcessId` → gets client PID
2. Backend calls `QueryFullProcessImageNameW` → verifies executable is `xmax.exe`
3. Backend verifies the executable path is under the expected install directory
4. If verification fails: pipe closed immediately, no response sent
5. If verification passes: normal command processing begins

Pipe ACL is set via `SECURITY_DESCRIPTOR` with a DACL allowing only the current user's SID.

### Message types

Every message on the pipe has a `type` field that identifies the message category. Commands additionally have a `method` field for the command name:

```jsonc
// 1. Command (FE → BE) — type + method + request id
{"type": "command", "method": "get_metrics", "id": "req_001"}
{"type": "command", "method": "set_fan", "id": "req_002", "mode": "auto"}

// 2. Response (BE → FE) — echoes the request id
{"type": "response", "id": "req_001", "ok": true, "data": { /* result */ }}
{"type": "response", "id": "req_002", "ok": false, "error": "tdp_out_of_range"}

// 3. Event (BE → FE, unsolicited) — no id
{"type": "event", "event": "button_press", "data": {"count": 5}}

// 4. Error (BE → FE) — for malformed/unparseable input, no id
{"type": "error", "error": "parse_error"}
```

| Direction | Type | Has `id` | Fields |
|-----------|------|----------|--------|
| FE → BE | `command` | Required | `type`, `method` (command name), `id`, command-specific payload |
| BE → FE | `response` | Echoed from request | `type`, `id`, `ok`, `data` or `error` |
| BE → FE | `event` | No | `type`, `event` (event name), `data` |
| BE → FE | `error` | No | `type`, `error` |

**Request IDs** are generated by the FE (UUID or monotonic counter). The BE echoes the `id` back in the response so the FE can correlate responses to requests when sending multiple commands in flight.

### Commands (Frontend → Backend)

| Command | Payload | Response `data` | Description |
|---------|---------|-----------------|-------------|
| `get_metrics` | — | `{metrics}` | Poll all current metrics |
| `subscribe_metrics` | `interval_ms` | `{ok}` | Start periodic metrics push at given interval |
| `unsubscribe_metrics` | — | `{ok}` | Stop periodic metrics push |
| `set_fan` | `mode` ("auto"/"curve"), `curve_id` (slug, curve mode only) | `{mode}` | Set fan mode via EC. Hardware write — rejected when persist=false |
| `get_fan` | — | `{mode, speed_pct, rpm}` | Read current fan state |
| `get_button` | — | `{presses}` | Read button press count |
| `get_power_mode` | — | `{mode, label}` | Read current power state (battery/usb_c_slow/usb_c_fast/dc_in) |
| `set_profile` | `id` (slug) | `{id, name}` | Apply a saved profile (sets TDP + fan curve). Disables adaptive controller if active. Hardware write — rejected when persist=false |
| `get_profiles` | — | `{profiles}` | Read all saved profiles (with resolved fan curve data) |
| `save_profile` | `id`, `name`, `tdp`, `fan_curve` | `{id}` | Create or update a profile. `id` is slug (auto-generated from name on create) |
| `delete_profile` | `id` (slug) | `{}` | Delete a profile. Fails if referenced by power-state profiles |
| `get_fan_curves` | — | `{fan_curves}` | Read all saved fan curves |
| `save_fan_curve` | `id`, `name`, `points` | `{id}` | Create or update a fan curve. `id` is slug |
| `delete_fan_curve` | `id` (slug) | `{}` | Delete a fan curve. Fails if referenced by any profile |
| `set_power_profile` | `state`, `profile` (slug), `tdp_max_w` | `{}` | Assign a profile and adaptive TDP ceiling to a power state (all states must be configured) |
| `get_power_profiles` | — | `{battery, usb_c_slow, usb_c_fast, dc_in}` (each with `profile`, `tdp_max_w`) | Read all power-state assignments |
| `set_charge_limit` | `percent` (75-100) | `{percent}` | Set battery charge limit via Super I/O (requires PawnIO). Hardware write — rejected when persist=false |
| `get_charge_limit` | — | `{percent}` | Read battery charge limit from EC `0x04A3` |
| `set_auto_tune` | `tuning`, `target_temp_c`, `tdp_max_w`, `fan_max_pct` | `{}` | Activate adaptive controller with given tuning. Deactivates the active profile (mutually exclusive). TDP ceiling is clamped by current power state. Hardware write — rejected when persist=false |
| `get_auto_tune` | — | `{active, tuning, target_temp_c, tdp_max_w, effective_tdp_max_w, fan_max_pct}` | Read adaptive controller state. `active` indicates whether adaptive is the current active mode (vs a profile). Includes post-clamp effective ceiling |
| `get_config` | — | Full `config.json` object | Read all app settings |
| `set_config` | Any subset of config fields | Updated config object | Update config.json (partial — only sent fields are changed) |
| `ping` | — | `{}` | Health check |

### Persist gating

When `persist` is false, the app is effectively disabled — only metrics monitoring and read operations are active. All commands that write hardware are rejected with a `persist_disabled` error. The frontend should disable or hide hardware controls when persist is off.

| Category | Commands | persist=false behavior |
|----------|----------|----------------------|
| **Read-only** | `get_*`, `subscribe_metrics`, `unsubscribe_metrics`, `ping` | Always allowed |
| **Disk writes** | `save_profile`, `delete_profile`, `save_fan_curve`, `delete_fan_curve`, `set_power_profile`, `set_config` | Always allowed (no hardware effect — prepares config for when persist is enabled) |
| **Hardware writes** | `set_fan`, `set_profile`, `set_charge_limit`, `set_auto_tune` | Rejected with `persist_disabled` error |

The frontend should check `persist` from `get_config` on connect and disable hardware controls accordingly. Toggling persist from false to true via `set_config` does not retroactively apply any profile — the user must manually apply a profile or wait for the next startup.

### Metrics subscription

Metrics push is **opt-in**. The FE sends `subscribe_metrics` with a desired interval when it needs live updates (dashboard open). It sends `unsubscribe_metrics` when live updates are no longer needed (dashboard closed, window hidden). The BE pushes `metrics` events only to subscribed clients.

When the FE is hidden or the metrics page is closed, no metrics are pushed — saving IPC bandwidth and avoiding unnecessary FE processing.

### Command timeout

The FE applies a **5-second timeout** to all commands. If no response is received within 5s, the FE treats the command as failed and shows an error to the user. The BE has no timeout — hardware operations (SMU writes, Super I/O) take as long as they take. The FE may retry timed-out commands at its discretion (e.g., `ping` for health check before retrying).

### Reconnection

Metrics subscriptions are **per-connection**. When the pipe disconnects, the BE drops all subscriptions for that connection. No events are buffered during disconnection.

On reconnect, the FE:
1. Sends `get_metrics` to get an immediate snapshot of current state (catches anything changed during disconnection)
2. Sends `subscribe_metrics` to resume push (if the dashboard is still open)
3. Reports its current window visibility state to the BE (syncs `fe_visible` after crash respawn — frontend always starts hidden, so the BE resets its visibility tracking)
4. Fetches any other state it needs (`get_fan`, `get_auto_tune`, etc.)

Global BE state (TDP, fan, charge limit, active profile) persists across disconnections — it's process-level state, not connection-level. The `get_metrics` snapshot on reconnect picks up everything.

### Events (Backend → Frontend, unsolicited)

| Event | Payload | Description |
|-------|---------|-------------|
| `button_press` | `{count}` | Hardware button pressed |
| `show_toggle` | `{visible}` | Backend toggled frontend visibility |
| `power_mode_change` | `{mode, label}` | Power source changed (auto-applies power-state profile if persist=true and no dynamic profile is active) |
| `metrics` | `{metrics}` | Periodic metrics push (only while subscribed) |
| `auto_tune_adjust` | `{tuning, tdp_w, fan_pct, smoothed_temp_c, effective_tdp_max_w, reason}` | Adaptive controller applied a change (only when values change, not every tick) |
| `auto_tune_state` | `{active}` | Adaptive controller became active (a profile was deselected) or inactive (a profile was selected) |

### Error handling

```jsonc
// Success
{"type": "response", "id": "req_001", "ok": true, "data": {"stapm": 45, "fast": 50, "slow": 45}}

// Command error — valid JSON, valid command, but invalid parameters
{"type": "response", "id": "req_001", "ok": false, "error": "tdp_out_of_range"}

// Unknown command
{"type": "response", "id": "req_002", "ok": false, "error": "unknown_command"}

// Malformed input — not valid JSON (no id available)
{"type": "error", "error": "parse_error"}
```

`error` is a machine-readable code. The FE maps error codes to user-facing strings (supports localization without BE involvement).

#### Error code management

All error codes are defined in a single shared file and code-generated into platform-specific enums at build time. This keeps the BE and FE in sync — impossible to add an error code on one side without the other knowing about it.

```
APP/
  shared/
    errors.json              ← single source of truth (codes + names only)
    generate_errors.py       ← generates enums for all platforms

  backend/src/generated/
    error_codes.h            ← C++ enum (generated at build)

  frontend/windows/Generated/
    ErrorCodes.cs            ← C# enum (generated at build)
```

**`errors.json`** contains only the protocol contract — code number and name. No translations, no descriptions:

```json
{
  "tdp_out_of_range":       { "code": 1001 },
  "fan_speed_invalid":      { "code": 1002 },
  "charge_limit_invalid":   { "code": 1003 },
  "unknown_command":        { "code": 2001 },
  "parse_error":            { "code": 2002 },
  "hardware_busy":          { "code": 3001 },
  "sensor_unavailable":     { "code": 3002 },
  "charge_limit_write_fail":{ "code": 3003 },
  "profile_not_found":      { "code": 4001 },
  "fan_curve_not_found":    { "code": 4003 },
  "fan_curve_in_use":       { "code": 4004 },
  "profile_in_use":         { "code": 4005 },
  "fan_curve_invalid":      { "code": 1004 },
  "persist_disabled":       { "code": 4006 }
}
```

**Translation strings** are owned by each FE project using that platform's native i18n system (`.resx` on Windows, `.po`/`.ts` on Linux). The BE never touches translations. Adding a new language is a FE-only change.

The FE falls back to a generic message for any error code it doesn't recognize (defense in depth against sync issues).

### Metrics payload

```json
{
  "cpu": {
    "util_pct": 7.8,
    "clock_mhz": 3000,
    "temp_c": 79,
    "package_watts": 45.2
  },
  "gpu": {
    "util_pct": 93.0,
    "clock_mhz": 1783,
    "temp_c": 73,
    "power_w": 47.0,
    "vram_used_mb": 4096,
    "vram_total_mb": 16384
  },
  "ram": {
    "used_gb": 25.8,
    "total_gb": 111.6,
    "avail_gb": 85.8,
    "load_pct": 23.0
  },
  "fan": {
    "mode": "auto",
    "speed_pct": 75.0,
    "rpm": 3200
  },
  "power": {
    "mode": "dc_in",
    "label": "DC-In (dedicated charger)",
    "battery_pct": 91,
    "charge_limit_pct": 90
  },
  "ts": 1722000000
}
```

Fields marked `null` when unavailable (e.g., `battery_pct` on devices without a battery).

## Backend — C++

### Thread model

| Thread | Responsibility | Poll rate |
|--------|---------------|-----------|
| **Main** | Event loop, tray icon, frontend process management | Event-driven |
| **Transport server** | Accept connections, read commands, dispatch to handlers, write responses | Blocking I/O |
| **Metrics poller** | Poll all sensors, update shared metrics struct (mutex-protected) | 2000ms |
| **Fan/auto-tune controller** | Fan curve interpolation OR adaptive PID (mutually exclusive — auto-tune overrides curve). Reads temp from shared metrics, writes fan speed | 1s |
| **Power state monitor** | Poll EC `0x04FE` + `0x04A3`, detect power source changes, auto-apply profiles (persist=true only), read charge limit for metrics | 2000ms (in metrics poller) |
| **Button monitor** | Poll EC register 0x0230, detect any state change (edge detect), toggle frontend visibility | 100ms |

The main loop implementation is platform-specific (Windows message loop on Windows, `epoll`/`glib` main loop on Linux) but its responsibilities are identical.

### Hardware connection lifecycle

All hardware connections are **persistent** — opened once at startup, held for the process lifetime, closed at shutdown. This applies to WMI COM (UMAInterface), PawnIO driver handle + loaded blobs (`RyzenSMU.bin`, `LpcIO.bin`), and ADLX library.

**⚠️ COM apartment threading (Windows):** WMI uses COM, which requires each thread that calls WMI to initialize its own COM apartment (`CoInitializeEx`). Multiple threads (metrics poller, button monitor, adaptive controller) all read EC registers via WMI — each must initialize COM independently with `COINIT_MULTITHREADED` (MTA). The WMI `IWbemServices` pointer **cannot be shared across threads** directly; each thread must either:
- Obtain its own `IWbemServices` pointer via `CoCreateInstance` + `ConnectServer`, or
- Use `CoMarshalInterThreadInterfaceInStream` / `CoGetInterfaceAndReleaseStream` to safely pass the interface pointer between threads.

Getting this wrong causes `RPC_E_WRONG_THREAD` or silent hangs. The platform layer must handle COM apartment initialization per-thread internally.

### Startup & shutdown behavior

**Startup sequence:**
1. Initialize hardware connections (WMI, PawnIO + LpcIO.bin, ADLX)
2. Detect current power state (EC `0x04FE`)
3. If `persist` is enabled: apply charge limit from `config.json`, apply user's profile from `config.json` `power_state_profiles[state]`, restore global `auto_tune` config from `config.json` (clamped by current power state bounds)
4. If `persist` is disabled: do nothing — hardware is already at BIOS defaults after reboot
5. Spawn frontend hidden
6. Enter main loop

**Shutdown sequence:**
1. Close hardware connections
2. Exit (Job Object kills frontend automatically)

**Hardware state is not persisted across reboots.** SMU TDP limits, EC fan settings, and EC charge limit all reset to BIOS/firmware defaults on every cold boot. The app does not need to restore safe defaults on startup or shutdown — the hardware does this automatically. If the app crashes mid-session, the system continues running with whatever settings were last applied until the next reboot resets them to BIOS defaults.

### Module architecture

The backend is layered: **domain modules** own hardware-specific knowledge (register addresses, opcodes, value interpretation), the **platform layer** provides raw read/write primitives, and **infrastructure modules** handle IPC, process lifecycle, and UI.

```
┌──────────────────────────────────────────────────────────┐
│  main.cpp                                                 │
│  (startup, thread orchestration, shutdown)                 │
├──────────────────────────────────────────────────────────┤
│                                                           │
│  ┌───────────┐ ┌──────────┐ ┌──────────┐ ┌────────────┐ │
│  │ transport │ │ metrics  │ │ adaptive │ │ button     │ │
│  │ (IPC)     │ │ poller   │ │ controller│ │ monitor    │ │
│  └─────┬─────┘ └────┬─────┘ └────┬─────┘ └─────┬──────┘ │
│        │             │            │              │         │
│        │    ┌────────┴────────────┘              │         │
│        │    │  domain controllers                 │         │
│        │    │  ┌───────────┐  ┌───────────┐     │         │
│        │    ├──┤ FanCtrl   │  │ TdpCtrl   │◄────┘         │
│        │    │  └───────────┘  └───────────┘               │
│        │    │  ┌───────────┐                              │
│        │    └──┤ profiles  │  (CRUD, apply, persist)      │
│        │       └───────────┘                              │
│        │    │         │              │                     │
│        │    │    shared state (mutex-protected)            │
│        │    └─────────────────────────────────────┘        │
│  ┌─────┴────┐ ┌──────────┐ ┌──────────┐                  │
│  │ protocol │ │ process  │ │ tray     │                  │
│  │ (JSON)   │ │ manager  │ │          │                  │
│  └──────────┘ └──────────┘ └──────────┘                  │
├──────────────────────────────────────────────────────────┤
│  platform.h (abstract interface)                          │
│  ┌────────────────┐  ┌──────────────────────────────┐    │
│  │ platform_win32 │  │ platform_linux [future]      │    │
│  └────────────────┘  └──────────────────────────────┘    │
└──────────────────────────────────────────────────────────┘
```

**Domain modules own the *what*:** register addresses, SMU opcodes, unit conversion, RPM calculation, dual-dispatch sequences. They are reused by the metrics poller, adaptive controller, and command handlers.

**Platform owns the *how*:** WMI vs sysfs, PawnIO vs MSR, Named Pipes vs Unix sockets. Shared code never touches platform-specific APIs directly.

### Modules

```
backend/
  src/
    main.cpp              — Entry point, thread creation, shutdown coordination
    transport.cpp         — Accept connections, read/write JSON lines, security handshake
    protocol.cpp          — JSON parse/serialize, command dispatch, request ID correlation
    metrics.cpp           — Polling loop: composes FanCtrl, TdpCtrl, Platform into Metrics struct
    tdp.cpp               — TdpController: SMU opcodes, write sequences, TDP state
    fan.cpp               — FanController: EC registers, auto/curve mode, curve interpolation, RPM
    button.cpp            — ButtonMonitor: EC 0x0230, edge detection, APP_FUN_EN init
    adaptive.cpp          — Adaptive controller: PID, global auto_tune config, TDP/fan adjustment loop
    profiles.cpp          — Profile + fan curve CRUD, profiles.json I/O, slug generation, apply logic
    power.cpp             — Power state detection (EC 0x04FE), charge limit read/write (EC 0x04A3), auto-profile switch
    process.cpp           — Frontend spawn, lifecycle monitoring, crash respawn
    tray.cpp              — System tray icon, context menu, tooltip updates
    platform/
      platform.h          — Abstract interface: transport, EC, SMU, charge limit, tray, process mgmt
      platform_win32.cpp  — Windows implementations (Named Pipes, WMI, PawnIO, Super I/O, Job Object)
      platform_linux.cpp  — Linux implementations (Unix sockets, sysfs, MSR, process groups) [future]
    shared.h              — Shared state structs, mutex, metric types (platform-neutral)
    protocol.h            — Message types, JSON serialization
  lib/
    PawnIOLib.lib         — PawnIO static lib (Windows)
    RyzenSMU.bin          — Signed SMU blob (LGPL-2.1, from PawnIO.Modules)
    LpcIO.bin             — Signed Super I/O blob for EC RAM writes (LGPL-2.1, from PawnIO.Modules)
  deps/
    nlohmann/json.hpp     — JSON parsing (header-only)
    ADLX/                 — AMD ADLX SDK headers (AMD SDK License, royalty-free)
  CMakeLists.txt          — Output: xmaxsvc.exe (Windows) or xmaxd (Linux)
```

### Module responsibilities

| Module | Owns | Depends on |
|--------|------|-----------|
| `main.cpp` | Startup sequence, thread creation, shutdown coordination | Everything |
| `transport.cpp` | Accept connections, read/write JSON lines, security handshake | `protocol.h`, `Platform` (peer verification) |
| `protocol.cpp` | JSON parse/serialize, command dispatch to handlers, request ID matching | `shared.h`, nlohmann/json |
| `metrics.cpp` | Polling loop, composing domain controllers + platform into unified `Metrics` struct | `FanController`, `TdpController`, `Platform` (RAM, CPU, GPU) |
| `tdp.cpp` | `TdpController`: SMU opcodes, dual-dispatch write sequences, TDP limits | `Platform` (`smu_send`) |
| `fan.cpp` | `FanController`: EC register addresses, auto/curve mode, RPM read, fan curve interpolation loop | `Platform` (`ec_read`, `ec_write`), `shared.h` |
| `button.cpp` | `ButtonMonitor`: EC 0x0230 edge detection, APP_FUN_EN init | `Platform` (`ec_read`) |
| `adaptive.cpp` | PID controller, global auto_tune config from `config.json`, TDP/fan adjustment loop, power state bound clamping | `shared.h`, `TdpController`, `FanController` |
| `profiles.cpp` | Profile + fan curve CRUD, `profiles.json` load/save, profile application logic (TDP + fan only), slug generation | `TdpController`, `FanController`, `shared.h` |
| `power.cpp` | Power state detection (EC `0x04FE`), state change tracking, auto-profile switching, charge limit read/write | `Platform` (`ec_read`, `charge_limit_write`), `profiles.cpp`, `shared.h` |
| `process.cpp` | Frontend spawn, Job Object/process groups, crash detection + respawn | `Platform` (spawn, show/hide) |
| `tray.cpp` | Tray icon, context menu, tooltip updates | `Platform` (tray), `shared.h` |
| `shared.h` | `Metrics`, `FanState`, `TdpState`, `PowerState`, shared mutex — **platform-neutral types only** | Nothing |
| `platform.h` | Abstract interface — the only include shared code touches for hardware | Nothing |

### Hardware access summary

| Capability | Domain module | Windows source (proven) | Linux source (future) |
|-----------|--------------|------------------------|----------------------|
| RAM metrics | `Platform` | `driverfree_test.cpp` — `GlobalMemoryStatusEx` | `sysinfo()` or `/proc/meminfo` |
| CPU utilization | `Platform` | `driverfree_test.cpp` — `GetSystemTimes` | `/proc/stat` delta |
| CPU clock/name | `Platform` | `driverfree_test.cpp` — WMI `Win32_Processor` | `/proc/cpuinfo`, `cpufreq` sysfs |
| CPU temperature | `Platform` | `driverfree_test.cpp` — WMI EC `0x0470` | `hwmon` sysfs or EC via `ec_sys` |
| GPU metrics | `Platform` | Official AMD ADLX SDK (COM interfaces, no DLL) | `amdgpu` sysfs (`/sys/class/drm/`) |
| CPU package power | `TdpController` | `tdp_test.cpp` — PawnIO + SMU mailbox | `/dev/cpu/0/msr` or `amd_pstate` sysfs |
| TDP control | `TdpController` | `tdp_test.cpp` — PawnIO + SMU mailbox | `/dev/cpu/0/msr` or `amd_pstate` sysfs |
| Fan control | `FanController` | `fan_status.ps1` — EC `0x044A/0x044B/0x0476/0x0477` | EC via `ec_sys` or `/dev/port` |
| Button detect | `ButtonMonitor` | `button_detect.cpp` — EC `0x0230` toggle | EC via `ec_sys` or `/dev/port` |
| Power supply mode | `Platform` | EC `0x04FE` (see HARDWARE.md §4) | EC or `/sys/class/power_supply/` |
| Charge limit (read) | `Platform` | EC `0x04A3` via UMAInterface (driver-free) | EC or `/sys/class/power_supply/` |
| Charge limit (write) | `Platform` | PawnIO + `LpcIO.bin` Super I/O (see HARDWARE.md §5) | EC or sysfs |

### Tray icon

- Left-click: toggle frontend visibility (see [Frontend visibility](#frontend-visibility))
- Right-click: context menu (Show Frontend, Restart Frontend, Quit)
- Tooltip: `"45W | 79°C | Gaming"` (TDP, CPU temp, active profile name if applicable) — updated at 1Hz max (Windows `Shell_NotifyIcon` rate limit)
- Icon: custom icon embedded in the binary (`.ico` resource on Windows, `.svg`/`.png` on Linux)

### Adaptive controller

A global backend thread that dynamically adjusts TDP and fan speed to track a user-set target temperature. There is one adaptive configuration — it operates independently of user profiles and across all power states. User profiles are static hardware configs (TDP + fan curve); they only take effect when adaptive is disabled. The frontend configures the adaptive controller and observes — the backend owns the control loop.

**Algorithm:** Two nested loops — an inner PID controls fan speed to maintain target temp, an outer loop adjusts TDP only when the fan runs out of headroom. Asymmetric smoothing (fast rise, slow fall) absorbs transient spikes without overreacting.

**Three tuning presets**, same algorithm, different tuning:

| Preset | Priority | Fan | TDP | Use case |
|--------|----------|-----|-----|----------|
| **Silent** | Quiet | Hard ceiling (user-set, e.g., 40%) | Primary control — gently throttled | Night use, shared spaces |
| **Default** | Balance | Minimized while holding target temp | Maximized when fan has headroom | Everyday gaming |
| **Performance** | Max TDP | Allowed to go high, smooth-controlled | Primary goal — maximized | Demanding games |

All presets share a user-set target temperature (e.g., 85°C). Temps can briefly exceed it; sustained overshoot triggers intervention. Safety overrides (critical temp, sensor failure) bypass all smoothing.

**Source:** `backend/src/adaptive.cpp`

Full algorithm details, tuning parameters, IPC interface, and implementation notes → [ADAPTIVE.md](ADAPTIVE.md)

### Power states

The device has 4 detectable power states (read from EC register `0x04FE`, driver-free via UMAInterface WMI). Each power state has a **user profile** (static TDP + fan curve) and **adaptive TDP bounds** assigned to it. When the power source changes and persist is enabled, the backend auto-applies the assigned profile. If adaptive is active, only the TDP bounds are recalculated — the profile is not re-applied.

| State ID | EC 0x04FE | Label | Description | Typical TDP headroom |
|----------|-----------|-------|-------------|---------------------|
| `battery` | `1` | Battery only | No charger connected | Lowest (55W/70W) |
| `usb_c_slow` | `8`/`9` | USB-C (65W class) | USB-C charger <100W (device requires ≥90W) | Low (~50W draw) |
| `usb_c_fast` | `2`/`3` | USB-C (100W class) | USB-C charger ≥100W | Medium (~90W draw) |
| `dc_in` | `4`/`5`/`0x85` | DC-In (dedicated) | Proprietary barrel charger | Full (120W+/140W+) |

**Profile assignment:** Every power state maps to a saved user profile and adaptive TDP bounds. The profile controls static hardware (TDP + fan curve) when a profile is the active mode. The TDP bounds clamp the global adaptive controller when adaptive is the active mode. On first launch, `config.json` doesn't exist yet — the backend does nothing (hardware at BIOS defaults). The user creates profiles and assigns power-state mappings through the frontend; subsequent boots with persist=true will use them.

```json
{
  "power_state_profiles": {
    "battery": {
      "profile": "battery-saver",
      "tdp_max_w": 25
    },
    "usb_c_slow": {
      "profile": "usb-c-efficient",
      "tdp_max_w": 35
    },
    "usb_c_fast": {
      "profile": "balanced",
      "tdp_max_w": 45
    },
    "dc_in": {
      "profile": "performance",
      "tdp_max_w": 55
    }
  }
}
```

All four keys are required — no `null` values. All values are user-configurable via the frontend. Stored in `config.json`.

**Adaptive TDP ceiling:** The power state's `tdp_max_w` is a hard ceiling that clamps the global adaptive controller's configured ceiling:

```
effective_tdp_max = min(auto_tune.tdp_max_w, power_state.tdp_max_w)
```

The adaptive controller's `tdp_max_w` (global, in `config.json`) is the user's desired ceiling. The power state's `tdp_max_w` is a per-source hard limit. The lower ceiling wins. When the power state changes, the effective ceiling is recalculated immediately — the adaptive controller keeps running, just within the new constraint.

**Mutually exclusive control:** Adaptive and profiles are mutually exclusive — exactly one is always active (radio-button model). Selecting a tuning preset activates adaptive and deactivates the current profile. Selecting a profile deactivates adaptive. There is no way to deselect — you can only switch from one to another. Before the user makes their first selection (fresh install, persist=false), nothing is active and hardware stays at BIOS defaults.

```
Adaptive controller (clamped by power state) | Active profile (static) → exactly one active → BIOS defaults (no selection yet)
```

**Detection:** Backend polls EC `0x04FE` at the metrics poll rate (2000ms). On state change:
1. Emit `power_mode_change` event to frontend (always — reporting/metrics only when persist=false)
2. If adaptive controller is active: recalculate effective TDP ceiling using the new power state's `tdp_max_w` (adaptive keeps running, just re-clamped)
3. If `persist` is enabled and adaptive is not active: apply `power_state_profiles[state]` (static profile)
4. If `persist` is disabled: do nothing (hardware stays at current state)

**Source:** `backend/src/power.cpp` (reads EC, tracks state, triggers profile switch)

### Profiles & Fan Curves

Profiles and fan curves are stored in `profiles.json`. Both use **immutable slug IDs** as keys — derived from the name at creation time (lowercase, spaces→hyphens, strip special chars). Renaming changes the `name` field only; the slug key never changes. Collisions get a numeric suffix (`gaming-2`).

#### Safe defaults (hardcoded)

Safe defaults are **hardcoded constants in the backend** — not stored in `config.json` or `profiles.json`. They represent the BIOS/firmware defaults for this specific device. They are **not applied automatically** on startup or shutdown. They exist solely as a user-triggered "Revert to system defaults" action from the frontend.

The exact values must be read from the hardware after a clean cold boot (before any app intervention) and stored as compile-time constants (e.g., `defaults.h` or a read-only resource file).

**Safe defaults (BIOS defaults) — TBD per device:**

| Value | Register / Source | Expected BIOS default |
|-------|-------------------|----------------------|
| STAPM TDP | SMU mailbox read | TBD — read after clean boot |
| Fast TDP | SMU mailbox read | TBD |
| Slow TDP | SMU mailbox read | TBD |
| Fan mode | EC `0x044A` | 0 (auto / BIOS-controlled) |
| Fan speed | EC `0x044B` | TBD — read after clean boot |
| Charge limit | EC `0x04A3` | 100% (no limiting) |

These values are device-specific (Strix Halo). Different hardware will have different BIOS defaults.

**First launch with persist=true:** `config.json` and `profiles.json` don't exist yet. The backend does nothing (hardware at BIOS defaults, same as persist=false). The user creates profiles and power-state mappings through the frontend; subsequent boots with persist=true will use them.

#### Fan Curves

A fan curve maps temperature to fan speed. The backend interpolates linearly between points.

```json
{
  "fan_curves": {
    "quiet": {
      "name": "Quiet",
      "points": [
        { "temp_c": 40, "speed_pct": 15 },
        { "temp_c": 60, "speed_pct": 25 },
        { "temp_c": 75, "speed_pct": 35 },
        { "temp_c": 85, "speed_pct": 40 }
      ]
    },
    "aggressive": {
      "name": "Aggressive",
      "points": [
        { "temp_c": 40, "speed_pct": 30 },
        { "temp_c": 55, "speed_pct": 50 },
        { "temp_c": 65, "speed_pct": 70 },
        { "temp_c": 75, "speed_pct": 90 },
        { "temp_c": 85, "speed_pct": 100 }
      ]
    }
  }
}
```

**Curve rules:**
- Minimum 2 points, maximum 10
- Points must be sorted by ascending `temp_c`
- `speed_pct` range: 0–100
- Below first point: use first point's speed
- Above last point: use last point's speed
- Between points: linear interpolation
- Temperature source: `max(cpu_temp, gpu_temp)` from metrics

#### Profiles

A profile is a named combination of TDP limits and a fan curve reference. Profiles are **static hardware configs** — they set fixed TDP values and fan behavior. The adaptive controller is a separate global subsystem (configured in `config.json`, not inside profiles). Adaptive and profiles are mutually exclusive (radio-button model): selecting a profile deactivates adaptive, activating adaptive deactivates the current profile. Exactly one is always active after the user's first selection.

```json
{
  "profiles": {
    "gaming": {
      "name": "Gaming",
      "tdp": { "stapm": 45, "fast": 50, "slow": 45 },
      "fan_curve": "aggressive"
    },
    "night": {
      "name": "Night",
      "tdp": { "stapm": 25, "fast": 30, "slow": 25 },
      "fan_curve": "quiet"
    },
    "max-perf": {
      "name": "Max Performance",
      "tdp": { "stapm": 55, "fast": 65, "slow": 55 },
      "fan_curve": null
    }
  }
}
```

**Field semantics:**

| Field | Type | Description |
|-------|------|-------------|
| `name` | string | User-visible display name |
| `tdp` | `{stapm, fast, slow}` | TDP limits in watts (hardware ceiling) |
| `fan_curve` | string \| null | Slug reference to a fan curve, or null |

**Behavior matrix (when profile is active and adaptive is disabled):**

| `fan_curve` | Fan control | TDP control |
|-------------|-------------|-------------|
| `"aggressive"` | Backend runs curve interpolation loop | Fixed at profile TDP values |
| `null` | BIOS auto (firmware controls fan) | Fixed at profile TDP values |

**Applying a profile (`set_profile`):**

1. Resolve fan curve from `fan_curves` (if referenced)
2. Write TDP limits (stapm, fast, slow) to hardware via TdpController
3. If `fan_curve` is set: backend starts fan curve interpolation loop
4. If `fan_curve` is null: set fan to BIOS auto mode

Note: applying a profile deactivates adaptive (mutually exclusive). The profile's TDP and fan settings take effect immediately.

#### Deletion constraints

- A fan curve cannot be deleted if any profile references it
- A profile cannot be deleted if any power state references it
- The FE should warn the user and show which profiles/power states reference the item

### Frontend lifecycle (Windows)

The frontend is spawned **hidden** at startup — it loads in the background so the first button press or tray click shows it instantly (no cold-start delay).

```
xmaxsvc starts
  ├── Create Job Object (KILL_ON_JOB_CLOSE)
  ├── Create named mutex (single instance)
  ├── Initialize hardware (WMI, PawnIO + RyzenSMU.bin + LpcIO.bin, ADLX)
  ├── Detect power state (EC 0x04FE)
  ├── If "persist" enabled: apply charge limit, apply power-state profile, restore global auto_tune (all from config.json, clamped by power state)
  ├── If "persist" disabled: do nothing (hardware at BIOS defaults)
  ├── Spawn xmax.exe hidden, assign to job
  ├── Store frontend PID + process handle
  └── Enter main loop

Frontend crashes (handle signaled)
  └── Respawn hidden after 1s delay

xmaxsvc exits
  └── Job Object closes → OS kills frontend automatically
```

**Hardware state resets to BIOS defaults on every reboot.** SMU TDP limits, EC fan settings, and EC charge limit are all volatile — they reset to firmware defaults on power loss. The app does not need to restore safe defaults on startup or shutdown. With persist=false, the app makes zero hardware writes. With persist=true, the app applies user-configured settings on startup. On reboot, the hardware resets itself.

## Frontend

### Windows (current) — WinUI 3 / C#

### Structure (Windows)

```
frontend/windows/
  App.xaml                — Application entry, single-instance redirect
  MainWindow.xaml         — Main window layout, bottom tab navigation
  MainWindow.xaml.cs      — Window logic, positioning, click-outside-to-hide
  Pages/
    HomePage.xaml         — Quick settings with configurable widgets
    ProfilesPage.xaml     — Profile CRUD, fan curve editor, power-state assignment
    SettingsPage.xaml     — Language, theme, persist, auto-start, widget layout
  Widgets/
    IHomeWidget.cs        — Widget interface (WidgetId, Control)
    ProfilesWidget.xaml   — Profile quick-select tiles
    MetricsWidget.xaml    — CPU/GPU/RAM gauge tiles
    AdaptiveWidget.xaml   — Tuning selector + TDP slider
    ChargeLimitWidget.xaml— Cycling charge limit button
    PowerWidget.xaml      — Power source + TDP ceiling slider
  Services/
    PipeClient.cs         — Named pipe connection, command/response, event handling
    MetricsService.cs     — Receives metrics, update UI via data binding
    ProfileService.cs     — Profile CRUD, active profile tracking
    AutoTuneService.cs    — Adaptive controller state
    WidgetService.cs      — Widget registration, order, visibility
  Models/
    Metrics.cs            — C# classes matching JSON metrics payload
    Profile.cs            — Profile model (id, name, tdp, fan_curve ref)
    FanCurve.cs           — Fan curve model (id, name, points)
    PowerState.cs         — Power state model
    AutoTuneConfig.cs     — Adaptive controller config model
  ViewModels/
    HomeViewModel.cs      — Widget orchestration
    AdaptiveViewModel.cs  — Adaptive selector bindings + set_auto_tune commands
    ProfilesViewModel.cs  — Profile list, CRUD, apply, power-state assignment
    SettingsViewModel.cs  — App settings bindings
  Converters/             — XAML value converters
  Assets/                 — Icons, images
  frontend.csproj         — WinUI 3 project (AssemblyName: xmax)
```

### Pipe client

`PipeClient.cs` is the single point of communication:

```
Connect to \\.\pipe\xmaxsvc
  ├── Send JSON commands, await JSON responses (5s timeout)
  ├── Receive unsolicited events (button_press, metrics push)
  ├── Auto-reconnect on disconnect (with backoff)
  │     └── On reconnect: get_metrics snapshot → subscribe_metrics → raise connected event
  └── Raise C# events for UI binding
```

### Frontend visibility

The frontend is a **Quick Settings-style popup** — frameless, translucent backdrop (Mica/Acrylic), rounded corners, no title bar. It does not minimize or appear in the taskbar. It simply shows and hides.

**Two triggers, one behavior (toggle show/hide):**

| Trigger | Source | How |
|---------|--------|-----|
| **Hardware button** | EC register 0x0230 state change → button monitor thread | Edge detect only — register value discarded |
| **Tray icon left-click** | `Shell_NotifyIcon` / `AppIndicator` callback | Same toggle function |

Both triggers go through the same code path — a single toggle function in the backend. The backend tracks visibility state internally (`bool fe_visible`) because two independent triggers can change it; the EC register's toggle value (0x00↔0x06) is **not** the source of truth for visibility.

The button monitor polls register 0x0230 at 100ms. Any state change (0x00→0x06 or 0x06→0x00) means "a press happened" — the new value is discarded. This is pure edge detection.

**Windows (current):** Backend uses `ShowWindow(hwnd, SW_SHOW/SW_HIDE)`. Frontend HWND found via `CreateProcess` + `EnumWindows` (match by PID). Window is `OverlappedPresenter` — frameless, always-on-top, non-resizable.

**Linux (future):** Backend sends a D-Bus signal or X11/Wayland event to the frontend process. Mechanism TBD based on display server.

### UI framework decision (Windows)

WinUI 3 is the target but the frontend structure is designed so that:
- `PipeClient.cs` and `Models/` are framework-agnostic (work with WPF, WinUI 3, Avalonia)
- Only `Pages/`, `ViewModels/`, and XAML files are WinUI 3-specific
- Swapping to a different XAML framework later would only require rewriting the view layer

### Linux (future)

The Linux frontend will be a separate project — likely **GTK4/libadwaita** in C++ or Rust, matching the GNOME HIG for handheld/desktop Linux. The IPC protocol is identical; only the UI layer and pipe client transport differ. Framework decision deferred until Linux work begins.

## Build & Run

### Windows (current)

**Backend:**
```bash
cd backend
cmake -B build -G "Visual Studio 17 2022" -A x64
cmake --build build --config Release
```
Output: `xmaxsvc.exe` (standalone, no runtime dependency)

**Frontend:**
```bash
cd frontend/windows
dotnet build -c Release -f net10.0-windows10.0.19041.0
dotnet publish -c Release -r win-x64 --self-contained
```
Output: `xmax.exe` (self-contained, includes WinUI 3 runtime)

**Running:**
```bash
xmaxsvc.exe          # Starts backend + spawns frontend automatically
```
Kill `xmaxsvc.exe` → frontend is killed automatically (Job Object).

### Linux (future)

**Backend:**
```bash
cd backend
cmake -B build -G Ninja
cmake --build build --config Release
```
Output: `xmaxd` (standalone binary)

**Frontend:** TBD (depends on framework choice — GTK4/Qt).

**Running:**
```bash
./xmaxd              # Starts backend + spawns frontend automatically
```
Kill `xmaxd` → frontend receives `SIGTERM` via process group.

## Testing

### Backend (C++ / Google Test)

**Framework:** Google Test, linked via CMake FetchContent. Test binary at `backend/tests/`.

**Test categories:**

| Category | Hardware required | Examples |
|----------|------------------|---------|
| **Unit tests** | No | Protocol parsing, config validation, fan curve interpolation, slug generation, deletion constraints |
| **Hardware tests** | Yes (OneXPlayer Super X) | EC read/write, SMU mailbox, charge limit, power state detection, Named Pipe transport |

Backend tests are designed to run on the target device. Devs testing the backend have the real hardware — no mocking needed for platform-layer tests.

**Running tests:**
```bash
cd backend
cmake -B build -G "Visual Studio 17 2022" -A x64
cmake --build build --config Release
./build/tests/Release/xmaxsvc_tests.exe
```

### Frontend (C# / xUnit)

**Framework:** xUnit, via `xmax.Tests/` project. Run with `dotnet test`.

**Test scope:** Logic only — PipeClient protocol handling, model deserialization, ViewModel state, widget framework ordering/visibility. No WinUI 3 rendering or UI interaction tests.

**Running tests:**
```bash
cd frontend/windows
dotnet test
```

## Files from TEST/ to promote to production code

These proven test scripts form the basis of the **Windows** platform implementation. Linux equivalents will be developed from platform documentation and hardware testing.

| TEST/ file | Target module | What to extract |
|-----------|---------------|-----------------|
| `driverfree_test.cpp` | `platform_win32.cpp` | WMI COM helpers, `GlobalMemoryStatusEx`, `GetSystemTimes`, `ec_read` |
| `tdp_test.cpp` | `platform_win32.cpp` | PawnIO init, SMU mailbox protocol, HC-style write sequence |
| `button_detect.cpp` | `platform_win32.cpp` | WMI COM init, `ec_read`, toggle detection loop |
| `adlx_test.cs` | `platform_win32.cpp` | ADLX struct definitions, `GetAdlxTelemetry` call pattern |
| `fan_status.ps1` | `platform_win32.cpp` | EC register addresses, RPM calculation, mode logic |
| `power_scan.ps1` | `power.cpp` | EC `0x04FE` power state detection, 4-state decode logic |
| `charge_limit_pawnio.cpp` | `power.cpp` + `platform_win32.cpp` | WMI COM EC read, PawnIO + IT87 Super I/O charge limit write, `LpcIO.bin` load |

## Translations

The FE supports **English** and **中文 (简体)**. The BE is unaware of translations — they are a FE concern.

### Single source of truth

All translatable strings are defined in shared JSON files — one per language. A generator script converts them into each platform's native format at build time. No one edits `.resx`, `.po`, or `.ts` files directly.

```
APP/
  shared/
    locales/
      en.json              ← all UI strings in English
      zh.json              ← all UI strings in Chinese
    errors.json            ← error codes (separate from translations)
    generate_errors.py     ← generates error enums
    generate_locales.py    ← generates platform-native translation files
```

**`en.json`:**
```json
{
  "dashboard.title": "Dashboard",
  "tdp.slider_label": "TDP Limit: {0}W",
  "fan.mode_auto": "Auto",
  "tuning.silent": "Silent",
  "tuning.default": "Default",
  "tuning.performance": "Performance",
  "settings.language": "Language",
  "settings.language_auto": "Auto (System)",

  "error.tdp_out_of_range": "TDP value outside supported range (6–120W)",
  "error.fan_speed_invalid": "Fan speed must be between 0 and 255",
  "error.charge_limit_invalid": "Charge limit must be between 75 and 100%",
  "error.charge_limit_write_fail": "Failed to set charge limit",
  "error.hardware_busy": "Hardware is busy, try again",
  "error.sensor_unavailable": "Sensor data unavailable",
  "error.profile_not_found": "Profile not found",
  "error.fan_curve_not_found": "Fan curve not found",
  "error.fan_curve_in_use": "Fan curve is used by a profile — remove the reference first",
  "error.profile_in_use": "Profile is assigned to a power state — remove the assignment first",
  "error.fan_curve_invalid": "Fan curve must have 2–10 points sorted by temperature",
  "error.persist_disabled": "Hardware controls are disabled. Enable persist in settings to apply changes",
  "error.unknown": "Something went wrong"
}
```

**`zh.json`:**
```json
{
  "dashboard.title": "仪表盘",
  "tdp.slider_label": "TDP限制：{0}W",
  "fan.mode_auto": "自动",
  "tuning.silent": "静音",
  "tuning.default": "默认",
  "tuning.performance": "性能",
  "settings.language": "语言",
  "settings.language_auto": "自动（跟随系统）",

  "error.tdp_out_of_range": "TDP值超出支持范围（6–120W）",
  "error.fan_speed_invalid": "风扇转速必须在0到255之间",
  "error.charge_limit_invalid": "充电限制必须在75%到100%之间",
  "error.charge_limit_write_fail": "设置充电限制失败",
  "error.hardware_busy": "硬件繁忙，请重试",
  "error.sensor_unavailable": "传感器数据不可用",
  "error.profile_not_found": "未找到配置文件",
  "error.fan_curve_not_found": "未找到风扇曲线",
  "error.fan_curve_in_use": "该风扇曲线被配置文件引用，请先移除引用",
  "error.profile_in_use": "该配置已分配给电源状态，请先移除分配",
  "error.fan_curve_invalid": "风扇曲线必须包含2–10个按温度排序的点",
  "error.persist_disabled": "硬件控制已禁用。请在设置中启用持久化以应用更改",
  "error.unknown": "发生了错误"
}
```

Keys use dot-separated namespaces (`dashboard.title`, `error.tdp_out_of_range`). The FE maps BE error codes to locale keys by prepending `error.` — e.g., `tdp_out_of_range` → `error.tdp_out_of_range`. The `error.unknown` key is the fallback for any unrecognized error code.

### Generated output

| Platform | Generated format | Compiled | Access in code |
|----------|-----------------|----------|----------------|
| Windows (WinUI 3) | `.resx` (XML) | Satellite assemblies | `Strings.Key` or `x:Uid` in XAML |
| Linux (GTK4) | `.po` (plain text) | `.mo` binary | `_("string")` via gettext |
| Linux (Qt) | `.ts` (XML) | `.qm` binary | `tr("string")` via Qt Linguist |

```
# Generated at build time (do not edit manually)
frontend/windows/Resources/
  Strings.en.resx
  Strings.zh.resx

xmax-gtk/po/              # future
  en.po
  zh_CN.po
```

UI strings and error messages share the same locale files — error keys use the `error.` prefix. The BE error code `tdp_out_of_range` maps to the locale key `error.tdp_out_of_range`.

### Language detection

All frameworks handle auto-detection natively:

| Platform | Mechanism |
|----------|-----------|
| Windows | `CultureInfo.CurrentUICulture` — defaults to system UI language, .NET selects the right `.resx` automatically |
| Linux (GTK4) | `setlocale()` reads `LC_ALL` > `LC_MESSAGES` > `LANG` env vars |
| Linux (Qt) | `QLocale::system()` reads the same env vars |

No custom detection code needed — the frameworks resolve the correct translation automatically.

### Manual override

`config.json` stores the user's language preference:

```json
{
  "language": "auto"
}
```

Values: `"auto"` (follow system), `"en"`, `"zh"`.

The FE settings page shows a dropdown:

| Display | Value |
|---------|-------|
| Auto (System) | `auto` |
| English | `en` |
| 中文 (简体) | `zh` |

**Override is applied before UI initialization:**

- **Windows:** Set `CultureInfo.DefaultThreadCurrentUICulture` before loading any XAML. .NET ResourceManager uses this to select `.resx` files.
- **Linux:** Set `LANGUAGE` env var (or equivalent) before `setlocale()` / `QTranslator` init.

Changing the language requires an app restart — resource lookups are cached at load time.

### Fallback chain

All frameworks fall back gracefully if a translation is missing:

```
Exact match (zh) → Language root (zh) → Default (en)
```

English is always the fallback language and must be complete. Other languages may have gaps — missing keys fall back to English rather than showing empty strings.

## Config & Data Storage

Per-user data stored in the platform-standard local app data directory (device-specific, not synced, writable without admin):

| Platform | Path |
|----------|------|
| **Windows** (current) | `%LOCALAPPDATA%\xmax\` |
| **Linux** (future) | `$XDG_DATA_HOME/xmax/` (typically `~/.local/share/xmax/`) |

```
config.json           — App settings, global preferences, charge limit, adaptive controller config, power-state profiles
profiles.json         — Saved profiles and fan curves (see Profiles & Fan Curves section)
```

**Config validation & error handling:**

On startup, the backend validates both `config.json` and `profiles.json`. If either file is missing, malformed JSON, or contains invalid values (unknown keys, out-of-range values, broken slug references), the backend replaces the corrupted portions with hardcoded built-in defaults and writes the corrected file back to disk. If the entire file is unrecoverable, it is replaced wholesale with defaults. Invalid profile slug references in `power_state_profiles` are dropped (the affected power state gets no profile assignment until the user reconfigures it). Built-in defaults mirror a passive/no-op configuration: `persist: false`, `auto_tune: null`, no profiles, no fan curves. The app continues to function — the user reconfigures through the frontend.

**`config.json` — full format:**

```json
{
  "language": "auto",
  "theme": "system",
  "persist": true,
  "charge_limit_pct": 85,
  "auto_start": true,
  "auto_tune": {
    "enabled": true,
    "tuning": "performance",
    "target_temp_c": 85,
    "tdp_max_w": 55,
    "fan_max_pct": 100
  },
  "power_state_profiles": {
    "battery": {
      "profile": "battery-saver",
      "tdp_max_w": 25
    },
    "usb_c_slow": {
      "profile": "usb-c-efficient",
      "tdp_max_w": 35
    },
    "usb_c_fast": {
      "profile": "balanced",
      "tdp_max_w": 45
    },
    "dc_in": {
      "profile": "performance",
      "tdp_max_w": 55
    }
  }
}
```

| Setting | Type | Description |
|---------|------|-------------|
| `language` | string | `"auto"`, `"en"`, or `"zh"` |
| `theme` | string | `"system"`, `"light"`, or `"dark"` |
| `persist` | bool | Use user-configured power-state profiles on startup (false = no writes, hardware at BIOS defaults) |
| `charge_limit_pct` | int | Battery charge limit (75–100). Applied on startup only when `persist` is true |
| `auto_start` | bool | Launch at user logon (Task Scheduler / systemd) |
| `auto_tune` | object \| null | Global adaptive controller config: `{enabled, tuning, target_temp_c, tdp_max_w, fan_max_pct}`. TDP ceiling is clamped by current power state at runtime |
| `power_state_profiles` | object | Power state → `{profile, tdp_max_w}` mapping (all four states required, no nulls). Only used when `persist` is true |

**Persist toggle:**

| `persist` | Behavior |
|-----------|----------|
| `true` | On startup: detect power state → apply charge limit from `config.json` → apply profile from `config.json` `power_state_profiles[state]` → restore global `auto_tune` from `config.json` (ceiling clamped by power state) |
| `false` | On startup: do nothing (hardware at BIOS defaults) |

**Persist controls all hardware writes.** When persist is disabled, the app makes zero hardware writes — startup, runtime power-state changes, and user-initiated commands are all rejected (see [Persist gating](#persist-gating)). The app is a passive metrics observer. The hardware stays at BIOS defaults after reboot. If the user wants no app intervention at all, they should disable auto-start.

**Safe defaults (BIOS defaults)** are hardcoded constants in the backend — they represent the firmware/hardware defaults for this device. They are NOT applied on startup or shutdown. They exist solely as a "revert to system defaults" option the user can trigger from the frontend. The exact values are device-specific and must be read from the hardware after a clean boot (before any app intervention) and stored as compile-time constants (e.g., `defaults.h`).

**Why local (not roaming) storage:**
- Profiles are device-specific (TDP/fan curves tied to exact hardware) — roaming sync would push Strix Halo profiles to unrelated machines
- Survives app reinstalls/updates
- Writable without elevation (unlike same-dir-as-exe under Program Files)
- Follows platform conventions (`%LOCALAPPDATA%` on Windows, `$XDG_DATA_HOME` on Linux)

## Auto-start

### Windows (current)

Task Scheduler task created at install time:

- **Task name:** `XmaX Service`
- **Trigger:** At user logon
- **Action:** Run `xmaxsvc.exe` from install directory
- **Privileges:** "Run with highest privileges" (avoids UAC prompt on every boot)
- **Settings:** Restart on failure (1 min delay, 3 retries), stop if running on battery is configurable
- **User control:** Enabled/disabled from frontend Settings page, or via `schtasks /Change /TN "XmaX Service" /Disable`

### Linux (future)

systemd user unit (`~/.config/systemd/user/xmaxd.service`) enabled via `systemctl --user enable xmaxd`. Root privileges for hardware access handled via `polkit` rules rather than running as root.

## Open questions

- **NPU metrics**: ADLX reports 0 on Strix Halo. Revisit when NPU workloads are available.
- **GPU clock control**: currently disabled in config. SMU mailbox can write it — include?
- **Linux EC access**: Will `ec_sys` kernel module or `/dev/port` I/O work on Strix Halo handhelds running Linux, or will a custom kernel module be needed? Needs testing on actual hardware.
- **Linux TDP control**: `amd_pstate` driver sysfs interface vs direct MSR writes via `/dev/cpu/0/msr` — which provides the SMU mailbox access needed for STAPM/FAST/SLOW limits?
- **Linux frontend framework**: GTK4/libadwaita (GNOME HIG) vs Qt/KDE (Plasma Mobile) — depends on which DE the target hardware runs.
