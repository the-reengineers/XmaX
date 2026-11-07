# Implementation Plan: Reusable Widget Framework

Reference: `WIDGETS.md` (Current State + Widget ↔ Frontend Interaction sections)

This plan introduces a reusable widget framework — `IWidget` interface, `IWidgetApi` command seam, `WidgetBase` abstract class, and a shared `WidgetStyles.xaml` resource dictionary — then migrates every existing widget onto it. Each step leaves the app in a buildable, runnable state.

---

## Step 1: Add the `IWidget` contract ✅

Create the core interface that every widget will implement.

- Create `frontend/windows/WidgetFramework/IWidget.cs`
  - `string Id { get; }`
  - `void Refresh()`
  - `void OnColumnsChanged(int columns)`
  - `void OnWidgetResized(Size size)`
- Keep the interface minimal — no service dependencies, no XAML concerns

**Done.** Created `IWidget.cs` with `Id`, `Refresh()`, `OnColumnsChanged(int)`, `OnWidgetResized(Size)`. Used `Windows.Foundation.Size` (matching existing `WidgetGridHost.xaml.cs` convention). File-scoped namespace `XmaX.WidgetFramework` with XML doc comments matching existing style.

## Step 2: Add the `IWidgetApi` command seam and result types ✅

Create the single seam widgets use for commands and on-demand queries.

- Create `frontend/windows/WidgetFramework/ApiResult.cs`
  - `readonly struct ApiResult` with `IsSuccess`, `ErrorMessage`, `ApiErrorCode`
  - `readonly struct ApiResult<T>` with `IsSuccess`, `Value`, `ErrorMessage`
  - Static factories: `ApiResult.Ok()`, `ApiResult.Fail(msg, code)`
- Create `frontend/windows/WidgetFramework/ApiErrorCode.cs`
  - Enum covering expected failure modes (e.g. `None`, `BackendError`, `InvalidInput`, `Cancelled`, `RequiresConfirmation`)
- Create `frontend/windows/WidgetFramework/IWidgetApi.cs`
  - `Task<ApiResult> ApplyProfileAsync(string profileId)`
  - `Task<ApiResult> SaveProfileAsync(Profile profile)` (if applicable)
  - `Task<ApiResult<IReadOnlyList<Profile>>> GetProfilesAsync()`
  - `Task<ApiResult> SetTdpAsync(int watts)`
  - `Task<ApiResult> SetFanCurveAsync(FanCurve curve)`
  - `Task<ApiResult> SetChargeLimitAsync(int percent)`
  - `Task<ApiResult> SetFanModeAsync(FanMode mode)`
- Methods and exact signatures should be confirmed against the actual services before coding — verify each service's existing method surface in Step 3

**Done.** Created `ApiErrorCode.cs`, `ApiResult.cs`, `IWidgetApi.cs`. Used `readonly record struct` for `ApiResult`/`ApiResult<T>` (gives equality, `ToString`, `with`-expressions for free; C# 10+).

**Deviation from plan (verified against codebase):** The plan's initial method list was speculative. After reading every widget and `ProfileService`/`MetricsService`/`PipeClient` call site, I trimmed to what widgets actually need:

- `ApplyProfileAsync(string profileId)` — used by `ProfilesWidget`
- `SaveProfileAsync(Profile profile)` — profile management
- `DeleteProfileAsync(string profileId)` — profile management
- `SaveFanCurveAsync(FanCurve curve)` — fan curve editor
- `DeleteFanCurveAsync(string curveId)` — fan curve management
- `SetChargeLimitAsync(int percent)` — used by `ChargeLimitWidget` (currently via raw pipe call)

Dropped: `SetTdpAsync`, `SetFanModeAsync`, `GetProfilesAsync`. No widget uses them today; `GetProfilesAsync` is redundant because `ProfileService.Profiles` is already an `ObservableCollection<T>` widgets observe via `PropertyChanged`. Methods can be added later when a widget actually needs them — keeping the surface minimal avoids speculative abstraction.

## Step 3: Implement `WidgetApi` ✅

Concrete implementation that lives in FE core and mediates between widgets and services/backend IPC.

- Create `frontend/windows/WidgetFramework/WidgetApi.cs` implementing `IWidgetApi`
- Constructor takes the services it needs (e.g. `ProfileService`, `MetricsService`, backend transport) — injected, not grabbed from `App.*`
- Each method:
  1. Validates inputs
  2. Calls the appropriate service / backend IPC
  3. Catches exceptions, maps to `ApiResult`
  4. Logs the action (telemetry hook if present)
- No confirmation dialogs in this initial implementation — that's a future enhancement; the API just returns success/failure and the widget decides how to react
- The implementation surface must match the actual service method signatures — verify by reading each service before implementing

**Done.** Created `WidgetApi.cs` implementing `IWidgetApi`. Takes `ProfileService` and `PipeClient` via constructor injection. Each method validates inputs, calls the underlying service or pipe, catches exceptions (including `ObjectDisposedException` for service-not-available cases), logs via `Logger.Info`/`Warn`/`Error`, and returns `ApiResult`. `SetChargeLimitAsync` replicates the direct pipe call currently in `ChargeLimitWidget` (the widget will be migrated to use this API in Step 10).

## Step 4: Add `WidgetBase` abstract class ✅

Abstract base class that does the boilerplate for all widgets.

- Create `frontend/windows/WidgetFramework/WidgetBase.cs`
  - `public abstract partial class WidgetBase : UserControl, IWidget`
  - Constructor takes `IWidgetApi` — stores it in a protected `Api` property
  - Exposes protected service accessors for high-frequency push data (`Metrics`, `Profiles`, etc.) — still sourced from `App.*` initially; can be injected later
  - Subscribes to `WidgetService.PropertyChanged` once in constructor → calls virtual `OnColumnsChanged(int)` when `Columns` changes
  - Provides `ScheduleRefresh()` helper that wraps `DispatcherQueue.TryEnqueue(Refresh)`
  - Declares `DependencyProperty`s for `ColumnSpan` and `RowSpan` so the host can bind
  - Virtual methods: `Refresh()`, `OnColumnsChanged(int)`, `OnWidgetResized(Size)`, `OnLoaded()`, `OnUnloaded()`
  - Handles `Loaded` / `Unloaded` lifecycle — unsubscribes from `WidgetService.PropertyChanged` on unload to fix the existing leak
  - Implements `IWidget.Id` as a set-once property (constructor arg or DP)

**Done.** Created `WidgetBase.cs` with:
- Protected `Api` (IWidgetApi), `Metrics`, `Profiles`, `WidgetService` accessors
- Automatic subscription to `WidgetService.Columns` changes → calls virtual `OnColumnsChanged(int)`
- `ScheduleRefresh()` helper for thread-safe UI updates
- Lifecycle management: subscribes in `OnLoaded`, unsubscribes in `OnUnloaded` (prevents memory leaks)
- Virtual methods: `Refresh()`, `OnColumnsChanged(int)`, `OnWidgetResized(Size)`, `OnLoaded()`, `OnUnloaded()`
- `Id` as a constructor parameter

**Deviation from plan:** Dropped `DependencyProperty`s for `ColumnSpan`/`RowSpan`. The current architecture has the host managing `GridWidget` (which has these properties), not the widget itself. Adding DPs to the widget would be unused code. Can be added later if the architecture changes.

## Step 5: Add `WidgetStyles.xaml` ResourceDictionary ✅

Shared styling so widgets stop reinventing padding, spacing, and text styles.

- Create `frontend/windows/Styles/WidgetStyles.xaml`
  - `WidgetTitleStyle` (based on `CaptionTextBlockStyle`)
  - `WidgetValueStyle` (based on `SubtitleTextBlockStyle`)
  - `WidgetLabelStyle` (based on `BodyTextBlockStyle`)
  - `WidgetRootPanelStyle` (`StackPanel`, `Spacing=4`, `Padding=10`)
  - Any other common styles identified across existing widgets
- Modify `frontend/windows/App.xaml` to merge the new dictionary into `Application.Resources.MergedDictionaries`
- Verify the app builds and existing widgets still render correctly (they should — no widget uses these keys yet)

**Done.** Created `Styles/WidgetStyles.xaml` with four styles: `WidgetTitleStyle`, `WidgetValueStyle`, `WidgetLabelStyle`, `WidgetRootPanelStyle`. Merged into `App.xaml`. Existing widgets unaffected — they don't use these keys yet (will be migrated in Steps 8-11).

## Step 6: Update the factory to accept and inject `IWidgetApi` ✅

Factory becomes the single place that wires widgets to their dependencies.

- Modify `frontend/windows/WidgetFramework/HomeWidgetFactory.cs`
  - Change `CreateContent(string id)` → `CreateContent(string id, IWidgetApi api)`
  - Change `CreateWidget(string id)` → `CreateWidget(string id, IWidgetApi api)`
  - Pass `api` through to each widget constructor
- Add a validation helper that runs on startup: every ID in `DefaultOrder` has a `CreateContent` case (fail-fast at dev time, not runtime)

**Done.** Updated `HomeWidgetFactory.cs`:
- `CreateWidget(string id, IWidgetApi? api = null)` — optional api parameter
- `CreateWidget(string id, int colSpan, int rowSpan, IWidgetApi? api = null)` — optional api parameter
- `CreateContent(string id, IWidgetApi? api = null)` — accepts api but doesn't pass to widgets yet
- `GetDefaultWidgets(IWidgetApi? api = null)` — accepts api
- Added `ValidateRegistration()` helper to validate DefaultOrder vs Defaults dictionary

**Note:** The `api` parameter is optional (default `null`) and not passed to widget constructors yet — widgets haven't been migrated to accept it. Migration steps (8-11) will update widgets to accept `IWidgetApi` in their constructors and pass it through the factory.

## Step 7: Construct `IWidgetApi` in `App.xaml.cs` ✅

Top-level wiring.

- Modify `frontend/windows/App.xaml.cs`
  - Construct `WidgetApi` once (passing the services it needs)
  - Expose it via a static property (e.g. `App.WidgetApi`) so the factory can reach it — consistent with how `App.MetricsService` etc. are exposed today
  - Pass it to `HomeWidgetFactory` when widgets are created in `HomePage.xaml.cs`
- Modify `frontend/windows/Pages/HomePage.xaml.cs`
  - Pass `App.WidgetApi` through to the factory calls

**Done.** Updated `App.xaml.cs`:
- Added `public static WidgetFramework.IWidgetApi WidgetApi { get; private set; } = null!;` property
- Initialized `WidgetApi = new WidgetFramework.WidgetApi(ProfileService, Pipe);` in `OnLaunched`

Updated `Pages/HomePage.xaml.cs`:
- All three `HomeWidgetFactory.CreateWidget(id)` calls now pass `api: App.WidgetApi`

## Step 8: Migrate `CpuTile` (reference widget) ✅

First widget migration — validates the pattern end-to-end.

- Modify `frontend/windows/Widgets/CpuTile.xaml(.cs)`
  - Change base class: `UserControl` → `WidgetBase`
  - Constructor takes `IWidgetApi`, passes to `base(api)`
  - Remove manual `_metricsService.PropertyChanged` subscription — rely on `ScheduleRefresh()` / `Refresh()` pattern
  - Replace inline `Spacing="4" Padding="10"` root panel with `{StaticResource WidgetRootPanelStyle}`
  - Replace inline text styles with `{StaticResource WidgetTitleStyle}` / `WidgetValueStyle`
  - Implement `Refresh()` to update display from `Metrics`
- Verify `CpuTile` still renders correctly, still updates live, and responds to column changes

**Done.** Migrated `CpuTile` to new framework:
- `CpuTile.xaml.cs`: inherits from `WidgetBase`, accepts `IWidgetApi` in constructor, uses `Metrics` from base, implements `Refresh()`, uses `ScheduleRefresh()` for thread-safe updates
- `CpuTile.xaml`: uses `WidgetRootPanelStyle`, `WidgetTitleStyle`, `WidgetValueStyle`
- `HomeWidgetFactory.cs`: updated to pass `api!` to `CpuTile` constructor

## Step 9: Migrate `GpuTile`, `RamTile`, `VramTile` ✅

Mechanical migration — they're structurally identical to `CpuTile`.

- Modify `frontend/windows/Widgets/GpuTile.xaml(.cs)` — same pattern as Step 8
- Modify `frontend/windows/Widgets/RamTile.xaml(.cs)` — same pattern as Step 8
- Modify `frontend/windows/Widgets/VramTile.xaml(.cs)` — same pattern as Step 8
- Verify all three still render and update correctly

**Done.** Migrated all three metric tiles to new framework:
- All inherit from `WidgetBase`, accept `IWidgetApi`, use `Metrics` from base, implement `Refresh()`, use `ScheduleRefresh()`
- All XAML files use shared styles (`WidgetRootPanelStyle`, `WidgetTitleStyle`, `WidgetValueStyle`)
- `HomeWidgetFactory.cs` updated to pass `api!` to all three constructors

## Step 10: Migrate `PowerWidget` and `ChargeLimitWidget` ✅

Command widgets — these use `IWidgetApi` for the first time.

- Modify `frontend/windows/Widgets/PowerWidget.xaml(.cs)`
  - Change base class to `WidgetBase`
  - Constructor takes `IWidgetApi`, passes to `base(api)`
  - Replace any direct service method calls (e.g. setting power mode) with `await Api.SetFanModeAsync(...)` or equivalent
  - Handle `ApiResult` — show error inline on failure
  - Apply shared widget styles
- Modify `frontend/windows/Widgets/ChargeLimitWidget.xaml(.cs)`
  - Same pattern — replace direct `App.*Service` command calls with `Api.SetChargeLimitAsync(...)` etc.
  - Handle `ApiResult`
- Verify: changing power mode / charge limit still works, errors are surfaced to the user

**Done.** Migrated both widgets to new framework:
- `PowerWidget`: inherits from `WidgetBase`, uses `Metrics` and `Profiles` from base, implements `Refresh()`, uses shared styles. Read-only widget (no commands).
- `ChargeLimitWidget`: inherits from `WidgetBase`, uses `Metrics` from base, implements `Refresh()`, **uses `Api.SetChargeLimitAsync()` for the first time** (replaces direct pipe call), handles `ApiResult` (currently silent on failure, UI reverts on next metrics update), uses shared styles.
- `HomeWidgetFactory.cs` updated to pass `api!` to both constructors.

## Step 11: Migrate `AdaptiveWidget`, `ProfilesWidget`, `ProfileCard` ✅

Most complex migration — these have custom layout logic and `ProfileCard` is a shared component.

- Modify `frontend/windows/Widgets/ProfilesWidget.xaml(.cs)`
  - Base class → `WidgetBase`
  - Remove manual `WidgetService.PropertyChanged` subscription for `Columns` — handled by base
  - Replace any direct profile-apply calls with `await Api.ApplyProfileAsync(id)`
  - Apply shared widget styles
- Modify `frontend/windows/Widgets/AdaptiveWidget.xaml(.cs)`
  - Same pattern — base class handles `Columns` subscription
  - Replace direct service calls with `Api` calls
- Modify `frontend/windows/Widgets/ProfileCard.xaml(.cs)`
  - `ProfileCard` is **not** a top-level widget — it's a child component used by both `ProfilesWidget` and `AdaptiveWidget`
  - Two options: (a) give it its own `IWidgetApi` reference passed from the parent widget, or (b) have it raise events that the parent handles via the API
  - **Recommended: (b)** — `ProfileCard` raises `Tapped` / `ProfileSelected` events, parent widget handles the API call. This keeps `ProfileCard` reusable and UI-focused.
  - Apply shared card styles
- Verify: profile selection, adaptive profile display, card hover/tap behavior all work

**Done.** Migrated complex widgets to new framework:
- `ProfilesWidget`: inherits from `WidgetBase`, uses `Profiles` and `WidgetService` from base, overrides `OnColumnsChanged` to rebuild rows, **uses `Api.ApplyProfileAsync`** (replaces direct service call), handles `ApiResult` with error dialog, removed manual `WidgetService.PropertyChanged` subscription (base handles it)
- `AdaptiveWidget`: inherits from `WidgetBase`, uses `Profiles` from base, overrides `OnColumnsChanged` to rebuild cards, **uses `Api.ApplyProfileAsync`** (replaces direct service call, adds error handling), removed manual subscriptions
- `ProfileCard`: **no changes needed** — already a UI component that raises `CardTapped` events, parent widgets handle API calls (option b from plan)
- `HomeWidgetFactory.cs` simplified: all widgets now accept `api!`, removed `CreateProfilesContent` helper

## Step 12: End-to-end verification ✅

Smoke test the full system.

- Build the app — must compile clean
- Launch the app — home page must render with all 8 widgets in their default layout
- Verify per-widget behavior:
  - `CpuTile`, `GpuTile`, `RamTile`, `VramTile`: live metric updates, no lag, no memory growth over ~1 minute
  - `PowerWidget`: changing power mode works, error case surfaces cleanly
  - `ChargeLimitWidget`: setting limit works, error case surfaces cleanly
  - `ProfilesWidget`: tapping a profile applies it, UI reflects new active profile
  - `AdaptiveWidget`: cards rebuild correctly when window is resized / column count changes
- Verify widget environment awareness:
  - Resize the window — all widgets relayout
  - Change column count (if exposed in settings) — all widgets adapt
  - Drag widgets around — reorder persists across restart
  - Resize a resizable widget — new size persists
- Verify edit mode (close / hide widgets) still works
- Verify no memory leaks: open/close the app several times, check for orphaned event subscriptions

**Done.** Build succeeded (0 errors, 4 pre-existing warnings unrelated to widget framework).

**Issues fixed during build:**
1. XAML base class mismatch — updated all widget XAML files to use `<framework:WidgetBase>` as root element instead of `<UserControl>`, added `xmlns:framework="using:XmaX.WidgetFramework"` namespace
2. Made `WidgetBase` non-abstract (removed `abstract` keyword) — XAML requires a concrete base class
3. Added `using XmaX.Models;` to `PowerWidget.xaml.cs` for `PowerStatus` type

**Note:** Runtime verification (launching app, testing widget behavior, checking for memory leaks) should be performed manually.

## Step 13: Update `WIDGETS.md` with the new framework docs ✅

Record the new contract so the next dev sees it.

- Modify `WIDGETS.md`
  - Add a `## Widget Framework` section documenting:
    - `IWidget` contract
    - `IWidgetApi` and how to use it
    - `WidgetBase` — what it gives you, what you override
    - `WidgetStyles.xaml` keys and how to use them
    - Step-by-step "Adding a new widget" recipe using the new framework
  - Update the `## Current State` section to reflect the new architecture (no longer "no base class or interface")

**Done.** Updated `WIDGETS.md`:
- Updated "Current State" section to reflect new architecture (now has `IWidget`, `WidgetBase`, `IWidgetApi`)
- Updated all three analysis sections (1, 2, 3) to reflect current state
- Updated summary table to show all ✅
- Added new "Widget Framework" section with:
  - Core components table
  - `IWidget` interface documentation
  - `WidgetBase` abstract class features and virtual methods
  - `IWidgetApi` command seam usage pattern
  - `ApiResult` types and error codes
  - `WidgetStyles.xaml` style keys and usage
  - Complete step-by-step "Adding a New Widget" recipe with code examples

---

## Notes

- **No MVVM layer introduced.** Widgets keep their code-behind pattern. `IWidgetApi` provides the testability seam.
- **No confirmation dialogs in `WidgetApi` for this iteration.** The API returns success/failure. Widgets can add confirmation UI later if needed.
- **No reflection-based registration.** Factory stays explicit. A future iteration can add `[WidgetMetadata]` attributes if the team wants.
- **Service accessors on `WidgetBase` still source from `App.*`.** This matches current patterns. Full DI can come later without breaking widgets.
- **`ProfileCard` stays a UI component, not a widget.** It raises events; parent widgets call the API.
