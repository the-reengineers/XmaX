# Linux Support (Fedora 44 / GNOME)

Brief record of what we discussed and decided for adding Linux support.

## Scope

- Target **Fedora 44 (GNOME)** first. Structure the Linux parts to be **DE-agnostic** so KDE/others can be added later.
- **No full windowed app for now.** Linux UI is **system integration only**, in two places:
  1. A **top-bar status icon** whose click opens a GNOME Shell **popup menu** (the "tray popup" carrying readouts + controls).
  2. **Quick Settings** toggles (the Wi-Fi/Bluetooth panel) with a *subset* of controls, each individually enable-able. Most are off by default to avoid bloat.
  - Both the top-bar icon and per-control Quick Settings visibility are user toggles stored in an `org.xmax.shell` GSettings schema.

## Key findings from the discussion

- **Backend already has a clean platform seam** — `Platform` interface (`backend/src/platform/platform.h`) + `platform_win32.cpp`. `CMakeLists.txt` and `justfile` already have Linux branches; `platform_linux.cpp` is referenced but does not exist yet. Binary name `xmaxd` on non-Windows.
- **Frontend is WinUI 3** (`net10.0-windows10.0.26100.0`) — Windows-only. Its C# *services* are portable, but the XAML/UI is not.
- **GNOME has no classic system tray.** The native equivalent is a **top-bar indicator + `PopupMenu`**, implemented in a **GNOME Shell extension (GJS/JavaScript)**.
- **`gnome-control-center` has no public plugin API** to add a settings page. (Workaround if ever needed: implement the `org.freedesktop.UPower.PowerProfiles` D-Bus interface so stock GNOME controls drive xmaxd.)
- **Distro ≠ desktop environment.** UI code varies by DE (GNOME/KDE/…); distro only affects packaging.

## Decisions

| Axis | Decision |
|---|---|
| Target | Fedora 44 (GNOME), DE-agnostic structure |
| Frontend | One GJS Shell extension: top-bar icon → `PopupMenu` + Quick Settings toggles |
| Frontend settings | `org.xmax.shell` GSettings (icon visibility, per-control QS toggles) |
| Backend language | C++23 (unchanged) — new `platform_linux.cpp` behind `Platform` |
| IPC | D-Bus `org.xmax.Service` + polkit |
| Privilege | systemd system service (root), polkit-gated SMU/EC writes |
| Sequencing | Backend-first, validated headless via `xmaxctl` |

## Architecture

```
GNOME Shell extension (GJS) ──Gio.DBusProxy──► org.xmax.Service (xmaxd, C++)
                                                  ├─ TransportService::dispatch()  ← existing handlers, reused
                                                  ├─ Platform (Linux impl)         ← ryzenadj + sysfs + EC
                                                  └─ polkit agent for SMU/EC writes
```

D-Bus methods delegate to `TransportService::dispatch()` (already public/thread-safe), so CLI, tests, and the extension share one command path.

## Hardware mapping (Windows → Linux)

| Feature | Windows | Linux |
|---|---|---|
| TDP (STAPM/Fast/Slow) | PawnIO SMU mailbox | `ryzenadj` (`set_stapm_limit`/`set_fast_limit`/`set_slow_limit`) |
| EC read/write | WMI `UMAInterface` | `ec_sys` module or Super-I/O port I/O |
| Charge limit (`0x04A3`) | Super I/O | same register via EC/Super-I/O |
| Fan duty/mode, RPM | EC registers | same registers via EC |
| CPU util / clock / temp | WMI | `/proc/stat`, cpufreq sysfs, `k10temp` hwmon |
| RAM | — | `/proc/meminfo` |
| GPU metrics | ADLX SDK | `amdgpu` hwmon + `/sys/class/drm/card*/device/` |
| Battery / power state | WMI | `/sys/class/power_supply/` |

**Parity gaps:** SmartShift/PowerDistribution (ADLX) has no clean Linux equivalent — likely omitted. UMA/VRAM resize is BIOS-level, not runtime-settable — unsupported. CPU package power is available via `ryzenadj` (matches the Windows driver-only gap).

## Backend-first plan

- **Phase 0 — build + skeleton:** create `platform_linux.cpp`, fix UNIX side of `CMakeLists.txt` (test target links `ole32/oleaut32/wbemuuid` unconditionally), verify `xmaxd` builds/runs.
- **Phase 1 — transport:** AF_UNIX socket at `$XDG_RUNTIME_DIR/xmax.sock`, `SO_PEERCRED` peer verification, same JSON line protocol → `transport.cpp` unchanged.
- **Phase 2 — read-only metrics:** driver-free (see mapping above).
- **Phase 3 — control:** SMU via `ryzenadj`, EC/Super-I/O for fan and charge limit.
- **Phase 4 — D-Bus + polkit + systemd:** `org.xmax.Service.xml`, `DbusService` → `dispatch()`, `xmaxd.service`, polkit rules, `xmaxctl`.
- **Phase 5 — GJS extension:** `indicator.js` (top-bar popup), `quickSettings.js`, GSettings schema, D-Bus client.

## Lifecycle seams that change on Linux

- `spawn_frontend` / hardcoded `XmaX.exe` lookup → no spawned FE (extension is the UI).
- `tray_icon` / `TrayManager` → no-op (top-bar icon belongs to the extension).
- `single_instance_lock` → `flock` under `XDG_RUNTIME_DIR`.
- `set_auto_start`/`is_auto_start_enabled` → enable/disable the systemd unit (polkit-authorized).
- `run_message_loop`/`quit_message_loop` → GMainLoop (glib) or `sigwait`.