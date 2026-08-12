# Home Page Widget Specification

## Overview

The home page displays widgets in a configurable grid layout. Widgets can span 1 to N columns (where N is the global column count setting, 3-4). Each widget has a defined background style based on whether it's a visual container or an interactive card.

**Widget Height**: All widgets have the same height = `page_width / columns`.

## Widget Types

### Background Styles

| Style | Usage | Appearance |
|-------|-------|------------|
| **Transparent** | Widgets that *contain* interactive elements (buttons, sliders) but are not themselves clickable | No background, just content |
| **Card** | Widgets that *are* the interactive element (clickable/toggleable card) | `CardBackgroundFillColorDefaultBrush`, `CornerRadius="8"`, `Padding="10"` |

### Column Span Configuration

Each widget has min/max column configuration:
- `min=1, max=1`: Fixed 1×1 cell
- `min=1, max=?`: Flexible, 1 to global column count
- `min=2, max=3`: Can be 2 or 3 columns wide
- `alwaysFillRow=true`: Always spans the full row width (ignores MaxColumns)

## Widget Registry

### Individual Metric Tiles (Transparent, 1×1)

| Widget ID | Min | Max | Background | Content |
|-----------|-----|-----|------------|---------|
| `cpu` | 1 | 1 | Transparent | CPU temp, util, power |
| `gpu` | 1 | 1 | Transparent | GPU temp, util, power |
| `ram` | 1 | 1 | Transparent | RAM usage, load % |
| `vram` | 1 | 1 | Transparent | VRAM usage |
| `power` | 1 | 1 | Transparent | Power state, TDP limit |

### Container Widgets (Transparent)

| Widget ID | Min | Max | Background | Content |
|-----------|-----|-----|------------|---------|
| `profiles` | 1 | 4 | Transparent | User profile cards (grid layout) |
| `adaptive` | 2 | 3 | Transparent | Adaptive preset cards (grid layout) |

### Interactive Card Widget (Card Background)

| Widget ID | Min | Max | Background | Content |
|-----------|-----|-----|------------|---------|
| `charge_limit` | 1 | 1 | Card | Toggle button (entire widget is clickable) |

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
- `minColumns=1, maxColumns=4`
- Transparent background

**Layout:**
- Grid with columns matching `min(Config.MaxColumns, homePageColumns)`
- Each card occupies 1 column width
- Cards wrap to next row if more profiles than columns
- Cards stretch to fill available height

### AdaptiveWidget

Container widget that displays tuning preset cards (silent, default, performance) in a grid layout.

**Configuration:**
- `minColumns=2, maxColumns=3`
- Transparent background

**Layout:**
- Grid with columns matching `min(Config.MaxColumns, homePageColumns)`
- 3 preset cards: Silent (60°C), Default (80°C), Performance (95°C)
- Cards wrap to next row if needed
- Cards stretch to fill available height

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

┌─────────────────────────────────────────────────────────────┐
│ PowerTile (1×1, transparent)                                │
│ ┌─────────────────────────────────────────────────────────┐ │
│ │  POWER                                                  │ │
│ │  DC-In                                                  │ │
│ │  55W                                                    │ │
│ └─────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────┘
```

### ProfilesWidget

```
┌─────────────────────────────────────────────────────────────┐
│ ProfilesWidget (transparent, spans min(max, homeColumns))   │
│ ┌──────────────┐ ┌──────────────┐ ┌──────────────┐         │
│ │ ProfileCard  │ │ ProfileCard  │ │ ProfileCard  │         │
│ │  Balanced    │ │  Performance │ │    Silent    │         │
│ │   [active]   │ │              │ │              │         │
│ │  28W · auto  │ │  45W · curve1│ │  15W         │         │
│ └──────────────┘ └──────────────┘ └──────────────┘         │
│ ┌──────────────┐ ┌──────────────┐                          │
│ │ ProfileCard  │ │ ProfileCard  │                          │
│ │   Custom 1   │ │   Custom 2   │                          │
│ │              │ │              │                          │
│ └──────────────┘ └──────────────┘                          │
└─────────────────────────────────────────────────────────────┘
```

### AdaptiveWidget

```
┌─────────────────────────────────────────────────────────────┐
│ AdaptiveWidget (transparent, spans min(3, homeColumns))     │
│ ┌──────────────────────┐ ┌──────────────────────┐           │
│ │   ProfileCard        │ │   ProfileCard        │           │
│ │   Silent             │ │   Default            │           │
│ │      [active]        │ │                      │           │
│ │   60°C               │ │   80°C               │           │
│ └──────────────────────┘ └──────────────────────┘           │
│ ┌──────────────────────┐                                    │
│ │   ProfileCard        │                                    │
│ │   Performance        │                                    │
│ │                      │                                    │
│ │   95°C               │                                    │
│ └──────────────────────┘                                    │
└─────────────────────────────────────────────────────────────┘
```

### ChargeLimitWidget

```
┌─────────────────────────────────────────────────────────────┐
│ ChargeLimitWidget (Card background, 1×1)                    │
│ ┌─────────────────────────────────────────────────────────┐ │
│ │  Charge Limit                                           │ │
│ │  80%                                                    │ │
│ │  Tap to cycle                                           │ │
│ └─────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────┘
```

## Layout Algorithm

1. Widgets are placed left-to-right, top-to-bottom
2. Each widget spans its configured column count (within min/max bounds)
3. If a widget doesn't fit in the current row, wrap to next row
4. Widget column spans must be ≤ global column setting (3-4)
5. All widgets have the same height = `page_width / columns`

## Widget Registration Order

Default registration order (users can reorder):
```
cpu, gpu, ram, vram, profiles, adaptive, power, charge_limit
```

## Implementation Notes

### Widget Interface

```csharp
public interface IHomeWidget
{
    string WidgetId { get; }
    WidgetConfig Config { get; }
    object Control { get; }
}

public class WidgetConfig
{
    public int MinColumns { get; }
    public int MaxColumns { get; }
    public bool IsInteractiveCard { get; }  // Determines background style
    public bool AlwaysFillRow { get; }       // If true, always span full row width
    
    // Presets
    public static WidgetConfig TransparentTile => new(1, 1, false);
    public static WidgetConfig CardTile => new(1, 1, true);
    public static WidgetConfig FlexibleTransparent(int min, int max, bool alwaysFillRow = false);
    public static WidgetConfig FixedTransparent(int min, int max, bool alwaysFillRow = false);
}
```

### Background Application

```csharp
// In HomePage.BuildGrid()
if (widget.Config.IsInteractiveCard)
{
    // Card background
    container.Background = CardBackgroundFillColorDefaultBrush;
    container.CornerRadius = new CornerRadius(8);
    container.Padding = new Thickness(10);
}
else
{
    // Transparent: no container, just the content
    container = content;
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

### ProfileCard Grid Layout

```csharp
// In ProfilesWidget/AdaptiveWidget
var columns = Math.Min(Config.MaxColumns, _widgetService.Columns);

// Create grid columns
for (int c = 0; c < columns; c++)
{
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
}

// Place cards in grid
for (int i = 0; i < profiles.Count; i++)
{
    var row = i / columns;
    var col = i % columns;
    Grid.SetRow(card, row);
    Grid.SetColumn(card, col);
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
```

### Open Issues

1. **Power TDP Source**: The PowerWidget currently shows `AutoTuneService.EffectiveTdpMaxW`, but this may not reflect the power state's configured TDP limit. Options:
   - Backend includes power state TDP in metrics
   - Frontend loads config and looks up TDP for current power mode
   - Backend sends power state assignments separately
