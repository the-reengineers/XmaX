# XmaX Implementation Plan

This plan implements the architecture defined in [PROJECT.md](PROJECT.md), [ADAPTIVE.md](ADAPTIVE.md), and [FRONTEND.md](FRONTEND.md).

**Execution model:** Backend first (fully testable via terminal), then frontend. Each step is sequential — complete one step before starting the next.

**Testing:** Google Test (backend), xUnit (frontend). Backend tests run on real hardware (OneXPlayer Super X / AMD Strix Halo).

---

## Phase 1: Backend Foundation

### Step 1: Project scaffolding and build system

**Deliverable:** Working CMake project that compiles an empty executable.

**Tasks:**
- Create `backend/` directory structure:
  ```
  backend/
    src/
    tests/
    lib/
    deps/
    CMakeLists.txt
  ```
- Set up CMake with C++17 or C++20
- Add nlohmann/json as header-only dependency (copy to `deps/` or use FetchContent)
- Add Google Test via FetchContent
- Create empty `main.cpp` that returns 0
- Create empty test file `tests/test_main.cpp` with one passing test
- Verify: `cmake -B build && cmake --build build && ./build/xmaxsvc` runs
- Verify: `./build/tests/xmaxsvc_tests` runs and passes

**Tests:** One trivial test to verify Google Test is working.

---

### Step 2: Shared types and protocol

**Deliverable:** Protocol layer that can parse and serialize all IPC messages.

**Files:**
- `src/shared.h` — Metrics, FanState, TdpState, PowerState structs (platform-neutral types only)
- `src/protocol.h` — Message types (Command, Response, Event, Error)
- `src/protocol.cpp` — JSON parse/serialize using nlohmann/json

**Tasks:**
- Define all shared state structs in `shared.h` using only standard C++ types (no HANDLE, DWORD, etc.)
- Define message type enums and structs in `protocol.h`
- Implement JSON serialization/deserialization in `protocol.cpp`:
  - Parse incoming JSON lines → Command struct
  - Serialize Response/Event/Error structs → JSON strings
  - Handle malformed JSON gracefully (return Error with `parse_error` code)
- Implement request ID correlation (echo `id` from request to response)

**Tests:**
- Parse valid command JSON → correct Command struct
- Parse malformed JSON → Error response
- Serialize Response → valid JSON with correct fields
- Serialize Event → valid JSON
- Request ID is echoed correctly
- Unknown command returns `unknown_command` error

---

### Step 3: Platform abstraction interface

**Deliverable:** Abstract Platform interface that defines all hardware and OS operations.

**Files:**
- `src/platform/platform.h` — Abstract interface

**Tasks:**
- Define `Platform` class with pure virtual methods:
  - Transport: `listen()`, `verify_peer()`
  - Hardware: `ec_read()`, `ec_write()`, `smu_send()`, `charge_limit_write()`
  - GPU telemetry: `gpu_metrics()`
  - Process management: `spawn_frontend()`, `show_window()`
  - System: `tray_icon()`, `data_dir()`, `single_instance_lock()`
- Define Result<T> type for error handling (or use std::expected if C++23)
- Define all supporting types (TransportServer, PeerId, PeerInfo, ChildProcess, etc.) in platform-neutral way

**Tests:** None (interface only).

---

### Step 4: Windows platform implementation — Named Pipes

**Deliverable:** Working Named Pipe server that accepts connections and performs security handshake.

**Files:**
- `src/platform/platform_win32.cpp` — Windows-specific implementations

**Tasks:**
- Implement `listen()` — create Named Pipe `\\.\pipe\xmaxsvc` with ACL for current user
- Implement `verify_peer()`:
  - Call `GetNamedPipeClientProcessId`
  - Call `QueryFullProcessImageNameW` to verify executable is `xmax.exe`
  - Verify path is under expected install directory
- Implement `single_instance_lock()` — named mutex `Global\XmaX_SingleInstance`
  - If mutex exists, show MessageBoxW and exit
- Implement `data_dir()` — return `%LOCALAPPDATA%\xmax\`
- Handle pipe disconnection and reconnection

**Tests (real hardware):**
- Create pipe, connect from test client, verify connection succeeds
- Connect from unauthorized process (not xmax.exe), verify connection rejected
- Single instance lock: second instance shows MessageBox and exits

---

### Step 5: Config and profile storage

**Deliverable:** Load/save config.json and profiles.json with validation and corruption recovery.

**Files:**
- `src/config.cpp` — Config loading, validation, defaults
- `src/profiles.cpp` — Profile/fan curve CRUD, slug generation, persistence

**Tasks:**
- Implement config loading from `%LOCALAPPDATA%\xmax\config.json`
- Validate config schema (required fields, types, ranges)
- On corruption/missing file, replace with hardcoded defaults and write corrected file
- Implement profile loading from `profiles.json`
- Implement slug generation (lowercase, spaces→hyphens, strip special chars, handle collisions)
- Implement profile CRUD operations (create, read, update, delete)
- Implement fan curve CRUD with validation (2-10 points, sorted by temp, speed 0-100)
- Implement deletion constraints (can't delete fan curve if referenced by profile, can't delete profile if referenced by power state)
- Implement power state profile validation (all 4 states required, no nulls)

**Tests:**
- Load valid config.json → correct struct
- Load corrupted JSON → defaults applied, corrected file written
- Load missing file → defaults created
- Slug generation: "Gaming Profile" → "gaming-profile"
- Slug collision: "Gaming" twice → "gaming", "gaming-2"
- Fan curve validation: <2 points → error, unsorted → error, out of range → error
- Deletion constraint: delete fan curve referenced by profile → error
- Deletion constraint: delete profile referenced by power state → error
- Config round-trip: load → modify → save → load again → verify

---

## Phase 2: Backend Domain Logic

### Step 6: Fan curve interpolation

**Deliverable:** FanController that interpolates fan speed from temperature using curve points.

**Files:**
- `src/fan.cpp` — FanController class

**Tasks:**
- Implement fan curve interpolation:
  - Below first point: use first point's speed
  - Above last point: use last point's speed
  - Between points: linear interpolation
  - Temperature source: `max(cpu_temp, gpu_temp)`
- Implement fan mode logic:
  - Auto mode: BIOS controls fan (no interpolation)
  - Curve mode: backend runs interpolation loop (1s tick)
- Implement `set_fan()` command handler (auto/curve mode switching)
- Implement `get_fan()` — read current mode, speed, RPM from EC registers

**Tests (real hardware):**
- Interpolation: temp=50, curve=[(40,20), (60,40)] → speed=30
- Interpolation: temp=30 (below first) → speed=20 (first point)
- Interpolation: temp=80 (above last) → speed=last point's speed
- EC read: get current fan RPM → reasonable value (0-5000)
- EC write: set fan speed → verify RPM changes (within tolerance)

---

### Step 7: TDP controller

**Deliverable:** TdpController that reads and writes TDP limits via SMU mailbox.

**Files:**
- `src/tdp.cpp` — TdpController class

**Tasks:**
- Implement SMU mailbox protocol for STAPM/FAST/SLOW TDP limits
- Implement `read_tdp()` — read current limits from SMU
- Implement `write_tdp(stapm, fast, slow)` — write limits via SMU
- Validate TDP values (6-120W range for Strix Halo)
- Handle SMU errors (busy, timeout)

**Tests (real hardware):**
- Read TDP → returns current limits (reasonable values)
- Write TDP (45, 50, 45) → read back → same values
- Write TDP out of range (5W) → error
- Write TDP out of range (150W) → error

---

### Step 8: Metrics poller

**Deliverable:** Background thread that polls all sensors and updates shared Metrics struct.

**Files:**
- `src/metrics.cpp` — Metrics poller thread

**Tasks:**
- Create poller thread that runs at 2000ms intervals
- Poll CPU metrics:
  - Utilization: `GetSystemTimes` delta
  - Clock: WMI `Win32_Processor`
  - Temperature: EC `0x0470` via WMI
  - Package power: SMU mailbox
- Poll GPU metrics via ADLX `GetAdlxTelemetry`
- Poll RAM metrics via `GlobalMemoryStatusEx`
- Poll fan state (mode, speed, RPM) via FanController
- Poll power state (EC `0x04FE`) and charge limit (EC `0x04A3`)
- Update shared Metrics struct (mutex-protected)
- Handle sensor failures gracefully (set fields to null, log error)

**Tests (real hardware):**
- Poll once → all fields populated with reasonable values
- CPU temp: 30-100°C
- GPU temp: 30-100°C
- CPU util: 0-100%
- RAM: 0-total_gb
- Fan RPM: 0-5000
- Power state: one of battery/usb_c_slow/usb_c_fast/dc_in

---

### Step 9: Power state detection and charge limit

**Deliverable:** Detect power source changes and read/write charge limit.

**Files:**
- `src/power.cpp` — Power state detection, charge limit

**Tasks:**
- Implement power state detection from EC `0x04FE`:
  - Decode register value → power state enum
  - Track state changes
- Implement charge limit read from EC `0x04A3`
- Implement charge limit write via PawnIO + LpcIO.bin (Super I/O path)
- Implement auto-profile switching on power state change:
  - If persist=true and adaptive is inactive: apply `power_state_profiles[state]`
  - If adaptive is active: recalculate effective TDP ceiling
  - Emit `power_mode_change` event

**Tests (real hardware):**
- Read power state → one of the 4 states
- Read charge limit → 75-100%
- Write charge limit 85% → read back → 85%
- Unplug/plug charger → power state changes detected
- Power state change with persist=true → profile applied

---

### Step 10: Adaptive controller

**Deliverable:** Background thread running PID algorithm to dynamically adjust TDP and fan.

**Files:**
- `src/adaptive.cpp` — Adaptive controller thread

**Tasks:**
- Create controller thread (1s tick rate)
- Implement asymmetric smoothing:
  - Rising: α=0.5 (fast tracking)
  - Falling: α=0.05 (slow decay)
- Implement inner loop: fan PID controller
  - Proportional, integral (with anti-windup clamp ±100), derivative
  - Clamp output to [fan_min, fan_max]
- Implement outer loop: TDP adjustment
  - Ramp down when fan saturated AND temp > target
  - Ramp up when fan < 70% AND temp < target
  - Clamp to [TDP_MIN, effective_tdp_max]
- Implement safety overrides:
  - Critical temp (95°C): max fan, min TDP immediately
  - Sensor failure (>5s): thermal safety fallback
- Implement tuning presets (Silent/Default/Performance) with hardcoded parameters
- Implement power state TDP ceiling clamping: `min(auto_tune.tdp_max_w, power_state.tdp_max_w)`
- Emit `auto_tune_adjust` event when values change
- Implement mutual exclusivity: activating adaptive deactivates profile

**Tests (real hardware):**
- Smoothing: spike 80→92°C → smoothed rises fast, falls slow
- PID: temp > target → fan increases
- PID: temp < target → fan decreases
- Outer loop: fan at max AND temp > target → TDP decreases
- Outer loop: fan < 70% AND temp < target → TDP increases
- Safety: temp > 95°C → fan=100%, TDP=min
- Power state change: DC-IN (55W) → battery (25W) → effective ceiling recalculated
- Activate adaptive → profile deactivated
- Activate profile → adaptive deactivated

---

## Phase 3: Backend Integration

### Step 11: Button monitor

**Deliverable:** Background thread that polls hardware button and toggles frontend visibility.

**Files:**
- `src/button.cpp` — ButtonMonitor thread

**Tasks:**
- Create monitor thread (100ms poll rate)
- Poll EC register `0x0230`
- Detect state changes (edge detection — any change means press)
- Track frontend visibility state (`bool fe_visible`)
- On button press: toggle visibility, call `show_window()`
- Initialize APP_FUN_EN if needed

**Tests (real hardware):**
- Poll button register → reads successfully
- Simulate button press (manual register write if possible) → visibility toggles

---

### Step 12: Transport server

**Deliverable:** Accept frontend connections, dispatch commands, send responses and events.

**Files:**
- `src/transport.cpp` — Transport server

**Tasks:**
- Accept connections on Named Pipe (call Platform::listen())
- Perform security handshake (call Platform::verify_peer())
- Read JSON lines from client
- Parse commands via protocol.cpp
- Dispatch to appropriate handlers (get_metrics, set_profile, etc.)
- Send responses with request ID correlation
- Send unsolicited events (button_press, metrics push, auto_tune_adjust)
- Handle metrics subscription (subscribe_metrics, unsubscribe_metrics)
- Handle disconnection and cleanup

**Tests (real hardware):**
- Connect client, send `ping` → receive `{}` response
- Send `get_metrics` → receive metrics JSON
- Send `subscribe_metrics` → receive periodic metrics events
- Send invalid JSON → receive `parse_error`
- Send unknown command → receive `unknown_command`
- Send `set_profile` with persist=false → receive `persist_disabled`

---

### Step 13: Process manager

**Deliverable:** Spawn frontend process, monitor lifecycle, respawn on crash.

**Files:**
- `src/process.cpp` — Process manager

**Tasks:**
- Implement `spawn_frontend()`:
  - Create Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`
  - Spawn `xmax.exe` as child process
  - Assign both backend and frontend to Job Object
  - Store frontend PID and process handle
- Implement `show_window(hwnd, visible)`:
  - Find frontend HWND via `EnumWindows` (match by PID)
  - Call `ShowWindow(SW_SHOW/SW_HIDE)`
- Monitor frontend process handle (wait in background thread)
- On unexpected exit: respawn hidden after 1s delay
- On backend exit: Job Object closes → OS kills frontend

**Tests (real hardware):**
- Spawn frontend → process running
- Show/hide window → window visibility changes
- Kill frontend → respawned after 1s
- Kill backend → frontend killed (Job Object)

---

### Step 14: Tray icon

**Deliverable:** System tray icon with context menu and tooltip.

**Files:**
- `src/tray.cpp` — Tray icon management

**Tasks:**
- Create tray icon via `Shell_NotifyIcon`
- Implement left-click: toggle frontend visibility
- Implement right-click: context menu (Show Frontend, Restart Frontend, Quit)
- Update tooltip at 1Hz: "45W | 79°C | Gaming" (TDP, CPU temp, active profile)
- Handle tray icon messages in main thread message loop

**Tests (manual):**
- Tray icon appears
- Left-click → frontend shows/hides
- Right-click → menu appears
- Tooltip updates

---

### Step 15: Main entry point and startup sequence

**Deliverable:** main.cpp that orchestrates all threads and startup/shutdown.

**Files:**
- `src/main.cpp` — Entry point

**Tasks:**
- Initialize hardware connections (WMI COM, PawnIO + blobs, ADLX)
- Detect current power state
- If persist=true:
  - Apply charge limit from config
  - Apply power-state profile (if configured)
  - Restore adaptive controller state (if was active)
- If persist=false: do nothing
- Spawn frontend hidden
- Create all background threads (metrics poller, adaptive controller, button monitor, transport server)
- Enter main message loop (for tray icon)
- On shutdown: close hardware connections, exit (Job Object kills frontend)

**Tests (real hardware):**
- Run xmaxsvc.exe → all threads start
- Connect frontend → metrics received
- Persist=true, profile configured → profile applied on startup
- Persist=false → no hardware writes
- Ctrl+C → clean shutdown

---

## Phase 4: Frontend Foundation

### Step 16: WinUI 3 project scaffolding

**Deliverable:** Working WinUI 3 project that compiles and runs an empty window.

**Tasks:**
- Create `frontend/windows/` directory with WinUI 3 project structure:
  ```
  frontend/windows/
    App.xaml
    MainWindow.xaml
    Pages/
    Widgets/
    Services/
    Models/
    ViewModels/
    Assets/
    Tests/
    frontend.csproj
  ```
- Add xUnit test project (`xmax.Tests/`)
- Create empty MainWindow that shows on launch
- Verify: `dotnet build` succeeds
- Verify: `dotnet test` runs one passing test

**Tests:** One trivial test to verify xUnit is working.

---

### Step 17: PipeClient

**Deliverable:** Named pipe client that connects to backend and handles command/response/events.

**Files:**
- `Services/PipeClient.cs`

**Tasks:**
- Connect to `\\.\pipe\xmaxsvc`
- Send JSON commands, await JSON responses (5s timeout)
- Receive unsolicited events
- Auto-reconnect on disconnect (with backoff)
- On reconnect: send `get_metrics` → `subscribe_metrics` → report visibility state
- Raise C# events for UI binding (MetricsReceived, EventReceived, Connected, Disconnected)
- Handle connection failures gracefully

**Tests:**
- Connect to backend → Connected event fires
- Send `ping` → receive `{}` response
- Send `get_metrics` → receive metrics JSON
- Receive event → EventReceived fires with correct payload
- Disconnect → Disconnected event fires
- Reconnect → metrics resumed

---

### Step 18: Models

**Deliverable:** C# classes matching all backend JSON structures.

**Files:**
- `Models/Metrics.cs`
- `Models/Profile.cs`
- `Models/FanCurve.cs`
- `Models/PowerState.cs`
- `Models/AutoTuneConfig.cs`

**Tasks:**
- Define all model classes with JSON deserialization attributes
- Handle nullable fields (e.g., `battery_pct` can be null)
- Implement `ToString()` for debugging

**Tests:**
- Deserialize metrics JSON → Metrics object with correct values
- Deserialize profile JSON → Profile object
- Handle missing optional fields → default values
- Handle null fields → null properties

---

### Step 19: Services layer

**Deliverable:** Services that wrap PipeClient and expose observable state.

**Files:**
- `Services/MetricsService.cs`
- `Services/ProfileService.cs`
- `Services/AutoTuneService.cs`

**Tasks:**
- MetricsService:
  - Subscribe to metrics on connect
  - Expose observable Metrics property
  - Update UI via PropertyChanged events
- ProfileService:
  - Fetch profiles on connect
  - Expose observable Profiles collection
  - Handle profile CRUD commands
- AutoTuneService:
  - Fetch auto_tune state on connect
  - Expose observable Active property
  - Handle set_auto_tune commands

**Tests:**
- MetricsService receives metrics → property updates
- ProfileService receives profiles → collection updates
- AutoTuneService receives state → property updates

---

## Phase 5: Frontend UI

### Step 20: Widget framework

**Deliverable:** Reusable widget system with configurable order and visibility.

**Files:**
- `Services/WidgetService.cs`
- `Widgets/IHomeWidget.cs`

**Tasks:**
- Define `IHomeWidget` interface (WidgetId, Control)
- Create WidgetService that manages widget registration, order, visibility
- Load widget order from config
- Implement reordering (move widget up/down)
- Implement visibility toggling (show/hide widget)
- Save changes to config

**Tests:**
- Register widget → appears in list
- Reorder widgets → order changes
- Hide widget → removed from visible list
- Save/load config → order persists

---

### Step 21: Home page and widgets

**Deliverable:** Home page with configurable widget layout.

**Files:**
- `Pages/HomePage.xaml`
- `Widgets/ProfilesWidget.xaml`
- `Widgets/MetricsWidget.xaml`
- `Widgets/AdaptiveWidget.xaml`
- `Widgets/ChargeLimitWidget.xaml`
- `Widgets/PowerWidget.xaml`

**Tasks:**
- Implement HomePage with configurable column grid (3-5)
- Implement each widget as UserControl:
  - ProfilesWidget: 3 tiles, tap to apply profile
  - MetricsWidget: 3 tiles (CPU, GPU, RAM)
  - AdaptiveWidget: 3-button selector + TDP slider
  - ChargeLimitWidget: cycling button (75/80/85/90/95/100)
  - PowerWidget: full-row with status + TDP ceiling slider
- Bind widgets to services (MetricsService, ProfileService, AutoTuneService)
- Implement widget ordering from config

**Tests:** None (UI rendering).

---

### Step 22: Profiles page

**Deliverable:** Profile CRUD and fan curve editor.

**Files:**
- `Pages/ProfilesPage.xaml`
- `ViewModels/ProfilesViewModel.cs`

**Tasks:**
- Display list of profiles
- Create new profile (name, TDP values, fan curve selection)
- Edit existing profile
- Delete profile (with constraint validation)
- Fan curve editor (drag points on graph)
- Power state assignment UI

**Tests:**
- Create profile → appears in list
- Edit profile → changes saved
- Delete profile referenced by power state → error shown
- Fan curve validation → invalid curves rejected

---

### Step 23: Settings page

**Deliverable:** App settings UI.

**Files:**
- `Pages/SettingsPage.xaml`
- `ViewModels/SettingsViewModel.cs`

**Tasks:**
- Language dropdown (Auto/English/中文)
- Theme dropdown (System/Light/Dark)
- Persist toggle
- Auto-start toggle
- Widget layout config (reorder, show/hide)
- Column count selector (3-5)
- "Revert to system defaults" button

**Tests:**
- Change language → config updated
- Change theme → config updated
- Toggle persist → config updated

---

### Step 24: Navigation and window setup

**Deliverable:** Bottom tab bar navigation and window configuration.

**Files:**
- `MainWindow.xaml`
- `MainWindow.xaml.cs`

**Tasks:**
- Set up NavigationView with bottom tab bar (Home, Profiles, Settings)
- Configure window:
  - Frameless (OverlappedPresenter)
  - Always on top
  - No taskbar entry
  - Rounded corners
  - Mica backdrop
- Position window bottom-right, taskbar-aware (8-12px margin)
- Implement click-outside-to-hide (Window.Activated event)
- Integrate with tray icon (backend controls visibility)

**Tests:** None (UI/window setup).

---

### Step 25: Integration and polish

**Deliverable:** Fully integrated app with all features working.

**Tasks:**
- End-to-end testing:
  - Launch xmaxsvc.exe → frontend spawns hidden
  - Press hardware button → frontend shows
  - Select profile → TDP/fan applied, adaptive deactivated
  - Activate adaptive → profile deactivated, controller running
  - Change power state → profile/adaptive responds
  - Close frontend → backend keeps running
  - Kill backend → frontend killed (Job Object)
- Error handling:
  - Backend disconnect → frontend shows error, auto-reconnects
  - Command timeout → frontend shows error
  - Hardware failure → graceful degradation
- Localization:
  - Verify all strings use locale keys
  - Test English and Chinese
- Performance:
  - Metrics update smoothly at 2s intervals
  - No UI lag during adaptive controller adjustments
  - Memory usage reasonable (<100MB frontend)
- Polish:
  - Tooltips on all controls
  - Loading states during async operations
  - Empty states (no profiles yet)
  - Error messages user-friendly

**Tests (manual):**
- Full user workflow end-to-end
- All error scenarios handled gracefully
- Localization correct
- Performance acceptable
