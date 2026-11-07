# Home Page Widget System

## Current State

The widget system lives under `frontend\windows` and is built around a **reusable widget framework** with the following layers:

| Layer | File(s) | Role |
|---|---|---|
| **Framework** | `WidgetFramework\IWidget.cs` | Core widget interface contract |
| **Framework** | `WidgetFramework\WidgetBase.cs` | Abstract base class providing boilerplate (service access, lifecycle, column change handling) |
| **Framework** | `WidgetFramework\IWidgetApi.cs` | Command seam for widgets to request hardware/profile changes |
| **Framework** | `WidgetFramework\WidgetApi.cs` | Concrete `IWidgetApi` implementation (mediates between widgets and services/backend) |
| **Framework** | `WidgetFramework\ApiResult.cs` | Result types for widget API operations |
| **Model** | `WidgetFramework\GridWidget.cs` | Wraps any widget with span/min/max/position metadata |
| Host | `WidgetFramework\WidgetGridHost.xaml(.cs)` | Canvas-based container; creates a styled `Border` around each widget |
| Factory | `WidgetFramework\HomeWidgetFactory.cs` | Registry of widget IDs → content creators + default sizes |
| Persistence | `Services\WidgetService.cs` | Saves/loads layout order, sizes, hidden widgets via IPC |
| Interaction | `DragReflowController.cs`, `GridLayoutEngine.cs` | Drag-reflow, resize, hit-testing |
| Styling | `Styles\WidgetStyles.xaml` | Shared resource dictionary for widget styles |

Existing widgets: `CpuTile`, `GpuTile`, `RamTile`, `VramTile`, `PowerWidget`, `ChargeLimitWidget`, `AdaptiveWidget`, `ProfilesWidget` (+ reusable `ProfileCard`). All inherit from `WidgetBase` and follow a consistent pattern.

### 1. Can a new widget follow a known structure?

**Yes.** All widgets inherit from `WidgetBase` which implements the `IWidget` interface. The recipe for a new widget is:

1. Create `Widgets\NewWidget.xaml(.cs)` with XAML root element `<framework:WidgetBase>` and code-behind inheriting from `WidgetBase`.
2. Constructor accepts `IWidgetApi`, passes to `base(api, "widget_id")`.
3. Override `Refresh()` to update UI from services (use `Metrics`, `Profiles` from base class).
4. Subscribe to service `PropertyChanged` events and call `ScheduleRefresh()` for thread-safe UI updates.
5. Optionally override `OnColumnsChanged(int columns)` to adapt layout when grid column count changes.
6. Register in `HomeWidgetFactory.cs`:
   - Add the ID to `DefaultOrder`
   - Add a defaults tuple `(colSpan, rowSpan, alwaysFillRow)` to `Defaults`
   - Add a case to `CreateContent()` that instantiates the widget with `api!`

See `CpuTile.xaml.cs` for a reference implementation.

### 2. Can it reuse the parent container XAML and styling?

**Yes — this is already handled for you.** `WidgetGridHost.CreateContainer()` (`WidgetGridHost.xaml.cs:377-479`) builds the wrapper `Border` for every widget uniformly:

- Corner radius (8.0, or 0 for `AlwaysFillRow` widgets like `ProfilesWidget`)
- Background, shadow, accent border during drag
- `CompositeTransform` for positioning/animation
- Close/resize buttons in edit mode

A new widget receives its container for free. You only author the *content* (the widget XAML). For internal styling, use the shared styles from `Styles\WidgetStyles.xaml`:

- `WidgetTitleStyle` — secondary text for labels (e.g. "CPU", "GPU")
- `WidgetValueStyle` — primary text for values (e.g. temperatures, percentages)
- `WidgetLabelStyle` — body text for descriptions
- `WidgetRootPanelStyle` — `StackPanel` with `Spacing=4`, `Padding=10` for simple widgets

These styles use WinUI system theme resources, so they automatically adapt to light/dark theme.

### 3. Is it aware of its environment?

**Yes — environment awareness is automatic via `WidgetBase`.**

| Awareness | How it works | Coverage |
|---|---|---|
| **Container resize** | `WidgetGridHost.OnScrollViewerSizeChanged` + `OnSizeChanged` recompute cell width and relayout all widgets | Automatic for all widgets |
| **Column count change** | `WidgetBase` subscribes to `WidgetService.Columns` changes and calls virtual `OnColumnsChanged(int)` | **Automatic.** Widgets override `OnColumnsChanged` to adapt layout. |
| **Drag / reorder** | `DragReflowController` fires pointer events; `WidgetGridHost` persists the new layout via `WidgetService` | Automatic for all widgets |
| **Resize (span change)** | `DragReflowController` handles edge-drag resize; only widgets where `MinSpan != MaxSpan` expose resize handles (`GridWidget.IsResizable`) | Opt-in via min/max span in factory |
| **Scroll snap** | `WidgetGridHost` snaps to row multiples on scroll settle | Automatic for all widgets |
| **Lifecycle** | `WidgetBase` handles `Loaded`/`Unloaded` events, subscribes/unsubscribes from services to prevent memory leaks | Automatic for all widgets |

Widgets that need to respond to column count changes (e.g. `ProfilesWidget`, `AdaptiveWidget`) override `OnColumnsChanged(int columns)` and call `ScheduleRefresh()`.

### Summary

| Question | Answer |
|---|---|
| Known structure? | ✅ Enforced via `IWidget` interface and `WidgetBase` abstract class. Consistent pattern across all widgets. |
| Reuse container/styling? | ✅ Container is fully reusable (built by host). Internal styling uses shared `WidgetStyles.xaml` resource dictionary. |
| Environment aware? | ✅ Automatic via `WidgetBase`. Column-count changes handled via `OnColumnsChanged` override. Lifecycle management prevents memory leaks. |

## Widget ↔ Frontend Interaction

There are two directions to think about, and they have different shapes:

- **Data in** — widgets reading metrics, profiles, hardware state (high-frequency, push-based)
- **Commands out** — widgets requesting profile changes, TDP changes, fan curve writes (low-frequency, request/response)

Treating them the same leads to either too much ceremony for metrics or too little structure for commands. Splitting them is the right move.

### 1. Data in: keep the service subscription pattern

For high-frequency, continuously-changing data (CPU temp, GPU load, fan RPMs), the current pattern is already correct:

```csharp
_metricsService.PropertyChanged += OnMetricsChanged;
// → DispatcherQueue.TryEnqueue(UpdateDisplay);
```

Push-based is right here — polling via an async API would waste cycles and add latency. The `WidgetBase` handles the subscription boilerplate so individual widgets don't have to.

**Small improvement:** widgets that need on-demand queries ("what profiles exist?", "what's the current TDP limit?") shouldn't grab `App.ProfileService` directly. Those belong in the Widget API (below) because they're request/response, not streaming.

### 2. Commands out: introduce a Widget API

This is the real gap. Right now, a widget that wants to apply a profile probably calls `App.ProfileService.ApplyProfile(id)` directly. That's fine today, but it means:

- Error handling lives in every widget, duplicated
- Confirmation dialogs ("are you sure you want to switch profiles?") are reinvented per widget
- Telemetry/logging of user actions is scattered
- Backend IPC details leak into widget code
- Testing a widget means mocking a real service

Introduce an `IWidgetApi` that is **the single seam between widgets and the rest of the app**:

```csharp
public interface IWidgetApi
{
    // Profiles
    Task<ApiResult>            ApplyProfileAsync(string profileId);
    Task<ApiResult>            SaveProfileAsync(Profile profile);
    Task<ApiResult<IReadOnlyList<Profile>>> GetProfilesAsync();

    // Hardware
    Task<ApiResult>            SetTdpAsync(int watts);
    Task<ApiResult>            SetFanCurveAsync(FanCurve curve);
    Task<ApiResult>            SetChargeLimitAsync(int percent);
    Task<ApiResult>            SetFanModeAsync(FanMode mode);
}

public readonly struct ApiResult
{
    public bool                 IsSuccess { get; init; }
    public string?              ErrorMessage { get; init; }
    public ApiErrorCode         ErrorCode { get; init; }
    public static ApiResult Ok() => new() { IsSuccess = true };
    public static ApiResult Fail(string msg, ApiErrorCode code = ...) => ...;
}

public readonly struct ApiResult<T>
{
    public bool      IsSuccess { get; init; }
    public T?        Value { get; init; }
    public string?   ErrorMessage { get; init; }
}
```

**What lives behind `IWidgetApi`:**

The implementation is in FE core (not in any widget), and it:

1. Validates inputs
2. Shows confirmation dialogs if the operation is dangerous (via a `IConfirmDialogService` or similar)
3. Calls the appropriate service(s) / backend IPC
4. Catches exceptions and maps them to `ApiResult`
5. Logs the action (telemetry, diagnostics)
6. Returns a uniform result

Widgets never see exceptions. They never see IPC. They never think about confirmation flows. They call, await, handle the `ApiResult`.

### 3. How widgets get the API

Through `WidgetBase`'s constructor, wired up by the factory:

```csharp
public abstract class WidgetBase : UserControl, IWidget
{
    protected IWidgetApi   Api { get; }
    protected MetricsService Metrics { get; }  // for high-frequency push data
    protected ProfileService Profiles { get; } // for high-frequency push data

    protected WidgetBase(IWidgetApi api)
    {
        Api      = api;
        Metrics  = App.MetricsService;
        Profiles = App.ProfileService;
        // base class wires Columns subscription, lifecycle, etc.
    }
}
```

A command widget (e.g. `ProfilesWidget`) uses `Api`:

```csharp
private async void OnProfileTapped(Profile profile)
{
    var result = await Api.ApplyProfileAsync(profile.Id);
    if (!result.IsSuccess) ShowError(result.ErrorMessage);
}
```

A metrics widget (e.g. `CpuTile`) ignores `Api` entirely and just reads from `Metrics`.

The factory:

```csharp
private static object CreateContent(string id, IWidgetApi api) => id switch
{
    "cpu"      => new CpuTile(api),
    "profiles" => new ProfilesWidget(api),
    ...
};
```

`IWidgetApi` is constructed once in `App.xaml.cs` and passed through.

### 4. Example walk-through: "user taps a profile card"

Concrete flow, so you can see where each piece lives:

1. User taps `ProfileCard` inside `ProfilesWidget`
2. `ProfilesWidget.OnProfileTapped(profile)` calls `await Api.ApplyProfileAsync(profile.Id)`
3. `WidgetApi` implementation:
   - Checks if the profile is already active (short-circuit → `Ok`)
   - Optionally asks the user to confirm (if the profile changes TDP, say)
   - Calls `ProfileService.ApplyProfileAsync(profile.Id)` → backend IPC
   - On success: logs the action, returns `Ok`
   - On failure: maps backend error to `ApiErrorCode`, returns `Fail`
4. `ProfilesWidget` receives `ApiResult`:
   - Success → UI updates naturally via `ProfileService.PropertyChanged` (the service still pushes state changes)
   - Failure → shows error inline or via a toast

Notice the data-in path (the profile list updating after apply) still uses the service subscription — the API is only for the command.

### 5. Why this split is the right shape for WinUI 3 / C#

- **`async`/`await` + `Task<T>`** is the idiomatic C# shape for request/response. WinUI 3 UI code is already async-heavy.
- **`IWidgetApi` as a seam** matches how WinUI 3 apps typically separate View from Model — widgets are Views, services are Models, the API is the ViewModel-like glue.
- **Constructor injection** is the MS-recommended pattern (CommunityToolkit.Mvvm leans on it, MS.Extensions.DependencyInjection is the standard).
- **`ApiResult` over exceptions** — WinUI 3 apps shouldn't throw across the UI thread boundary for expected failures. A result type is cleaner and forces the widget to handle failure.
- **Push for metrics, pull for commands** — matches how the underlying backend IPC already works (streaming metrics, RPC for commands).

### 6. What about full MVVM with ViewModels?

**Not introducing it as part of this framework.** Reasons:

- Widgets are small and stateful in the view (animations, visual states). A ViewModel layer adds files without reducing complexity.
- The current code-behind pattern works for every existing widget.
- `{x:Bind}` compiled bindings work fine directly against the code-behind.
- If a specific widget grows complex (e.g. `AdaptiveWidget`), *that* widget can adopt a ViewModel internally — doesn't need to be the framework-wide pattern.

The `IWidgetApi` gives you most of the testability benefit of MVVM without the ceremony.

### 7. Summary

| Flow | Mechanism | Why |
|---|---|---|
| Metrics / hardware state (high-frequency) | Service `PropertyChanged` subscription | Push-based, low latency, matches backend streaming |
| On-demand queries ("get profiles") | `IWidgetApi` async method | Request/response, uniform error handling |
| Commands ("apply profile", "set TDP") | `IWidgetApi` async method returning `ApiResult` | Centralizes validation, confirmation, logging, IPC |
| Widget lifecycle / environment | `WidgetBase` virtual methods | Automatic, no per-widget boilerplate |

## Widget Framework

The widget framework provides a consistent structure for building home page widgets. All widgets inherit from `WidgetBase` and follow the same patterns for data flow, command handling, and lifecycle management.

### Core Components

| Component | File | Purpose |
|---|---|---|
| `IWidget` | `WidgetFramework\IWidget.cs` | Interface contract for all widgets |
| `WidgetBase` | `WidgetFramework\WidgetBase.cs` | Abstract base class providing boilerplate |
| `IWidgetApi` | `WidgetFramework\IWidgetApi.cs` | Command seam for hardware/profile operations |
| `WidgetApi` | `WidgetFramework\WidgetApi.cs` | Concrete implementation of `IWidgetApi` |
| `ApiResult` | `WidgetFramework\ApiResult.cs` | Result types for widget API operations |
| `WidgetStyles.xaml` | `Styles\WidgetStyles.xaml` | Shared styling resources |

### IWidget Interface

All widgets implement `IWidget`:

```csharp
public interface IWidget
{
    string Id { get; }
    void Refresh();
    void OnColumnsChanged(int columns);
    void OnWidgetResized(Size size);
}
```

- `Id` — unique widget identifier (matches factory registration)
- `Refresh()` — called when underlying data changes; update UI from services
- `OnColumnsChanged(int columns)` — called when grid column count changes; adapt layout if needed
- `OnWidgetResized(Size size)` — called when widget's actual rendered size changes

### WidgetBase Abstract Class

`WidgetBase` implements `IWidget` and provides:

**Automatic features:**
- Subscribes to `WidgetService.Columns` changes and calls `OnColumnsChanged`
- Handles `Loaded`/`Unloaded` lifecycle to prevent memory leaks
- Provides `ScheduleRefresh()` helper for thread-safe UI updates via `DispatcherQueue`

**Protected members:**
- `Api` (`IWidgetApi`) — for executing commands (apply profile, set charge limit, etc.)
- `Metrics` (`MetricsService`) — for high-frequency push data (CPU temp, GPU load, etc.)
- `Profiles` (`ProfileService`) — for profile list, active profile, adaptive state
- `WidgetService` — for layout configuration

**Virtual methods to override:**
- `Refresh()` — update UI from data sources (default: no-op)
- `OnColumnsChanged(int columns)` — adapt layout to new column count (default: no-op)
- `OnWidgetResized(Size size)` — respond to size changes (default: no-op)
- `OnLoaded()` — custom initialization when widget loads (default: no-op)
- `OnUnloaded()` — custom cleanup when widget unloads (default: no-op)

### IWidgetApi Command Seam

`IWidgetApi` is the single seam between widgets and the rest of the app for commands:

```csharp
public interface IWidgetApi
{
    Task<ApiResult> ApplyProfileAsync(string profileId);
    Task<ApiResult> SetChargeLimitAsync(int percent);
    Task<ApiResult> SetPowerModeAsync(string mode);
    // ... other commands
}
```

**Usage pattern:**
```csharp
var result = await Api.ApplyProfileAsync(profileId);
if (!result.IsSuccess)
{
    // Handle error — show toast, log, etc.
}
```

**What `WidgetApi` does:**
- Validates inputs
- Shows confirmation dialogs if needed
- Calls appropriate services / backend IPC
- Catches exceptions and maps to `ApiResult`
- Logs actions

Widgets never see exceptions or IPC details.

### ApiResult Types

```csharp
public readonly record struct ApiResult
{
    public bool IsSuccess { get; init; }
    public string? ErrorMessage { get; init; }
    public ApiErrorCode ErrorCode { get; init; }
}

public readonly record struct ApiResult<T>
{
    public bool IsSuccess { get; init; }
    public T? Value { get; init; }
    public string? ErrorMessage { get; init; }
    public ApiErrorCode ErrorCode { get; init; }
}
```

**Error codes:** `None`, `BackendError`, `InvalidInput`, `Cancelled`, `Disconnected`, `Timeout`, `NotSupported`

### WidgetStyles.xaml

Shared styles for consistent widget appearance:

| Style Key | Based On | Usage |
|---|---|---|
| `WidgetTitleStyle` | `CaptionTextBlockStyle` + secondary foreground | Widget labels (e.g. "CPU", "GPU") |
| `WidgetValueStyle` | `SubtitleTextBlockStyle` | Primary values (e.g. temperatures, percentages) |
| `WidgetLabelStyle` | `BodyTextBlockStyle` | Secondary labels and descriptions |
| `WidgetRootPanelStyle` | `StackPanel` with `Spacing=4`, `Padding=10` | Root panel for simple widgets |

**Usage in XAML:**
```xml
<StackPanel Style="{StaticResource WidgetRootPanelStyle}">
    <TextBlock Text="CPU" Style="{StaticResource WidgetTitleStyle}" />
    <TextBlock x:Name="TempText" Style="{StaticResource WidgetValueStyle}" />
</StackPanel>
```

### Adding a New Widget

**Step-by-step recipe:**

1. **Create widget files:**
   ```
   Widgets\NewWidget.xaml
   Widgets\NewWidget.xaml.cs
   ```

2. **XAML structure:**
   ```xml
   <?xml version="1.0" encoding="utf-8"?>
   <framework:WidgetBase x:Class="XmaX.Widgets.NewWidget"
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:framework="using:XmaX.WidgetFramework"
                mc:Ignorable="d">
       
       <StackPanel Style="{StaticResource WidgetRootPanelStyle}">
           <!-- Widget content -->
       </StackPanel>
   
   </framework:WidgetBase>
   ```

3. **Code-behind structure:**
   ```csharp
   using XmaX.Services;
   using XmaX.WidgetFramework;
   
   namespace XmaX.Widgets;
   
   public sealed partial class NewWidget : WidgetBase
   {
       public NewWidget(IWidgetApi api) : base(api, "new_widget")
       {
           this.InitializeComponent();
           
           // Subscribe to service updates
           Metrics.PropertyChanged += OnMetricsChanged;
           Refresh();
       }
   
       private void OnMetricsChanged(object? sender, PropertyChangedEventArgs e)
       {
           if (e.PropertyName == nameof(MetricsService.Metrics))
           {
               ScheduleRefresh();
           }
       }
   
       public override void Refresh()
       {
           // Update UI from Metrics.Metrics
       }
   
       public override void OnColumnsChanged(int columns)
       {
           // Optional: adapt layout to column count
           ScheduleRefresh();
       }
   }
   ```

4. **Register in factory:**
   
   In `HomeWidgetFactory.cs`:
   ```csharp
   // Add to DefaultOrder array
   public static readonly string[] DefaultOrder = { ..., "new_widget" };
   
   // Add to Defaults dictionary
   ["new_widget"] = (1, 1, false),  // (colSpan, rowSpan, alwaysFillRow)
   
   // Add to CreateContent switch
   "new_widget" => new NewWidget(api!),
   ```

5. **Test:**
   - Build and run the app
   - Verify widget appears on home page
   - Test data updates (if metrics-based)
   - Test commands (if using `Api`)
   - Test responsive layout (resize window, change column count)

**Reference implementations:**
- Simple metric widget: `CpuTile.xaml.cs`
- Widget with commands: `ChargeLimitWidget.xaml.cs`
- Widget with responsive layout: `ProfilesWidget.xaml.cs`
