# Implementation Plan: Session Persist Feature

## Overview

Change the `persist` flag semantics: when `persist=false`, users can still apply hardware settings manually via a session-level toggle. Add a "test mode" banner to the navigation footer and a factory reset button to Settings.

**Behavior change:**
- `persist=false` (current): All hardware writes rejected
- `persist=false` (proposed): Hardware writes allowed when `session_persist=true`

**Session persist:**
- In-memory flag that starts as a copy of `persist` on backend startup
- Can be toggled at runtime via UI
- Lost when backend service stops
- When toggled ON: immediately applies all configured settings to hardware

**Factory reset:**
- New button at bottom of Settings page
- Deletes all user configs, profiles, fan curves
- Restores factory defaults
- Warning styling

---

## Step 1: Add session_persist field to backend Config struct ✓

Add `session_persist` field to `Config` struct in `backend/src/config.h`. This field is in-memory only and not serialized to JSON.

**Files:** `backend/src/config.h`

**Changes:**
- Add `bool session_persist = false;` field to `Config` struct
- Add comment explaining it's in-memory only

**Completed:** Added `session_persist` field with comment after `persist` field.

---

## Step 2: Initialize session_persist from persist on backend startup ✓

Modify `load_config()` in `backend/src/config.cpp` to initialize `session_persist` from the loaded `persist` value.

**Files:** `backend/src/config.cpp`

**Changes:**
- At the end of `load_config()`, after loading all fields, add: `config.session_persist = config.persist;`
- This ensures session starts as a copy of the disk-persisted value

**Completed:** Added initialization in three places: normal load path, file-not-found path, and exception/corruption path.

---

## Step 3: Add set_session_persist dispatch case ✓

Add dispatch case for `set_session_persist` command in `backend/src/transport.cpp`.

**Note:** This codebase uses string-based method dispatch, not a CommandType enum. No changes needed to protocol.h/protocol.cpp.

**Files:** `backend/src/transport.cpp`

**Changes:**
- Add dispatch case: `if (cmd.method == "set_session_persist") return handle_set_session_persist(cmd);`

**Completed:** Added dispatch case after `set_config` handler.

---

## Step 4: Implement set_session_persist handler in transport ✓

Add command handler in `backend/src/transport.h` and `backend/src/transport.cpp` to set `session_persist` and apply all settings when transitioning from false to true.

**Files:** `backend/src/transport.h`, `backend/src/transport.cpp`

**Changes:**
- `transport.h`: Add `auto handle_set_session_persist(const Command& cmd) -> Response;` declaration
- `transport.h`: Add `void apply_all_settings();` private method declaration
- `transport.cpp`: Implement `handle_set_session_persist`:
  - Parse `value` from payload (boolean)
  - Store previous `session_persist` value
  - Update `config_.session_persist` (under lock)
  - If transitioning from false to true: call `apply_all_settings()`
  - Return success response
- `transport.cpp`: Implement `apply_all_settings()`:
  - Applies charge limit if configured (75-100)
  - Gets current power state and applies corresponding profile
  - Applies profile TDP limits and fan curve if configured
  - Activates adaptive controller if configured

**Completed:** Implemented both handlers in transport.cpp:1266-1347.

---

## Step 5: Modify check_persist to use session_persist ✓

Change the persist gate in `backend/src/transport.cpp` to check `session_persist` instead of `persist`.

**Files:** `backend/src/transport.cpp`

**Changes:**
- In `check_persist()` method, read `config_.session_persist` instead of `config_.persist`
- Keep the rest of the logic unchanged

**Completed:** Changed `check_persist()` at line 210 to use `session_persist` instead of `persist`.

---

## Step 6: Return session_persist in get_config response ✓

Modify `handle_get_config()` in `backend/src/transport.cpp` to include `session_persist` in the response data.

**Files:** `backend/src/transport.cpp`

**Changes:**
- In `handle_get_config()`, add: `data["session_persist"] = config_.session_persist;`

**Completed:** Added `session_persist` to get_config response at line 1196.

---

## Step 7: Implement restore_defaults command ✓

Add a factory reset command that deletes all user configs and restores defaults.

**Files:** `backend/src/transport.h`, `backend/src/transport.cpp`

**Changes:**
- `transport.cpp`: Add dispatch case for `restore_defaults`
- `transport.h`: Add `auto handle_restore_defaults(const Command& cmd) -> Response;` declaration
- `transport.cpp`: Implement handler:
  - Reset config to defaults (call `get_default_config()`)
  - Reset session_persist to false
  - Save default config to disk
  - Clear all profiles from storage
  - Clear all fan curves from storage
  - Save empty profiles to disk
  - Set fan to Auto mode (hardware reset)
  - Return success response

**Completed:** Implemented factory reset at lines 1307-1332. Clears config, profiles, fan curves, and resets fan to Auto.

---

## Step 8: Add SessionPersist property to frontend AppConfig ✓

Add `SessionPersist` field to `AppConfig` model in `frontend/windows/Models/AppConfig.cs`.

**Files:** `frontend/windows/Models/AppConfig.cs`

**Changes:**
- Add `SessionPersist` property with JSON attribute and documentation
- Update `ToString()` to include SessionPersist

**Completed:** Added SessionPersist property at lines 24-30 and updated ToString() at line 57.

---

## Step 9: Add SessionPersist property to SettingsViewModel ✓

Add `SessionPersist` property to `SettingsViewModel` that sends `set_session_persist` command when changed.

**Files:** `frontend/windows/ViewModels/SettingsViewModel.cs`

**Changes:**
- Add `SessionPersist` property that sends `set_session_persist` command when toggled

**Completed:** Added SessionPersist property at lines 82-94 with change notification and backend sync.

---

## Step 10: Add test mode banner to MainWindow navigation ✓

Add a warning banner above the NavigationView in `MainWindow.xaml` that shows when `persist=false`.

**Files:** `frontend/windows/MainWindow.xaml`, `frontend/windows/MainWindow.xaml.cs`

**Changes:**
- `MainWindow.xaml`: Add TestModeBanner with warning background and ToggleSwitch
- `MainWindow.xaml.cs`: Add LoadConfigAndUpdateBannerAsync() and OnSessionPersistToggled()

**Completed:** Added banner UI and event handlers. Banner shows when persist=false, toggle sends set_session_persist command.

---

## Step 11: Add localization strings for banner ✓

Add localization strings for the test mode banner to `shared/locales/en.json` and `shared/locales/zh.json`, then regenerate `frontend/windows/Generated/Loc.cs`.

**Files:** `shared/locales/en.json`, `shared/locales/zh.json`, `frontend/windows/Generated/Loc.cs`

**Changes:**
- Added `nav.test_mode` and `nav.apply` to en.json and zh.json
- Regenerated Loc.cs via `python scripts/generate_locales.py`

**Completed:** Added 2 new locale keys and regenerated Loc.cs (101 keys total).

---

## Step 12: Add factory reset button to Settings page ✓

Add a "Restore Defaults" button at the bottom of Settings page with warning styling.

**Files:** `frontend/windows/Pages/SettingsPage.xaml`, `frontend/windows/Pages/SettingsPage.xaml.cs`

**Changes:**
- `SettingsPage.xaml`: Add factory reset section with warning border and button
- `SettingsPage.xaml.cs`: Add labels from Loc and OnFactoryResetClick handler

**Completed:** Added danger zone section with warning border at lines 98-116. Handler sends restore_defaults command and reloads config.

---

## Step 13: Add localization strings for factory reset ✓

Add localization strings for the factory reset button and confirmation dialog.

**Files:** `shared/locales/en.json`, `shared/locales/zh.json`, `frontend/windows/Generated/Loc.cs`

**Changes:**
- Added 3 new keys to en.json and zh.json
- Regenerated Loc.cs via `python scripts/generate_locales.py`

**Completed:** Added settings.restore_defaults, settings.restore_defaults_desc, settings.restore_defaults_confirm. Loc.cs now has 104 keys.

---

## Step 14: Verify backend changes [COMPLETE]

Build and test backend to ensure:
- `session_persist` is initialized correctly
- `set_session_persist` command works
- `check_persist` uses `session_persist`
- `restore_defaults` command works

**Actions:**
- Build backend: `cmake --build backend/build` ✓
- Run backend tests: `cd backend/build && ctest` ✓ (155/155 passing)
- Manual test: start backend, send commands via test client
  - Verify `get_config` returns `session_persist`
  - Send `set_session_persist` with `value: true`
  - Verify hardware commands are now allowed
  - Send `restore_defaults`
  - Verify config and profiles are reset

**Notes:**
- Updated 5 existing tests to set both `persist = true` and `session_persist = true` since `check_persist()` now checks `session_persist` instead of `persist`
- Tests updated: SetFanPersistEnabled, SetFanInvalidMode, SetProfileNotFound, SetChargeLimitInvalidRange, SetAutoTunePersistEnabled

---

## Step 15: Verify frontend changes [COMPLETE]

Build and test frontend to ensure:
- Banner shows when `persist=false`
- Toggle sends command and updates state
- Factory reset button works

**Actions:**
- Build frontend: `cd frontend/windows && dotnet build` ✓ (0 warnings, 0 errors)
- Run frontend tests: `cd frontend/windows && dotnet test` ✓ (91/91 passing)
- Manual test:
  - Set `persist=false` in config
  - Start app
  - Verify banner shows
  - Toggle "Apply" on
  - Verify hardware commands work
  - Restart backend
  - Verify banner shows again (session_persist lost)
  - Click "Restore Defaults"
  - Verify config and profiles reset
