# Implementation Plan: Linux Support (Fedora 44 / GNOME)

Reference: `LINUX.md` (scope, decisions, architecture, hardware mapping, phases)

Backend-first. Each phase lands in a buildable, runnable state; every step includes verification scripts. Linux UI is a GJS Shell extension (Step 5), so the backend is validated headless via a socket client and `xmaxctl` before any UI exists. All scripts created during implementation live under `scripts/linux/`.

---

## Step 0: Build + skeleton (`platform_linux.cpp`)

Create the Linux `Platform` implementation so `xmaxd` and the test suite build and run on Fedora; fix the UNIX side of `CMakeLists.txt`.

**Files created/modified:**
- Create `backend/src/platform/platform_linux.cpp` — implements every pure-virtual in `platform.h`. Real final implementations for the infrastructure seams; hardware methods (`ec_*`, `smu_*`, `gpu_metrics`, `system_metrics`, `cpu_clock_mhz`, `charge_limit_write`) return `std::unexpected(ErrorCode::SensorUnavailable)` for now (filled in Steps 2–3).
  - `data_dir()` → `$XDG_CONFIG_HOME/xmax` (fallback `~/.config/xmax`); `self_exe_path()` → `readlink("/proc/self/exe")`.
  - `single_instance_lock()` / `release_instance_lock()` → `flock(LOCK_EX|LOCK_NB)` on `$XDG_RUNTIME_DIR/xmax.lock` (fallback `/tmp/xmax-<uid>.lock`).
  - `tray_icon` / `update_tray_tooltip` / `remove_tray_icon` → no-op success (top-bar icon belongs to the GJS extension, per LINUX.md; handle `nullptr` safely).
  - `spawn_frontend` → `std::unexpected(ErrorCode::SensorUnavailable)`; `show_window`/`wait_for_process`/`terminate_process` → inert no-ops/errors (no windowed FE on Linux).
  - `set_auto_start` / `is_auto_start_enabled` → `std::unexpected(ErrorCode::SensorUnavailable)` for now (finalized in Step 4 — see note).
  - `run_message_loop` → `sigwait`/`signalfd` on SIGINT+SIGTERM; `quit_message_loop` → `raise(SIGTERM)`.
  - `init_hardware()` → `true` (no mandatory init yet).
- Modify `backend/CMakeLists.txt` — replace the UNIX branch's missing-source reference with `src/platform/platform_linux.cpp`, and guard the test target's `ole32 oleaut32 wbemuuid` link (around lines 220–226) behind `if(WIN32)`.
- Modify `backend/src/main.cpp` — guard the `XmaX.exe` frontend-spawn lookup (line ~317) so it's skipped on non-Windows (no FE to launch).

**Verification:**
- `scripts/linux/verify-build.sh` — configure, compile `xmaxd` + test binary, and run `ctest`. Fails if the test target still tries to link Windows libs or if `platform_linux.cpp` leaves a virtual unimplemented.

> **Note (temporary seam):** `set_auto_start`/`is_auto_start_enabled` return `SensorUnavailable` until Step 4, where they are finalized to enable/disable the systemd unit. This is a 2-phase gap, not throwaway code.

**Done.** Created `backend/src/platform/platform_linux.cpp` (all pure-virtuals implemented: infra seams real, hardware/transport return `SensorUnavailable` until Steps 1–3), guarded the `XmaX.exe` spawn in `main.cpp` behind `#ifdef _WIN32`, and guarded the test target's `ole32/oleaut32/wbemuuid` link behind `if(WIN32)`. Added `scripts/linux/verify-build.sh` (fresh `backend/build-linux`, reuses cached FetchContent sources) and added `backend/build-linux/` to `.gitignore`. Verified: `xmaxd` + tests build on Fedora with GCC 16; **156/156 tests pass**; `xmaxd` starts, runs, and shuts down cleanly on SIGTERM (exit delay of a few seconds is the existing background-thread sleep cadence, not a defect).

**Implementation notes for later steps:** message loop uses a condition-variable wait driven by `main.cpp`'s existing signal handler (simpler than `sigwait` and doesn't fight the already-installed `std::signal` handlers). `data_dir()` uses `$XDG_CONFIG_HOME` (config+profiles are configuration — note `platform.h`'s comment still says `XDG_DATA_HOME`; worth a doc touch-up later).

---

## Step 1: Transport (AF_UNIX socket)

Implement the Linux transport seam so `xmaxd` serves the existing JSON line protocol over a Unix-domain socket in `$XDG_RUNTIME_DIR/xmax.sock`, reusing `TransportService` unchanged.

**Files created/modified:**
- Create `backend/src/platform/transport_unix.cpp` (compiled into `platform_linux.cpp` or linked separately) implementing `Platform`'s transport methods: `listen()` binds `$XDG_RUNTIME_DIR/xmax.sock` (mode 0600), `accept_connection()`, `read_data`/`write_data` (buffered, newline-delimited), `verify_peer()` via `SO_PEERCRED` (`getsockopt(SO_PEERCRED)`) → map to `PeerInfo` (uid/pid/gid), `close_connection`/`close_server`.
- Modify nothing in `transport.cpp`/`protocol.h` — they are platform-agnostic.

**Verification:**
- `scripts/linux/verify-transport.sh` — start `xmaxd`; send `{"type":"command","method":"ping","id":"1"}` over the socket with a small Python client (`socketserver`/`socket`), assert a matching `{"type":"response","id":"1","ok":true}` line; assert `SO_PEERCRED` reports the client's real uid/pid; assert a second client connection is accepted concurrently; assert unprivileged `ping` works and (sanity) a malformed frame returns `parse_error`.
- Add gtest cases (reusing the existing `MockPlatform`) that already cover `TransportService::dispatch` — confirm they pass under `ctest` on Linux (they must, since transport logic is platform-free).

**Done.** Implemented the full AF_UNIX transport in `platform_linux.cpp` (no separate `transport_unix.cpp` — it lives beside the other `Platform` methods and matches the Win32 file's layout): `listen()` binds `$XDG_RUNTIME_DIR/xmax.sock` (mode 0600, unlinks stale socket first; single-instance flock guards against clobbering a live instance), `accept_connection()` uses a 1s `poll()` timeout then `accept()` and reads the peer via `SO_PEERCRED`, `verify_peer()` resolves `/proc/<pid>/exe` and verifies the peer uid == `getuid()`, and `pipe_read`/`pipe_write`/`pipe_disconnect`/`close_server` operate on the accepted client fd (writes use `send(MSG_NOSIGNAL)` so a closed peer can't `SIGPIPE` the daemon). `read_data`/`write_data`/`close_connection` remain unused stubs (exactly matching Win32). The `TransportServer.handle` points to a heap `LinuxTransportServer{atomic listen_fd, client_fd}`; instances are owned by a vector for process lifetime (never freed mid-run), so `close_server` on the stop thread can never race a `delete`, and each fd is closed exactly once via `std::atomic<int>::exchange(-1)`. Verified: gtest **156/156 pass**; one-off smoke check (see scoping note) confirmed ping `ok:true`, reconnect after disconnect, malformed frame → `parse_error`, socket `0600`, and `verify_peer` reporting the real pid/uid. `transport.cpp`/`protocol.h` untouched.

> **Scoping note (verification scripts):** per the "don't commit implementation-only checks" rule, the transport smoke check was run from `/tmp/xmax-transport-check.py` (clearly named, **not** tracked — no `scripts/linux/verify-transport.sh` was created). The only durable script kept in the repo is `scripts/linux/verify-build.sh` (build + `ctest`, useful for devs). Future step-level HV-probe checks will follow the same `/tmp` pattern, and only genuinely reusable dev/maintenance scripts will land under `scripts/linux/`.

> **Note for Step 4:** `verify_peer` currently trusts same-uid peers (`peer uid == getuid()`). Once `xmaxd` runs as a root systemd service (Step 4), the uid check must relax to "local user (uid ≥ 1000 or 0)" with polkit gating privileged methods — an intended authz seam, not throwaway code.

---

## Step 2: Read-only metrics (sysfs + `/proc`)

Populate the driver-free metrics. This touches **both** `platform_linux.cpp` **and** the `#ifdef _WIN32` blocks in `metrics.cpp` (CPU util, RAM, battery % are currently Windows-only inline code).

**Files created/modified:**
- Create `backend/src/platform/linux/procfs.h/.cpp` — pure, unit-testable parsers: `/proc/stat` (CPU util via prev-idle/prev-total deltas), `/proc/meminfo` (MemTotal/MemAvailable/MemFree → total/avail/used/load), `/sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq` and `cpuinfo_cur_freq` (CPU clock MHz), `k10temp` hwmon (`Tctl`/`temp1_input`), `/sys/class/power_supply/BAT*/capacity` + `AC*/online` (battery %, AC presence), `amdgpu` hwmon (`hwmon*/power1_average`, `temp1_input`, `freq1_input`, `mem_info_vram_used`/`vram_total`) and `/sys/class/drm/card*/device/gpu_busy_percent`.
- Modify `backend/src/platform/platform_linux.cpp` — implement `cpu_clock_mhz()`, `gpu_metrics()`, `system_metrics()` (return zeros/`nullopt` for SmartShift/PowerDistribution — no Linux equivalent), using the parsers above.
- Modify `backend/src/metrics.cpp`:
  - Hoist the platform-generic calls in `poll_cpu()` (`platform_.cpu_clock_mhz()`, `platform_.ec_read(0x0470)`, `platform_.system_metrics()`) **out** of the `#ifdef _WIN32` block so they run on both platforms.
  - Add `#else` (Linux) branch in `poll_cpu()` for CPU util via `procfs::cpu_util()`.
  - Add `#else` branch in `poll_ram()` via `procfs::ram()`.
  - Add `#else` branch in `poll_power()` for `power.battery_pct` via `procfs::battery_pct()`.

**Verification:**
- gtest (new `backend/tests/test_linux_procfs.cpp`): parse **fixture files** (committed sample `/proc/stat`, `/proc/meminfo`, hwmon files) and assert exact values; assert delta logic returns 0 util on first call and sane values after; assert missing-file → `SensorUnavailable`. Fixtures keep the tests hermetic (no real hardware needed in CI).
- `scripts/linux/check-hw-metrics.sh` — probe the real host: print `/proc/stat` CPU util (two samples), `/proc/meminfo`, present hwmon chips (`sensors`/`ls /sys/class/hwmon`), `k10temp`, `amdgpu`, and `find /sys/class/power_supply -name capacity`. Fail (or warn) if `amdgpu`/`k10temp` are absent so control-side deps can be gated properly.
- `scripts/linux/verify-metrics.sh` — with `xmaxd` running, call `get_metrics` via the socket and assert non-zero `cpu.clock_mhz`, `ram.total_bytes`, a numeric `cpu.util_pct` after a delay, and `power.battery_pct` present when on battery.

**Done.** Created `backend/src/platform/linux/procfs.h/.cpp`: `/proc/stat` CPU util (a stateful `CpuUtil` sampler holding prev-idle/prev-total, first sample 0.0), `/proc/meminfo` (`MemInfo` with total/avail/free/used/load), cpufreq `cpu_clock_mhz` (kHz→MHz), `read_long`/`read_temp_c` hwmon helpers, `cpu_temp_c` (k10temp scan), `battery_pct`+`ac_online` (`BAT*/capacity`, `AC*/online`), and `gpu_info` (amdgpu hwmon + `drm card*/device/gpu_busy_percent`; `found=false` when no amdgpu chip, so an absent GPU maps to `SensorUnavailable`). Implemented `cpu_clock_mhz()`/`gpu_metrics()`/`system_metrics()` in `platform_linux.cpp` (`system_metrics` returns an empty `SystemTelemetry` — no SmartShift/PowerDistribution equivalent). Reworked `metrics.cpp`: hoisted `cpu_clock_mhz`/`ec_read(0x0470)`/`system_metrics` out of the `#ifdef _WIN32` block and added `#else` branches for CPU util (`procfs::CpuUtil`), RAM (`procfs::read_meminfo`), and battery % (`procfs::battery_pct`). `CMakeLists.txt` adds `procfs.cpp` to both targets plus `test_linux_procfs.cpp` and the `XMAX_FIXTURES_DIR` define. Verified: gtest **173/173 pass** (17 new fixture-driven `Procfs*` tests under `backend/tests/fixtures/`); a `/tmp` e2e check shows live values (`cpu.clock_mhz`≈3084, `ram.total_bytes`≈33 GB, `cpu.util_pct`≈2.8% after a delay, `gpu.temp_c`=45, `battery_pct`=91).

> **Scoping note (verification scripts):** per the "don't commit implementation-only checks" rule, `verify-metrics.sh` was run as `/tmp/xmax-metrics-check.py` (clearly named, **not** tracked). `scripts/linux/check-hw-metrics.sh` **is** kept — it's a durable, read-only environment probe for devs. It reports this host correctly: `k10temp`+`amdgpu` present; `[warn]` only for `cpuinfo_cur_freq` (absent — `scaling_cur_freq` fallback already handles it) and `mem_info_vram_*` (absent — GPU VRAM shows `nullopt`, which is correct and not an error).

> **Note for Step 3:** on Linux, CPU temperature still comes from `ec_read(0x0470)` (now hoisted but returning `SensorUnavailable` until the EC backend lands), so `cpu.temp_c` stays `nullopt` until Step 3. The `k10temp` `cpu_temp_c()` parser is complete and tested but not yet consumed — left as a library function rather than a second CPU-temp source, to avoid diverging from the Windows "EC 0x0470" path.

---

## Step 3: Control (ryzenadj SMU + EC / Super-I/O)

Wire up privileged writes for TDP, fan, and charge limit.

**Files created/modified:**
- Create `backend/src/platform/linux/ryzenadj.cpp/.h` — load `libryzenadj.so` via `dlopen` (no hard link dependency), expose `set_stapm_limit`/`set_fast_limit`/`set_slow_limit` and the matching reads (mW → W conversion to match the existing `TdpController`/SMU units in `shared.h`).
- Create `backend/src/platform/linux/ec.cpp/.h` — EC access backend:
  - **Primary:** `ec_sys` kernel module — read/write `/sys/kernel/debug/ec/ec0/io` via `lseek`+`pread`/`pwrite` (must be run as root; debugfs mount not required when the module exposes the char path).
  - **Fallback:** Super-I/O port I/O via `iopl`/`outb`/`inb` on the EC data/command ports — clearly separated and selected at runtime by capability probe.
  - Implements `ec_read(reg)`/`ec_write(reg, val)` with the register map already used by `fan.h`/`power.h`/`metrics.cpp` (0x044A/0x044B fan mode+duty, 0x0476/0x0477 RPM, 0x0470 CPU temp, 0x04A3 charge limit, 0x04FE power state, 0x0230/0x0231 button).
- Modify `backend/src/platform/platform_linux.cpp` — implement `smu_send`/`smu_read_safe` (route to `ryzenadj`, encode SMU opcodes 0x14/0x15/0x16 as STAPM/Fast/Slow), `ec_read`/`ec_write`, `charge_limit_write` (write 0x04A3). Preserve `SensorUnavailable`/`ChargeLimitWriteFail` on failures.

**Verification:**
- `scripts/linux/check-ec.sh` — probe `ec_sys` (`modprobe ec_sys` or `/sys/kernel/debug/ec/ec0/io` presence), read charge-limit (0x04A3) and power-state (0x04FE) byte-by-byte, read fan duty (0x044B) and RPM (0x0476/0x0477), then write fan duty to a value and **restore the original** (safe non-destructive check).
- `scripts/linux/check-ryzenadj.sh` — detect `libryzenadj.so`/`ryzenadj` CLI, read current STAPM/Fast/Slow (mW), write STAPM +/-1W and **restore** the original.
- `scripts/linux/verify-control.sh` — via `xmaxctl` (once Step 4 lands) or the socket client: `set_fan {mode:curve}`, `set_tdp {stapm:45,fast:50,slow:45}`, `set_charge_limit {percent:85}` then read back with `get_metrics`/`get_charge_limit` and assert the writes round-trip; restore prior values at the end.

**Done.** Created `backend/src/platform/linux/ryzenadj.h/.cpp` — a `dlopen` binding to `libryzenadj.so` (candidates `libryzenadj.so{,.6,.5,.4,.1}` plus a `XMAX_RYZENADJ_LIB` override); `init_ryzenadj()` is probed once (dlclose + unusable on any missing symbol or init failure), and `set/get_stapm|fast|slow_limit` operate in mW, returning `false`/`nullopt` when absent. Created `backend/src/platform/linux/ec.h/.cpp` — `ec_sys` debugfs (`/sys/kernel/debug/ec/ec0/io`, `pread`/`pwrite` at the register offset) primary + Super-I/O (`iopl`/`inb`/`outb` on 0x66/0x62, compiled out on non-x86) fallback, selected once by capability probe; `read`/`write` degrade to `nullopt`/`false`. Wired `platform_linux.cpp`: `ec_read`/`ec_write`→`xmax_ec`, `smu_send`/`smu_read_safe`→`ryzenadj` (opcodes 0x14 STAPM / 0x15 Fast / 0x16 Slow, arg is mW), `charge_limit_write`→`ec_write(0x04A3)`, and `init_hardware()` now probes both backends and logs per-backend `[hw]` warnings while still returning `true` (non-fatal). `CMakeLists.txt` adds `ec.cpp`+`ryzenadj.cpp` to the UNIX target and `${CMAKE_DL_LIBS}`. Verified: build clean, gtest **173/173 pass**; `/tmp/xmax-verify-control.py` (not committed) runs `xmaxd` over the socket and confirms — `get_charge_limit`→`sensor_unavailable` (EC read), `set_charge_limit`→`persist_disabled` pre-persist then `charge_limit_write_fail` after `set_session_persist`, `get_fan`/`get_power_mode`/`get_button` synthesize graceful defaults (no crash), and both `[hw]` warnings are emitted.

> **Scoping note (verification scripts):** `scripts/linux/check-ec.sh` and `scripts/linux/check-ryzenadj.sh` are **kept** (durable env probes for devs; `check-ec.sh` is read-only, `check-ryzenadj.sh` does a read-only ctypes read of current limits). The write-and-restore round-trip check in `verify-control.sh` was run as `/tmp/xmax-verify-control.py` (not committed) and, on this non-root box, confirms graceful degradation only — no root EC access and no `libryzenadj`, so the real hardware write/read round-trip is deferred to running `check-ec.sh`/`check-ryzenadj.sh` as root (and `xmaxctl` in Step 4) once the deps are present.

> **Note for Step 4:** control is currently exercised only through the unprivileged same-uid socket, so on a root `xmaxd` the super→uid check (see Step 1 note) plus polkit gating of `set_fan`/`set_tdp`/`set_charge_limit` are what actually makes these writes safe to expose. The 16-bit vendor registers (charge limit 0x04A3, etc.) need the `ec_sys` path — the Super-I/O fallback only sees the low byte of the ACPI EC's 256-byte space, so a fallback-only host can't reach them (documented in `check-ec.sh`).

---

## Step 4: D-Bus + polkit + systemd + `xmaxctl`

Expose `org.xmax.Service` on the system bus, gate privileged writes with polkit, run `xmaxd` as a systemd service, and add a CLI.

**Files created/modified:**
- Create `backend/src/dbus/org.xmax.Service.xml` — D-Bus interface: methods `Ping`, `GetMetrics`, `SetTdp(a{sv})`, `SetFan(a{sv})`, `SetChargeLimit(u)`, `GetChargeLimit`, `SetAutoStart(b)`; properties `Version`, `AutoStartEnabled`; signal `MetricsChanged(a{sv})`.
- Create `backend/src/dbus/dbus_service.cpp/.h` — a `DbusService` that holds a `TransportService&` and maps each D-Bus method to `transport.dispatch(Command)`, translating `Response` back to D-Bus replies/errors. `sd-bus` (systemd) is the recommended library if available; otherwise `Gio::DBus` (glibmm). Single choice, confirmed during implementation.
- Create `packaging/linux/org.xmax.policy` (polkit: `org.xmax.set` actions for TDP/fan/charge-limit) and `packaging/linux/xmaxd.service` (Type=dbus, BusName=org.xmax.Service, ExecStart=/usr/local/bin/xmaxd, User=root, requires `ec_sys`/`ryzenadj` present).
- Create `backend/src/xmaxctl.cpp` — CLI using the same `TransportService` (via D-Bus, falling back to the socket): `xmaxctl metrics|tdp|fan|charge-limit|auto-start|version`.
- Modify `backend/src/platform/platform_linux.cpp` — finalize `set_auto_start`/`is_auto_start_enabled` (enable/disable the systemd unit via a `systemctl enable/disable` helper; polkit-authorized path).
- Modify `backend/CMakeLists.txt` — new `xmaxctl` target; link D-Bus lib (`libsystemd` or `gio-2.0`/`glibmm`); optional `dbus-glib` for introspection.

**Verification:**
- `scripts/linux/verify-dbus.sh` — install the `.service` + `.policy`; `busctl --system introspect org.xmax.Service /org/xmax/Service` (or `gdbus introspect`) lists methods; `busctl call ... Ping` returns; `GetMetrics` returns a struct with populated fields; calling `SetTdp` as non-root triggers the polkit agent and is denied without auth, and succeeds once authorized (script asserts the authorization path).
- `scripts/linux/verify-systemd.sh` — `systemctl start xmaxd`, assert `systemctl is-active xmaxd` = active, check `journalctl -u xmaxd` for clean startup, then `systemctl stop xmaxd`.
- `scripts/linux/verify-xmaxctl.sh` — run `xmaxctl version`, `xmaxctl metrics`, `xmaxctl tdp 45 50 45` then `xmaxctl metrics` to confirm round-trip, and `xmaxctl auto-start on|off`.

**Done.** Created `backend/src/dbus/org.xmax.Service.xml`, `dbus_service.h/.cpp` (sd-bus; two bus connections — `bus_` for the service + `auth_bus_` for polkit `CheckAuthorization` via raw sd-bus, since polkit-gobject headers are absent), `packaging/linux/org.xmax.policy` (+ `set-auto-start` action), `org.xmax.Service.conf` (system-bus policy required because dbus system.conf has `<deny own="*"/>`), and `xmaxd.service` (Type=dbus, BusName=org.xmax.Service, `ExecStart=/usr/local/bin/xmaxd`). Added `backend/src/xmaxctl.cpp` (D-Bus primary + `$XDG_RUNTIME_DIR/xmax.sock` fallback), finalized `set_auto_start`/`is_auto_start_enabled` (systemctl enable/disable + is-enabled), and updated `CMakeLists.txt` (xmaxctl target, `PkgConfig::SYSTEMD`, `XMAX_VERSION` define). **Deviation: `GetMetrics` returns a JSON string (`s`), not a struct** — matches the socket protocol's JSON responses; Step-5 GJS clients must read a string. Wired a NEW `set_tdp` transport command (`transport.cpp` maps to `TdpController::write_tdp`, SMU opcodes 0x14/0x15/0x16). Env seams `XMAX_DBUS_BUS=user` (session bus) and `XMAX_NO_POLKIT=1`. Verified: clean build; **176/176 tests pass** (3 new `SetTdp*`); session-bus smoke test (unprivileged) confirmed introspection lists every method/property/signal, `Ping` ok, `GetMetrics` returns live telemetry, and `SetTdp`/`SetFan` parse `a{sv}` (uint + string variants) correctly — including a fixed `read_asv` bug where it read `peek_type`'s `contents` out-param instead of `type`. The three `verify-*.sh` scripts are written + executable but **the system-bus/systemd ones were not run here (need root)** — hand to the user, e.g. `sudo scripts/linux/verify-dbus.sh`. Caveats: `xmaxctl` opens the *system* bus (no env override); `set_auto_start` reports `sensor_unavailable` on a systemctl failure (no dedicated error code exists).

---

## Step 5: GJS GNOME Shell extension (top-bar popup + Quick Settings)

Build the system-integration UI: a top-bar indicator opening a `PopupMenu`, plus per-control Quick Settings toggles, both driven by `org.xmax.shell` GSettings and `org.xmax.Service` D-Bus.

**Files created/modified:**
- Create `frontend/gnome/xmax@local/metadata.json` — extension metadata (shell versions for GNOME 48/49, `uuid: xmax@local`).
- Create `frontend/gnome/xmax@local/schemas/org.xmax.shell.gschema.xml` — keys: `show-top-bar-icon` (default true), `top-bar-icon-shows-temp`, and one `qs-<control>` boolean per Quick Settings control (fan mode, TDP slider, charge limit, power profile) **all defaulting off**.
- Create `frontend/gnome/xmax@local/extension.js` (entry), `indicator.js` (top-bar `PanelMenu.Button` + `PopupMenu` with readouts and controls confirming to the current adapter/layout), `quickSettings.js` (`QuickSettingsToggle`/`QuickSlider` classes, registered off by default), `dbus.js` (`Gio.DBusProxy` client for `org.xmax.Service`, request/response correlation, signal subscription), `prefs.js` (optional prefs window to toggle visibility).
- Guard against GNOME API drift: a thin `shellCompat.js` adapter so future DE/version differences are localized.

**Verification:**
- `scripts/linux/verify-extension-schema.sh` — `glib-compile-schemas` (asserts the gschema XML is valid), `gsettings get org.xmax.shell show-top-bar-icon`, `gsettings list-keys org.xmax.shell`, and `gsettings set org.xmax.shell qs-fan-mode true` round-trips.
- `scripts/linux/verify-extension-install.sh` — `gnome-extensions install` + `gnome-extensions enable xmax@local`, assert `gnome-extensions info` shows enabled; validate `metadata.json` parses (JSON + `od` sanity).
- `scripts/linux/verify-extension-ui.sh` — nested `gnome-shell --mode=initial-setup`/Xvfb session or `dbus-run-session`: assert the indicator registers, opening the panel lists the popup, and toggling a QS control sends the expected D-Bus method (using a mock `org.xmax.Service` on a private bus). Where headless UI isn't available, fall back to a `gjs -m` unit test of `dbus.js` against a stub bus + a documented manual screenshot checklist.

**Done.** Created the full GNOME 50 (ESM) extension under `frontend/gnome/xmax@local/`: `extension.js` (entry — `Extension` subclass wiring client + indicator + QS), `indicator.js` (top-bar `PanelMenu.Button` + `PopupMenu` with CPU/GPU/RAM/Fan/Power/Charge readouts; optional `top-bar-icon-shows-temp` swaps the glyph for a °C label), `quickSettings.js` (one `SystemIndicator` holding four opt-in tiles — fan `QuickToggle`, TDP `QuickSlider` 15–60 W, charge-limit `QuickSlider` 75–100%, power-profile `QuickMenuToggle`), `shellCompat.js` (re-exports `Main`/`PanelMenu`/`PopupMenu`/`QuickSettings`/`Config` + `shellAtLeast(major,minor)`), `prefs.js` (`ExtensionPreferences`/Adw window), plus the schema and metadata (already present). **Key production fix:** the daemon runs as a systemd unit on the *system* bus, so `dbus.js` now resolves its connection from `XMAX_DBUS_BUS` (default system bus, `=user` → session bus, matching the daemon's dev seam) and routes all calls through `this._conn`. **GJS pitfalls fixed in `dbus.js`:** `deep_unpack()`/`unpack()` collapse `a{sv}` variant values to `{}`, so the `MetricsChanged` `metrics` string and `UPower.PowerProfiles` `aa{sv}` `Profiles` are now decoded with `lookup_value()`/`get_variant()` (drives a fixed 9/9 gjs test — the signal now delivers). **Backend-matching semantics:** fan toggle maps `auto`↔`curve` (`set_fan` rejects `manual`), TDP slider writes one wattage across all three SMU limits (6–120 W valid), and the charge-limit slider/schema range is clamped to **75–100** (the backend rejects `<75`; the prior `0–100`/“0 = disabled” idea had no backend equivalent). Verified here: `verify-extension-schema.sh` (strict compile, 8 keys listed, `show-top-bar-icon` default true, `qs-fan-mode` round-trip, charge range `75 100`) and `verify-extension-ui.sh` (gjs `dbus.js` test **9/9** against a live `xmaxd` on a private session bus); all shell `.js` files pass `node --check`. **Not runnable here (needs a live session / not this host):** `verify-extension-install.sh` (`gnome-extensions install/enable`, session-bus D-Bus) and the nested-shell UI path (Xvfb absent) — hand to the user, e.g. `scripts/linux/verify-extension-install.sh` inside GNOME.

> **Note for Step 6:** the gjs client reads `GetMetrics` as a JSON string (`s`, per the Step 4 deviation) and the QS power-profile control write `ActiveProfile` via `Properties.Set` on the system bus — both already exercised in the unit test.

---

## Step 6: Verify / Test (end-to-end)

Full-system smoke test across the whole Linux stack.

**Files created/modified:**
- Create `scripts/linux/verify-e2e.sh` — orchestrates: build → `ctest` → `xmaxd`+systemd → `xmaxctl` metrics/control round-trips → D-Bus introspection → extension schema/install → polkit authorization path. One command gives a green/red summary per phase.
- Optional: `scripts/linux/verify-parity.sh` — diffs `get_metrics` field coverage and `xmaxctl` command parity against the Windows surface documented in `backend/src/protocol.h`, flagging any Linux-only gaps (SmartShift, UMA) as explicitly "unsupported, not missing".

**Verification:**
- Run `scripts/linux/verify-e2e.sh` from a clean checkout on Fedora 44; all steps green.
- Manual: confirm the top-bar icon appears, its popup shows live temps/TDP, Quick Settings toggles appear only when enabled in `org.xmax.shell`, and disabling the top-bar icon hides it.

**Done.** Added `scripts/linux/verify-e2e.sh` — a dependency-ordered orchestrator over the per-step verify scripts (build+ctest → extension schema → gjs D-Bus client → D-Bus install+introspect+polkit → xmaxctl round-trips → systemd start/stop → extension install) that records pass/fail/skip per phase, skips the root phases with a `sudo` rerun hint when unprivileged, and exits 0 iff no phase failed. Added `scripts/linux/verify-parity.sh` — derives the transport/D-Bus/xmaxctl command surfaces from source, prints the `Metrics` field-coverage table (yes / control / gap), and flags SmartShift/PowerDistribution/UMA/reboot explicitly as "unsupported, not missing". Verified unprivileged: build + **176/176 ctest**, extension schema, and gjs D-Bus client (**9/9**) pass; the root-gated phases correctly SKIP. **Surfaced + fixed a latent bug in `verify-extension-install.sh` (Step 5):** `gnome-extensions install` wants a bundle, and this host's `pack` tool does not auto-follow the ESM import graph, so it now `pack`s with `--extra-source` for the four modules (shellCompat/dbus/indicator/quickSettings) before installing — a complete bundle is required for the extension to load. **Caveat:** a freshly-installed local extension is not adopted by a running gnome-shell until it restarts (no reload method on `org.gnome.Shell.Extensions`), so `enable`/`info` warn ("log out/in or Alt+F2 → r") rather than hard-fail. The full green run is `sudo scripts/linux/verify-e2e.sh` plus a shell restart.

---

## Notes

- **Hardware deps:** `ryzenadj` and `ec_sys` (Super-I/O fallback) are runtime prerequisites for Step 3+, not build prerequisites. Steps 0–2 build and run without them; control paths degrade to `SensorUnavailable`. RyzenAdj is vendored as Linux-only source at `backend/lib/ryzenadj/` (pinned `v0.19.0`, LGPL-3.0) and built/installed with `scripts/linux/build-ryzenadj.sh` — xmaxd `dlopen`s the resulting `libryzenadj.so` at runtime (no link dependency).
- **Parity gaps are intentional** (SmartShift/PowerDistribution, UMA resize) per LINUX.md — they map to "unsupported," surfaced through the existing error codes rather than fake values.
- **MockPlatform reuse:** the existing `MockPlatform` in `backend/tests/test_main.cpp` already exercises `TdpController`/`FanController`/`PowerController`/`MetricsPoller`/`TransportService`; new Linux parsers get their own hermetic gtest file so Linux logic is covered without hardware.