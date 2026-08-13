# Home Page Widget Specification

## Overview

The home page displays widgets in a configurable grid layout. Widgets can span multiple columns and rows based on their configuration and content. The layout system allocates space for widgets, while widgets manage their own visual structure (titles, backgrounds, internal layouts).

**Key Principles:**
- **Layout system allocates space** - tells widgets how much space they have
- **Widgets own their visual structure** - titles, backgrounds, internal layouts are managed by widgets
- **Uniform row height** - all rows have height = `page_width / columns`
- **Flexible multi-row support** - widgets can span multiple rows based on content

## Widget Types

### Background Styles

| Style | Usage | Appearance |
|-------|-------|------------|
| **Transparent** | Widgets that *contain* interactive elements but are not themselves clickable | No background added by layout; widget manages its own appearance |
| **Card** | Widgets that *are* the interactive element (clickable/toggleable card) | Widget handles its own card background internally |

**Note:** The layout system no longer wraps widgets in containers. Each widget is responsible for its own visual appearance.

### Column Span Configuration

Each widget has min/max column configuration:
- `min=1, max=1`: Fixed 1 column wide
- `min=1, max=?`: Flexible, 1 to global column count
- `min=2, max=3`: Can be 2 or 3 columns wide
- `alwaysFillRow=true`: Always spans the full row width (ignores MaxColumns)

### Row Span Configuration

Widgets can span multiple rows:
- `Rows`: Fixed number of rows (default 1)
- `AutoExpandRows`: If true, widget can expand beyond base row count based on content (requires `AlwaysFillRow=true`)
- `GetRequiredRows(availableColumns)`: Widget calculates how many content rows it needs

**Title Support:**
- Widgets can have a `Title` property (string?)
- If title is present, layout allocates an extra row with `TitleHeight` (24px) for the title
- Widgets render their own titles internally (not added by layout system)
- Total row span = content rows + (hasTitle ? 1 : 0)

## Widget Registry

### Individual Metric Tiles (Transparent, 1×1)

| Widget ID | Min | Max | AlwaysFillRow | Title | Content |
|-----------|-----|-----|---------------|-------|---------|
| `cpu` | 1 | 1 | false | null | CPU temp, util, power |
| `gpu` | 1 | 1 | false | null | GPU temp, util, power |
| `ram` | 1 | 1 | false | null | RAM usage, load % |
| `vram` | 1 | 1 | false | null | VRAM usage |
| `power` | 1 | 1 | false | null | Power state, TDP limit |

### Container Widgets (Transparent)

| Widget ID | Min | Max | AlwaysFillRow | AutoExpandRows | Title | Content |
|-----------|-----|-----|---------------|----------------|-------|---------|
| `profiles` | 1 | 4 | true | true | "Profiles" | User profile cards (grid layout) |
| `adaptive` | 2 | 3 | false | false | "Adaptive" | Adaptive preset cards (grid layout) |

### Interactive Card Widget

| Widget ID | Min | Max | AlwaysFillRow | Title | Content |
|-----------|-----|-----|---------------|-------|---------|
| `charge_limit` | 1 | 1 | false | null | Toggle button (widget handles its own card background) |

## Reusable Components

### ProfileCard

A reusable toggleable card component used by ProfilesWidget and AdaptiveWidget.

**Appearance:**
- Custom card style with `CardBackgroundFillColorDefaultBrush`
- `CornerRadius="8"`
- Toggleable (selected/unselected state with accent border)
- Shows profile name (`BodyStrongTextBlockStyle`) and info (`CaptionTextBlockStyle`)
- Stretches to fill grid cell both horizontally and vertically

**Behavior:**
- `CardTapped` event raised when clicked
- `IsSelected` property for active state highlighting
- Hover effect (opacity change)

### ProfilesWidget

Container widget that displays user profile cards in a grid layout.

**Configuration:**
- `minColumns=1, maxColumns=4, alwaysFillRow=true, autoExpandRows=true`
- Title: "Profiles"
- Transparent background (widget manages its own appearance)

**Layout:**
- Grid with columns matching `alwaysFillRow ? homePageColumns : min(Config.MaxColumns, homePageColumns)`
- Each card occupies 1 column width
- Cards wrap to next row if more profiles than columns
- Cards stretch to fill available height
- `GetRequiredRows()` returns `ceil(profiles.Count / columns)`
- Total row span = content rows + 1 (for title)

### AdaptiveWidget

Container widget that displays tuning preset cards (silent, default, performance) in a grid layout.

**Configuration:**
- `minColumns=2, maxColumns=3, alwaysFillRow=false`
- Title: "Adaptive"
- Transparent background (widget manages its own appearance)

**Layout:**
- Grid with columns matching `min(Config.MaxColumns, homePageColumns)`
- 3 preset cards: Silent (60°C), Default (80°C), Performance (95°C)
- Cards wrap to next row if needed
- Cards stretch to fill available height
- `GetRequiredRows()` returns `Config.Rows` (fixed)
- Total row span = content rows + 1 (for title)

### ChargeLimitWidget

Interactive card widget for cycling charge limits.

**Configuration:**
- `minColumns=1, maxColumns=1, alwaysFillRow=false`
- Title: null
- Widget handles its own card background internally (wrapped in Border in XAML)

**Layout:**
- Fixed 1×1 size
- Entire widget is clickable
- Shows charge limit percentage and "Tap to cycle" hint
- `GetRequiredRows()` returns 1

## Widget Structure Diagrams

### Metric Tiles (CPU, GPU, RAM, VRAM, Power)

```
┌─────────────────────────────────────────────────────────────┐
│ CpuTile (1×1, transparent)                                  │
│ ┌─────────────────────────────────────────────────────────┐ │
│ │  CPU                                                    │ │
│ │  65°C                                                   │ │
│ │  45% util                                               │ │
│ │  25W                                                    │ │
│ └─────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────┘
```

### ProfilesWidget (with title, multi-row)

```
┌─────────────────────────────────────────────────────────────┐
│ ProfilesWidget (transparent, spans full width)              │
│ ┌─────────────────────────────────────────────────────────┐ │
│ │ Profiles                                  [Title row]   │ │ ← 24px
│ └─────────────────────────────────────────────────────────┘ │
│ ┌──────────────┐ ┌──────────────┐ ┌──────────────┐         │
│ │ ProfileCard  │ │ ProfileCard  │ │ ProfileCard  │         │ ← rowHeight
│ │  Balanced    │ │  Performance │ │    Silent    │         │
│ │   [active]   │ │              │ │              │         │
│ │  28W · auto  │ │  45W · curve1│ │  15W         │         │
│ └──────────────┘ └──────────────┘ └──────────────┘         │
│ ┌──────────────┐ ┌──────────────┐                          │
│ │ ProfileCard  │ │ ProfileCard  │                          │ ← rowHeight
│ │   Custom 1   │ │   Custom 2   │                          │
│ │              │ │              │                          │
│ └──────────────┘ └──────────────┘                          │
└─────────────────────────────────────────────────────────────┘
Total height: 24px + (2 × rowHeight)
```

### AdaptiveWidget (with title, multi-row)

```
┌─────────────────────────────────────────────────────────────┐
│ AdaptiveWidget (transparent, spans 2-3 columns)             │
│ ┌─────────────────────────────────────────────────────────┐ │
│ │ Adaptive                                  [Title row]   │ │ ← 24px
│ └─────────────────────────────────────────────────────────┘ │
│ ┌──────────────────────┐ ┌──────────────────────┐           │
│ │   ProfileCard        │ │   ProfileCard        │           │ ← rowHeight
│ │   Silent             │ │   Default            │           │
│ │      [active]        │ │                      │           │
│ │   60°C               │ │   80°C               │           │
│ └──────────────────────┘ └──────────────────────┘           │
│ ┌──────────────────────┐                                    │
│ │   ProfileCard        │                                    │ ← rowHeight
│ │   Performance        │                                    │
│ │                      │                                    │
│ │   95°C               │                                    │
│ └──────────────────────┘                                    │
└─────────────────────────────────────────────────────────────┘
Total height: 24px + (2 × rowHeight)
```

### ChargeLimitWidget

```
┌─────────────────────────────────────────────────────────────┐
│ ChargeLimitWidget (widget handles own card background)      │
│ ┌─────────────────────────────────────────────────────────┐ │
│ │  Charge Limit                                           │ │
│ │  80%                                                    │ │
│ │  Tap to cycle                                           │ │
│ └─────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────┘
```

## Layout Algorithm

### Overview

The layout algorithm places widgets in a grid, tracking occupied cells to support multi-row widgets. Widgets flow left-to-right, top-to-bottom, with multi-row widgets allowing other widgets to flow around them.

### Algorithm Steps

1. **Initialize grid:**
   - Create N column definitions (N = global column setting, 3-4)
   - Calculate standard row height = `gridWidth / columns`
   - Initialize empty set of occupied cells

2. **For each visible widget (in order):**
   - Calculate `columnSpan`:
     - If `AlwaysFillRow`: span = all columns
     - Otherwise: span = `min(MaxColumns, columns)`
   - Calculate `contentRows` = `widget.GetRequiredRows(availableColumns)`
   - Calculate `totalRowSpan` = `contentRows + (hasTitle ? 1 : 0)`
   - Find next available position where widget fits:
     - Start at row 0, column 0
     - Check if all cells in the widget's footprint are free
     - If not, advance to next column; if at end of row, advance to next row
     - Repeat until position found
   - Mark all cells in widget's footprint as occupied
   - Create row definitions:
     - If widget has title: first row = `TitleHeight` (24px), remaining rows = `rowHeight`
     - Otherwise: all rows = `rowHeight`
   - Place widget at position with `Grid.SetRow`, `Grid.SetColumn`, `Grid.SetColumnSpan`, `Grid.SetRowSpan`

### Example Layout (3 columns, 8 widgets)

```
Row 0: [cpu] [gpu] [ram]           ← rowHeight
Row 1: [vram] [charge_limit] [power]  ← rowHeight
Row 2: [profiles title]             ← TitleHeight (24px)
Row 3: [profiles content]           ← rowHeight
Row 4: [profiles content]           ← rowHeight
Row 5: [adaptive title]             ← TitleHeight (24px)
Row 6: [adaptive content]           ← rowHeight
```

## Widget Registration Order

Default registration order (users can reorder):
```
cpu, gpu, ram, vram, profiles, adaptive, charge_limit, power
```

## Implementation Notes

### Widget Interface

```csharp
public interface IHomeWidget
{
    string WidgetId { get; }
    WidgetConfig Config { get; }
    object Control { get; }
    string? Title { get; }  // null = no title
    int GetRequiredRows(int availableColumns);  // Content rows only (excludes title row)
}
```

### WidgetConfig

```csharp
public sealed class WidgetConfig
{
    public const double TitleHeight = 24.0;  // Standard title height in pixels
    
    public int MinColumns { get; }
    public int MaxColumns { get; }
    public bool IsInteractiveCard { get; }  // Deprecated: widgets handle own backgrounds
    public bool AlwaysFillRow { get; }
    public int Rows { get; }  // Fixed row count (default 1)
    public bool AutoExpandRows { get; }  // Requires AlwaysFillRow=true
    
    // Validation: AutoExpandRows=true requires AlwaysFillRow=true
    
    // Presets
    public static WidgetConfig TransparentTile => new(1, 1, false);
    public static WidgetConfig CardTile => new(1, 1, true);  // Deprecated
    public static WidgetConfig FlexibleTransparent(
        int minColumns = 1, int maxColumns = 4, 
        bool alwaysFillRow = false, int rows = 1, bool autoExpandRows = false);
    public static WidgetConfig FixedTransparent(
        int minColumns, int maxColumns, 
        bool alwaysFillRow = false, int rows = 1, bool autoExpandRows = false);
}
```

### Widget Height Calculation

```csharp
// In HomePage
private double CalculateWidgetHeight()
{
    var columns = _widgetService.Columns;
    var gridWidth = WidgetGrid.ActualWidth - WidgetGrid.Padding.Left - WidgetGrid.Padding.Right;
    if (gridWidth <= 0) return 100; // Fallback
    return gridWidth / columns;
}
```

### GetRequiredRows Implementation

```csharp
// Example: ProfilesWidget
public int GetRequiredRows(int availableColumns)
{
    var profiles = _profileService.Profiles;
    if (profiles.Count == 0) return Config.Rows;

    // Calculate columns this widget will actually use
    var columns = Config.AlwaysFillRow
        ? availableColumns
        : Math.Min(Config.MaxColumns, availableColumns);

    // Calculate rows needed for all profiles
    var calculatedRows = (profiles.Count + columns - 1) / columns;

    // If auto-expand is enabled, return max of base rows and calculated rows
    return Config.AutoExpandRows
        ? Math.Max(Config.Rows, calculatedRows)
        : Config.Rows;
}
```

### Title Row Allocation

```csharp
// In HomePage.BuildGrid()
var hasTitle = !string.IsNullOrEmpty(widget.Title);
var totalRowSpan = contentRows + (hasTitle ? 1 : 0);

// Create row definitions with appropriate heights
for (int r = 0; r < totalRowSpan; r++)
{
    var rowIndex = placeRow + r;
    var height = (r == 0 && hasTitle) ? WidgetConfig.TitleHeight : rowHeight;
    EnsureRowDefinition(rowIndex, height);
}
```

### Widget Background Handling

Widgets now handle their own backgrounds internally:

```csharp
// In ChargeLimitWidget.xaml
<Border Background="{ThemeResource CardBackgroundFillColorDefaultBrush}"
        CornerRadius="8"
        Padding="10">
    <!-- Widget content -->
</Border>
```

The layout system no longer wraps widgets in containers. It simply places the widget's `Control` directly into the grid.

### Occupied Cells Tracking

```csharp
// Track occupied cells for multi-row widget layout
var occupiedCells = new HashSet<(int row, int col)>();

// After placing a widget:
for (int dr = 0; dr < totalRowSpan; dr++)
{
    for (int dc = 0; dc < columnSpan; dc++)
    {
        occupiedCells.Add((placeRow + dr, placeCol + dc));
    }
}

// When finding position for next widget:
bool fits = true;
for (int dr = 0; dr < totalRowSpan && fits; dr++)
{
    for (int dc = 0; dc < columnSpan && fits; dc++)
    {
        if (occupiedCells.Contains((placeRow + dr, c + dc)))
        {
            fits = false;
        }
    }
}
```

### Config Loading on Startup

```csharp
// In MainWindow
private async Task InitializeAsync()
{
    await LoadConfigAndUpdateBannerAsync();  // Loads home_layout into WidgetService
    RootFrame.Navigate(typeof(HomePage));    // Then navigate to HomePage
}

// In HomePage.OnNavigatedTo()
_widgetService.SetVisible("power", true);
_widgetService.SetVisible("charge_limit", true);
// Grid will be built when page loads (see OnPageLoaded)

// In HomePage.OnPageLoaded()
BuildGrid();  // Build grid after all widgets are fully loaded
```

## Open Issues

1. **Power TDP Source**: The PowerWidget currently shows `AutoTuneService.EffectiveTdpMaxW`, but this may not reflect the power state's configured TDP limit. Options:
   - Backend includes power state TDP in metrics
   - Frontend loads config and looks up TDP for current power mode
   - Backend sends power state assignments separately

2. **Widget Visibility Defaults**: The system forces `power` and `charge_limit` widgets to be visible on every navigation to the home page. This overrides any user configuration that might hide them. Consider making this configurable or removing the forced visibility.
