# Implementation Plan: Custom Power Profiles

Reference: User request to add editable power profiles (TDP + fan curve + adaptive) with CLI + extension settings UI, replacing GNOME's native profile selector.

**Background:** The backend profile system is already complete (Fixed + Adaptive profiles, fan curves, JSON persistence, CRUD IPC). The gap is D-Bus exposure + UI. This plan adds D-Bus methods, CLI commands, and GNOME extension UI.

---

## Step 1: Expose profile operations via D-Bus

Add D-Bus methods to `DbusService` so the GNOME extension and `xmaxctl` can manage profiles remotely. Currently profiles are only accessible via the AF_UNIX socket.

**Files to modify:**
- `backend/src/dbus/dbus_service.h` — add method declarations
- `backend/src/dbus/dbus_service.cpp` — implement handlers, add vtable entries

**D-Bus methods to add:**
- `GetProfiles()` → `s` (JSON array of profiles)
- `SetProfile(s id)` → apply profile to hardware
- `SaveProfile(s profile_json)` → create/update profile on disk
- `DeleteProfile(s id)` → remove profile
- `GetFanCurves()` → `s` (JSON array of fan curves)
- `SaveFanCurve(s curve_json)` → create/update fan curve
- `DeleteFanCurve(s id)` → remove fan curve

**Polkit:** Write operations (Save/Delete) require authorization (`org.xmax.set-profile`, `org.xmax.set-fan-curve`). Read operations (Get) don't.

**Dependencies:** None — uses existing `TransportService::dispatch()` for profile CRUD.

---

## Step 2: Add profile subcommands to xmaxctl

Add CLI commands for profile and fan-curve management. Each command uses D-Bus first, falls back to AF_UNIX socket.

**Files to modify:**
- `backend/src/xmaxctl.cpp`

**Subcommands to add:**
- `xmaxctl profiles list` — list all profiles (JSON)
- `xmaxctl profiles set <id>` — apply profile
- `xmaxctl profiles save --name <name> --type <fixed|adaptive> [--stapm <w> --fast <w> --slow <w> --fan-curve <id>] [--tuning <silent|default|performance> --target-temp <c> --tdp-max <w> --fan-max <pct>] [--power-state <battery|usb-c-slow|usb-c-fast|dc-in>]`
- `xmaxctl profiles delete <id>`
- `xmaxctl fan-curves list` — list fan curves
- `xmaxctl fan-curves save --id <slug> --name <name> --points <temp:speed,temp:speed,...>`
- `xmaxctl fan-curves delete <id>`

**Argument parsing:** Use simple positional/flag parsing (no external library). Validate required args per subcommand.

---

## Step 3: Add profile methods to XmaxClient in dbus.js

Add profile-related methods to the GNOME extension's D-Bus client.

**Files to modify:**
- `frontend/gnome/xmax@local/dbus.js`

**Methods to add:**
- `getProfiles()` → array of profile objects
- `setProfile(id)` → apply profile
- `saveProfile(profileObj)` → create/update profile
- `deleteProfile(id)` → remove profile
- `getFanCurves()` → array of fan curve objects
- `saveFanCurve(curveObj)` → create/update fan curve
- `deleteFanCurve(id)` → remove fan curve

**Implementation:** Synchronous `connection.call_sync()` to `org.xmax.Service`, parse JSON responses.

---

## Step 4: Replace GNOME's power profile selector with XmaX profiles [DONE]

Modify the Quick Settings `qs-power-profile` toggle to show XmaX profiles instead of UPower profiles. This becomes the primary profile selector.

**Completed changes:**
- `quickSettings.js`: Replaced `_buildPowerProfileToggle()` (UPower-based) with `_buildProfileToggle()` (XmaX profile selector). Menu populated from `client.getProfiles()`, selection calls `client.setProfile(id)`. Icons map tuning presets (Silent/Default/Performance) and adaptive type. Active profile tracked in memory.
- `dbus.js`: Removed UPower.PowerProfiles client code (`getPowerProfile`, `getPowerProfiles`, `setPowerProfile`, `_ppGet`). Header comment updated.
- Extension symlink fixed to point to source tree. Schemas compiled.

**Files to modify:**
- `frontend/gnome/xmax@local/quickSettings.js` — replace `_buildPowerProfileToggle()` with profile selector
- `frontend/gnome/xmax@local/extension.js` — pass profile list to Quick Settings, handle profile changes

**UI design:**
- Quick Menu Toggle with profile name as subtitle
- Menu lists all profiles (Fixed + Adaptive) with type indicator
- Selecting a profile calls `setProfile(id)` and updates subtitle
- On metrics update, refresh current profile name (if daemon auto-switches on power state change)

**Schema change:** Repurpose `qs-power-profile` to control visibility of the new XmaX profile selector (instead of UPower). No schema migration needed — same key, different backend.

**Behavior:**
- On extension enable: fetch profiles, populate menu
- On profile selection: call `setProfile(id)`, update subtitle
- On `MetricsChanged` signal: refresh current profile display (adaptive controller may auto-switch)

**Note:** GNOME's native power profile selector (in system Quick Settings) cannot be hidden by extensions. Users will see both, but XmaX's selector takes precedence for hardware control. Document this.

---

## Step 5: Add profile management UI to extension preferences [DONE]

Add a "Profiles" page to `prefs.js` for creating, editing, and deleting profiles and fan curves.

**Completed changes:**
- `prefs.js`: Added second "Profiles" page with:
  - **Profiles section**: List of profiles (Adw.ActionRow per profile showing name + type), Create button, Edit/Delete buttons per row
  - **Profile editor dialog** (Adw.Dialog): Name, ID (slug), Type (Fixed/Adaptive), type-specific fields (Fixed: STAPM/Fast/Slow TDP + fan curve selector; Adaptive: tuning preset + target temp + TDP max + fan max), power state assignment
  - **Fan curves section**: List of fan curves with point count, Create button, Edit/Delete buttons per row
  - **Fan curve editor dialog**: Name, ID, dynamic point editor (temp→speed pairs with add/remove)
  - Type-specific field visibility toggle (Fixed vs Adaptive fields shown/hidden based on type selection)
  - Confirmation dialogs for delete operations
  - Daemon-unreachable placeholder page
- Uses `XmaxClient` from dbus.js to call profile/fan-curve CRUD methods
- All dialogs use libadwaita widgets (Adw.PreferencesPage, Adw.ActionRow, Adw.ComboRow, Adw.SpinRow, Adw.EntryRow, Adw.AlertDialog)

**Files to modify:**
- `frontend/gnome/xmax@local/prefs.js` — add Profiles page with editor UI

**UI structure:**
- **Profiles page** (Adw.PreferencesPage):
  - "Profiles" group: list of profiles (Adw.ActionRow per profile, showing name + type)
  - "Create Profile" button → opens profile editor dialog
  - Each profile row has Edit/Delete buttons
- **Profile editor** (Adw.Dialog or Adw.PreferencesDialog):
  - Name (Adw.EntryRow)
  - Type (Adw.ComboRow: Fixed / Adaptive)
  - **For Fixed:**
    - STAPM (Adw.SpinRow, 6-120W)
    - Fast (Adw.SpinRow, 6-120W)
    - Slow (Adw.SpinRow, 6-120W)
    - Fan curve (Adw.ComboRow, populated from `getFanCurves()`)
  - **For Adaptive:**
    - Tuning (Adw.ComboRow: silent / default / performance)
    - Target temp (Adw.SpinRow, 60-95°C)
    - TDP max (Adw.SpinRow, 6-120W)
    - Fan max % (Adw.SpinRow, 0-100%)
  - Power state assignment (Adw.ComboRow: None / Battery / USB-C Slow / USB-C Fast / DC-In)
  - Save / Cancel buttons
- **Fan Curves section** (in same page or separate group):
  - List of fan curves (name, point count)
  - Create/Edit/Delete buttons
  - Simple point editor (Adw.SpinRow pairs for temp:speed)

**Dependencies:** Step 3 (dbus.js client methods) must be complete.

---

## Step 6: Verify end-to-end [DONE]

Test the full profile workflow: CLI, Quick Settings selector, preferences UI.

**Verification completed:**
- Backend rebuilt: xmaxctl now includes profile/fan-curve subcommands
- Profile storage exists: `~/.config/xmax/profiles.json` contains 3 profiles (auto, auto-tune, fixed) and 1 fan curve (quiet)
- Extension files in place via symlink: dbus.js (7.6K), quickSettings.js (8.0K), prefs.js (24K)
- All JS files pass syntax validation
- GSettings schemas compiled

**Interactive testing required (needs polkit auth + session restart):**
1. **CLI**: Run `xmaxctl profiles list`, `save`, `set`, `delete` with polkit auth (pkexec or sudo)
2. **Quick Settings**: After logout/login, enable `qs-power-profile` toggle, verify XmaX profile selector appears in Quick Settings panel, test profile selection
3. **Preferences**: Run `gnome-extensions prefs xmax@local`, navigate to Profiles page, test create/edit/delete for profiles and fan curves
4. **Session restart**: Logout/login to clear duplicate Quick Settings controls and load new extension code cleanly

**Note**: The daemon is running on the system bus. CLI commands require polkit authentication. The GNOME Shell extension will authenticate via the session's polkit agent.

**Verification steps:**
1. Rebuild daemon: `just build-be`
2. Reinstall extension: `just reinstall-fe`
3. Start daemon: `sudo scripts/linux/verify-dbus.sh` (or `backend/build-linux/xmaxd`)
4. Test CLI:
   ```bash
   xmaxctl profiles list
   xmaxctl profiles save --name "Silent" --type adaptive --tuning silent --target-temp 80
   xmaxctl profiles set silent
   xmaxctl profiles delete silent
   ```
5. Test Quick Settings:
   - Enable profile selector: `gsettings set org.xmax.shell qs-power-profile true`
   - Open Quick Settings → verify XmaX profile selector appears
   - Select a profile → verify hardware changes (TDP/fan)
6. Test preferences:
   - Open extension preferences → verify Profiles page
   - Create a profile → verify it appears in list and Quick Settings
   - Edit a profile → verify changes persist
   - Delete a profile → verify it's removed
7. Run `just dev-gui` to verify extension loads cleanly in headless shell.

---

## Summary

**Total steps:** 6

**What this plan covers:** Exposes the existing backend profile system via D-Bus, adds CLI commands for profile management, and builds GNOME extension UI (Quick Settings selector + preferences editor) so users can create, edit, and apply custom power profiles (Fixed + Adaptive) that replace GNOME's native profile selector.

**Key decisions:**
- Profiles stored in JSON (already implemented in backend)
- D-Bus methods added to `DbusService` (currently only AF_UNIX)
- `qs-power-profile` repurposed to show XmaX profiles (not UPower)
- Preferences UI uses Adw widgets (libadwaita)
- GNOME's native profile selector cannot be hidden (documented limitation)

**Files created:** None (all modifications to existing files)

**Files modified:**
- Backend: `dbus_service.h`, `dbus_service.cpp`, `xmaxctl.cpp`
- Extension: `dbus.js`, `quickSettings.js`, `extension.js`, `prefs.js`

---

Does this plan look correct, or would you like changes before I begin implementing?
