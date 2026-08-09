# Frontend Design — XmaX

See [PROJECT.md](PROJECT.md) for the architecture and IPC protocol. This document covers the frontend window, layout, widgets, navigation, and theming.

## Window

### Position

Bottom-right of the screen, taskbar-aware. The frontend queries the OS work area (`SystemParameters.WorkArea` on Windows) and positions the window with an 8–12px margin from the bottom-right corner of the work area — not the screen edge. This naturally avoids overlapping the taskbar.

```
Screen
┌──────────────────────────────┐
│                              │
│                              │
│                     ┌───────┐│
│                     │ XmaX  ││ ← 8–12px from work area edge
│                     │       ││
│                     └───────┘│
├──────────────────────────────┤
│         Taskbar              │
└──────────────────────────────┘
```

Windows 11 locks the taskbar to the bottom. Windows 10 allows any edge — `WorkArea` handles both. Positioning logic is a FE concern (the BE has no concept of screen geometry or pixels).

**Future:** position may become user-configurable. The preference would be stored via `set_config` like other settings, but the calculation remains FE-side.

### Frame

- **No title bar, no close button, no resize handle** — `OverlappedPresenter` with `IsAlwaysOnTop = true`, `IsResizable = false`, `IsModal = false`
- **Rounded corners** — native on Windows 11 via WinUI 3
- **Click outside to hide** — monitor `Window.Activated` event. When the window loses focus (user clicks elsewhere), hide it. Same behavior as Windows 11 Quick Settings and Start menu
- **No taskbar entry** — the window does not appear in the taskbar. The tray icon is the persistent presence

### Size

~380px wide × ~640px tall. The footprint of Windows 11 Quick Settings — tall enough for the home page without scrolling, narrow enough to feel like a popup.

## Colors & Theme

Default to system settings — zero configuration:

| Setting | Default | Override |
|---------|---------|----------|
| Light/dark mode | Follow Windows | Settings page: `light` / `dark` / `system` |
| Accent color | Follow Windows accent color | Future: color picker in Settings |
| Background | Mica (`MicaBackdrop`) — translucent, adapts to light/dark and desktop wallpaper | None needed |

Stored in `config.json` `theme`: `"system"`, `"light"`, or `"dark"`.

## Navigation

**Bottom tab bar** with icons and short labels. ~40px tall. Three tabs now, room for up to 5 total (two reserved for future pages):

```
├──────────────────────────────────────┤
│  🏠 Home   📊 Profiles   ⚙ Settings │
└──────────────────────────────────────┘
```

At 380px width, 5 tabs fit at ~76px each. WinUI 3 `NavigationView` in `PaneDisplayMode="Top"` or a custom `SelectorBar` for tighter styling control.

### Pages

| Page | Tab | Purpose |
|------|-----|---------|
| **Home** | 🏠 | Quick settings — configurable widgets |
| **Profiles** | 📊 | Profile CRUD, fan curve editor, power-state assignment |
| **Settings** | ⚙ | Language, theme, persist, auto-start, widget layout config, safe defaults reset |
| *(reserved)* | *(+2 slots)* | Future pages |

The home page is the default — shown every time the window opens. It is the only page most users will ever see.

## Home Page

A Quick Settings-style layout (similar to Android's swipe-down panel). The home page surfaces everything that matters at a glance with inline controls. Detailed editing (fan curve editor, profile CRUD) lives on the Profiles page.

**No header** — no app title, no hamburger menu. The bottom tab bar is the only navigation chrome.

**No section headings** — widgets are self-explanatory from their content (profile tiles show profile names, the adaptive selector shows preset names, etc.)

### Grid system

The home page uses a configurable column grid: **3–5 columns** (user setting, default 3).

| Widget type | Column span |
|-------------|-------------|
| Tile (profile, metric, charge limit) | 1 column |
| Full-row (adaptive, power) | All columns |

At 3 columns, tile rows show 3 items per row. At 4–5 columns, tiles maintain their minimum width and additional columns provide breathing room or accommodate more tiles when future widgets are added.

```json
{
  "home_columns": 3
}
```

### Layout example (3 columns)

```
┌──────────────────────────────────────┐
│                                      │
│  ┌────────┐ ┌────────┐ ┌────────┐    │  profiles row
│  │ Gaming │ │ Silent │ │ Max    │    │
│  │  45W   │ │  25W   │ │  55W   │    │
│  └────────┘ └────────┘ └────────┘    │
│                                      │
│  ┌──────┐  ┌──────┐  ┌──────┐        │  metrics row
│  │ 79°C │  │ 73°C │  │ 23%  │        │
│  │ 45W  │  │ 47W  │  │25.8G │        │
│  └──────┘  └──────┘  └──────┘        │
│                                      │
│  ┌──────────────────────────────────┐│  adaptive (full-row)
│  │ [ Silent ] [ Default ] [ Perf ]  ││
│  │  TDP ●──────────────── 45W       ││
│  └──────────────────────────────────┘│
│                                      │
│  ┌────────────────┐                  │  charge limit
│  │   Charge: 85%  │                  │
│  └────────────────┘                  │
│                                      │
│  ┌──────────────────────────────────┐│  power (full-row)
│  │ ⚡DC-In (dedicated)  Battery 91%││
│  │ TDP Ceiling ●─────────────── 55W ││
│  └──────────────────────────────────┘│
│                                      │
├──────────────────────────────────────┤
│  🏠 Home   📊 Profiles  ⚙ Settings │
└──────────────────────────────────────┘
```

### Widgets

Each widget is a self-contained `UserControl` with its own `ViewModel`, subscribing to `MetricsService` or `PipeClient` events independently. The home page renders widgets in config-defined order, skipping hidden ones.

#### Profiles widget

**Type:** Tile row (3 tiles, each 1 column)

Shows saved profiles as selectable tiles. The active profile is highlighted. Tapping a tile sends `set_profile` to apply it and deactivates adaptive (mutually exclusive). Tapping the already-active profile is a no-op — a profile can only be deselected by selecting another profile or activating adaptive.

Each tile shows the profile name and its TDP value (e.g., "45W").

#### Metrics widget

**Type:** Tile row (3 tiles, each 1 column)

Live gauges updated via `subscribe_metrics` push:

| Tile | Primary | Secondary |
|------|---------|-----------|
| CPU | `cpu.temp_c` °C | `cpu.package_watts` W |
| GPU | `gpu.temp_c` °C | `gpu.power_w` W |
| RAM | `ram.load_pct` % | `ram.used_gb` GB |

#### Adaptive widget

**Type:** Full-row

Two parts stacked vertically:

1. **Three-button selector** — `[ Silent ] [ Default ] [ Performance ]`
   - Clicking a button activates adaptive with that tuning preset, sends `set_auto_tune(tuning=...)`. Deactivates the current profile (mutually exclusive)
   - Clicking the **active** button is a no-op — adaptive cannot be disabled directly, only by selecting a profile
   - When a profile is active (not adaptive), all three buttons are unselected

2. **TDP limit slider** — below the toggle buttons
   - Shows and controls `auto_tune.tdp_max_w`
   - Disabled (greyed out) when adaptive is off
   - When the user drags the slider, sends `set_auto_tune(tdp_max_w=...)`
   - The effective ceiling after power-state clamping (`effective_tdp_max_w`) is shown as a secondary label

#### Charge limit widget

**Type:** Tile (1 column)

Single button that cycles through discrete values on press:

```
┌────────────────┐
│   Charge: 85%  │  →  click  →  Charge: 90%  →  click  →  95% → 100% → 75% → ...
└────────────────┘
```

Values: 75 → 80 → 85 → 90 → 95 → 100 → 75 (cyclic). One tap per step. Sends `set_charge_limit(percent=...)`.

#### Power widget

**Type:** Full-row

Two parts stacked vertically:

1. **Status line** — power source icon + label on the left, battery percentage on the right
   - Example: `⚡ DC-In (dedicated)` ... `Battery 91%`
   - Updates on `power_mode_change` events

2. **TDP ceiling slider** — controls `tdp_max_w` for the **current** power state
   - Sends `set_power_profile(state=<current>, tdp_max_w=...)`
   - When the device switches power states, the slider value updates to reflect the new state's configured ceiling
   - The user adjusts the limit for whatever power source they're on right now — no dropdown to select which state to configure
   - This value is the per-power-state ceiling that clamps the adaptive controller: `effective_tdp_max = min(auto_tune.tdp_max_w, power_state.tdp_max_w)`

### Widget configuration

**Order and visibility** are stored as an ordered array in `config.json`:

```json
{
  "home_widgets": ["profiles", "metrics", "adaptive", "charge_limit", "power"],
  "hidden_widgets": [],
  "home_columns": 3
}
```

- Array order = render order (top to bottom)
- Widgets not in `home_widgets` are hidden
- `hidden_widgets` tracks widgets the user has explicitly hidden (for the Settings page UI to show toggles)
- No sort numbers — position in the array IS the sort order. To reorder, move the string. No duplicates possible by design
- New widgets added in future updates: backend detects unknown widget IDs on startup and appends them to the array at the end (default position). The user can reorder via Settings

### Widget framework

Each widget implements a common interface:

```csharp
public interface IHomeWidget
{
    string WidgetId { get; }          // "profiles", "metrics", "adaptive", etc.
    UserControl Control { get; }       // the actual UI element
}
```

Widget order and visibility are managed externally (the home page reads from config, not from the widgets). Widgets don't know about each other — they are independent, self-contained components.

Shared styling is enforced via WinUI 3 resource dictionaries: common colors, spacing, corner radius, typography. All widgets inherit the same visual language.

**Adding a new widget:**
1. Create a `UserControl` + `ViewModel` implementing `IHomeWidget`
2. Register it in the widget collection (service registration)
3. Add its ID to the `home_widgets` config array (appended on first launch for existing users)

## Frontend Visibility

The window simply shows and hides — it does not minimize or appear in the taskbar.

### Triggers

| Trigger | Source |
|---------|--------|
| **Hardware button** | EC register 0x0230 state change → backend button monitor → `ShowWindow` |
| **Tray icon left-click** | `Shell_NotifyIcon` callback → backend toggle function |

Both triggers go through the same toggle function in the backend. The backend tracks visibility state (`bool fe_visible`) because two independent triggers can change it.

### Click outside to hide

The FE monitors `Window.Activated`. When the window loses activation (user clicks another window, the desktop, etc.), the FE hides itself and notifies the backend so `fe_visible` is synced.

### Visibility sync on reconnect

When the FE connects (or reconnects after a crash), it reports its current window visibility state to the backend. Since the FE always spawns hidden, this syncs `fe_visible = false` on fresh start and corrects any stale state after a crash respawn.

