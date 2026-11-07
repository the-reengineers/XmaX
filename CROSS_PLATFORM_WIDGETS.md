# Cross-Platform Widget Architecture

This document covers the architecture for supporting widgets across Windows (WinUI 3) and Linux (KDE/GNOME).

## The Challenge

- **Windows:** WinUI 3 (C#/XAML) — already implemented
- **Linux KDE:** Qt/QML (C++/QML)
- **Linux GNOME:** GTK (C/Vala/Rust)

Different UI frameworks, different widget lifecycle models, different styling systems. How do we package widgets so a developer can add a widget with support for Windows and/or Linux?

---

## Recommended Architecture: Hybrid Approach

**Shared contracts + platform-specific implementations**

```
shared/
  contracts/
    IWidget.cs              # C# interface (portable)
    IWidgetApi.cs
    ApiResult.cs
  
  logic/
    MetricsAggregator.cs    # Shared business logic
    ProfileManager.cs
    HardwareCommands.cs

frontend/
  windows/
    Widgets/
      CpuTile.xaml(.cs)     # WinUI 3 implementation
      widget-manifest.json
  
  linux/
    kde/
      Widgets/
        CpuTile.qml         # Qt/QML implementation
        widget-manifest.json
    
    gnome/
      Widgets/
        cpu-tile.vala       # GTK implementation
        widget-manifest.json
```

### Widget Manifest

Each widget should have a manifest describing its capabilities:

```json
{
  "id": "cpu_tile",
  "version": "1.0.0",
  "name": "CPU Monitor",
  "description": "Shows CPU temperature, utilization, and power",
  
  "platforms": {
    "windows": {
      "implementation": "CpuTile.xaml.cs",
      "min_size": [1, 1],
      "max_size": [2, 2]
    },
    "linux-kde": {
      "implementation": "CpuTile.qml",
      "min_size": [1, 1],
      "max_size": [2, 2]
    },
    "linux-gnome": {
      "implementation": "cpu-tile.vala",
      "min_size": [1, 1],
      "max_size": [2, 2]
    }
  },
  
  "data_sources": ["metrics.cpu"],
  "commands": [],
  "refresh_interval_ms": 1000
}
```

### Developer Workflow

- **Windows-only widget:** implement in `frontend/windows/Widgets/`
- **Linux-only widget:** implement in `frontend/linux/kde/Widgets/` or `frontend/linux/gnome/Widgets/`
- **Cross-platform widget:** implement in all relevant platform directories

### Key Principles

1. **Consistent widget IDs** — `cpu_tile` is `cpu_tile` on all platforms
2. **Consistent API contracts** — `IWidgetApi` methods are the same everywhere
3. **Platform detection at runtime** — host app detects platform, loads appropriate widget implementations
4. **Graceful degradation** — if a widget isn't available on a platform, show a placeholder or hide it
5. **Shared test infrastructure** — unit tests for business logic can run on any platform

---

## Language/Framework Options for Shared Code

### Option 1: C/C++ (Native)

Write shared contracts and logic in C or C++, consume directly in each platform.

**Pros:**
- ✅ Universal compatibility — all three UI frameworks have native C/C++ support
- ✅ Maximum performance — no interop overhead
- ✅ Mature ecosystem — decades of libraries, tools, documentation
- ✅ Smallest binary size

**Cons:**
- ❌ Verbose — more boilerplate than modern languages
- ❌ Memory management complexity — manual (C) or smart pointer discipline (C++)
- ❌ Slower development — compile times, debugging complexity
- ❌ Learning curve — pointers, templates, RAII

**Interop Matrix:**
| Platform | Support | Notes |
|---|---|---|
| WinUI 3 (C#) | Good | C++/WinRT bridge, or P/Invoke for C |
| Qt (C++) | Native | Direct consumption |
| GTK (C) | Native | Direct consumption, gtkmm for C++ |

**Best For:** Performance-critical widgets, teams with strong C/C++ expertise

---

### Option 2: C#/.NET (Recommended for Your Case)

Write shared code in C#, run on Windows natively, use .NET 6+ on Linux with FFI for Qt/GTK.

**Pros:**
- ✅ Already used for Windows — no new language to learn
- ✅ Excellent developer experience — modern language features, great tooling
- ✅ .NET 6+ runs on Linux — can execute shared logic directly
- ✅ Strong typing, huge ecosystem, fast development
- ✅ Good performance — JIT compilation, value types

**Cons:**
- ❌ Qt/GTK need interop — can't consume C# directly, need C ABI bridge
- ❌ Runtime dependency — .NET runtime must be installed on Linux
- ❌ Larger binary size — .NET runtime is ~100MB (can be trimmed)
- ❌ FFI complexity — marshaling between C# and C/C++

**Interop Matrix:**
| Platform | Support | Notes |
|---|---|---|
| WinUI 3 (C#) | Native | Direct consumption |
| Qt (C++) | Via FFI | Need C wrapper library |
| GTK (C) | Via FFI | Need C wrapper library |

**Implementation Strategy:**
```
shared/
  contracts/
    IWidget.cs              # C# interface
    IWidgetApi.cs
    ApiResult.cs
  
  logic/
    MetricsAggregator.cs    # Shared business logic
  
  native-bridge/            # C library for FFI
    widget-bridge.h
    widget-bridge.c         # Exports C functions that call C# code

frontend/
  windows/
    Widgets/                # WinUI 3 widgets (C#)
  
  linux/
    kde/
      Widgets/              # Qt widgets (C++)
        # Call into shared C# via native-bridge
    
    gnome/
      Widgets/              # GTK widgets (C/Vala)
        # Call into shared C# via native-bridge
```

**Best For:** Teams already using C#, widgets with significant shared business logic

---

### Option 3: Rust

Write shared code in Rust, generate C bindings, consume in all platforms.

**Pros:**
- ✅ Memory safety — no null pointers, no data races
- ✅ Zero-cost abstractions — high-level features, low-level performance
- ✅ Excellent FFI — designed for interoperability
- ✅ Cross-platform — compiles to Windows, Linux, macOS

**Cons:**
- ❌ Learning curve — ownership, borrowing, lifetimes
- ❌ Smaller ecosystem — fewer libraries than C#/C++
- ❌ Compile times — can be slow for large projects
- ❌ Team expertise — need Rust knowledge

**Interop Matrix:**
| Platform | Support | Notes |
|---|---|---|
| WinUI 3 (C#) | Via FFI | Generate C bindings, P/Invoke |
| Qt (C++) | Via FFI | Generate C bindings |
| GTK (C) | Via FFI | Generate C bindings, or use gtk-rs |

**Best For:** Performance-critical + safety-critical code, teams willing to invest in Rust

---

### Option 4: Web Technologies (TypeScript/JavaScript)

Write widgets as web components, embed in native shells via WebView.

**Pros:**
- ✅ Single codebase — write once, run everywhere
- ✅ Huge ecosystem — npm, React/Vue/Svelte
- ✅ Fast iteration — hot reload
- ✅ Cross-platform

**Cons:**
- ❌ Performance overhead — WebView is heavier than native
- ❌ Doesn't feel native — web UI looks different
- ❌ Large bundle size — Chromium/WebKit is ~100MB+
- ❌ Limited system access — need native bridge

**Interop Matrix:**
| Platform | Support | Notes |
|---|---|---|
| WinUI 3 | WebView2 | Microsoft's Edge WebView |
| Qt | Qt WebEngine | Chromium-based |
| GTK | WebKitGTK | Used by Epiphany browser |

**Best For:** Widgets with complex UI, teams with web expertise

---

### Option 5: Cross-Platform UI Framework (Avalonia/Flutter)

Use a single cross-platform framework for all widgets.

**Avalonia (.NET):**
- WPF-like XAML, runs on Windows/Linux/macOS
- Pros: C# ecosystem, familiar to WinUI developers
- Cons: not native widgets, smaller community

**Flutter (Dart):**
- Google's framework, renders to Skia canvas
- Pros: beautiful UI, hot reload, single codebase
- Cons: Dart language, not native widgets, large runtime

**Pros (Both):**
- ✅ Single codebase
- ✅ Consistent look
- ✅ Fast development

**Cons (Both):**
- ❌ Not native — custom rendering
- ❌ Performance — not as fast as native
- ❌ Platform integration — need plugins

**Best For:** Consistent cross-platform look is priority

---

### Comparison Matrix

| Criterion | C/C++ | C# | Rust | Web | Cross-Platform UI |
|---|---|---|---|---|---|
| **Windows interop** | ✅ Native | ✅ Native | ⚠️ FFI | ⚠️ WebView | ⚠️ Framework |
| **Qt interop** | ✅ Native | ⚠️ FFI | ⚠️ FFI | ⚠️ WebEngine | ⚠️ Framework |
| **GTK interop** | ✅ Native | ⚠️ FFI | ⚠️ FFI | ⚠️ WebKitGTK | ⚠️ Framework |
| **Dev speed** | ⚠️ Slow | ✅ Fast | ⚠️ Medium | ✅ Fast | ✅ Fast |
| **Performance** | ✅ Best | ✅ Good | ✅ Best | ⚠️ OK | ⚠️ OK |
| **Binary size** | ✅ Small | ⚠️ Large | ✅ Small | ❌ Huge | ⚠️ Large |
| **Memory safety** | ⚠️ Manual | ✅ GC | ✅ Compile | ✅ GC | ✅ GC |
| **Learning curve** | ⚠️ Steep | ✅ Easy | ❌ Steep | ✅ Easy | ⚠️ Medium |
| **Team expertise** | ⚠️ Varies | ✅ You have it | ❌ Unlikely | ✅ Likely | ❌ New |

---

## Avalonia UI Approach

### What is Avalonia?

Avalonia is a **cross-platform .NET UI framework** similar to WPF:
- Uses XAML (like WinUI 3)
- Runs on Windows, Linux, macOS
- Your existing C# skills transfer directly
- Self-contained deployment

### Recommended: Avalonia for Linux Only

Keep WinUI 3 on Windows, use Avalonia on Linux.

```
shared/
  WidgetFramework/          # Shared C# code
    IWidget.cs              # Interface (already have this)
    IWidgetApi.cs           # Command seam (already have this)
    ApiResult.cs            # Result types (already have this)
    WidgetBase.cs           # Abstract base (adapt for Avalonia)
  
  logic/
    MetricsAggregator.cs    # Shared business logic

frontend/
  windows/
    WidgetFramework/
      WidgetBase.cs         # WinUI 3 specific
    Widgets/
      CpuTile.xaml(.cs)     # WinUI 3 widgets
  
  linux/
    WidgetFramework/
      WidgetBase.cs         # Avalonia specific
    Widgets/
      CpuTile.axaml(.cs)    # Avalonia widgets
    App.axaml               # Avalonia app shell
```

### Widget Example Comparison

**WinUI 3 (Windows):**
```xml
<UserControl x:Class="XmaX.Widgets.CpuTile">
    <StackPanel Style="{StaticResource WidgetRootPanelStyle}">
        <TextBlock Text="CPU" Style="{StaticResource WidgetTitleStyle}" />
        <TextBlock x:Name="TempText" Style="{StaticResource WidgetValueStyle}" />
    </StackPanel>
</UserControl>
```

**Avalonia (Linux):**
```xml
<UserControl xmlns="https://github.com/avaloniaui"
             x:Class="XmaX.Linux.Widgets.CpuTile">
    <StackPanel Classes="WidgetRootPanel">
        <TextBlock Text="CPU" Classes="WidgetTitle" />
        <TextBlock Name="TempText" Classes="WidgetValue" />
    </StackPanel>
</UserControl>
```

**Key differences:**
- XML namespace (`https://github.com/avaloniaui`)
- Styles use `Classes` instead of `Style="{StaticResource ...}"`
- `Name` instead of `x:Name`
- Code-behind is nearly identical (C# is C#)

### Shared Code Example

**Shared interface (works on both):**
```csharp
// shared/WidgetFramework/IWidget.cs
public interface IWidget
{
    string Id { get; }
    void Refresh();
    void OnColumnsChanged(int columns);
}
```

**WinUI 3 base class:**
```csharp
// frontend/windows/WidgetFramework/WidgetBase.cs
public abstract class WidgetBase : UserControl, IWidget
{
    protected IWidgetApi Api { get; }
    protected MetricsService Metrics { get; }
    
    protected WidgetBase(IWidgetApi api, string id)
    {
        Api = api;
        Id = id;
        Metrics = App.MetricsService;
        // WinUI 3 specific setup
    }
}
```

**Avalonia base class:**
```csharp
// frontend/linux/WidgetFramework/WidgetBase.cs
public abstract class WidgetBase : UserControl, IWidget
{
    protected IWidgetApi Api { get; }
    protected MetricsService Metrics { get; }
    
    protected WidgetBase(IWidgetApi api, string id)
    {
        Api = api;
        Id = id;
        Metrics = LinuxApp.MetricsService;
        // Avalonia specific setup
    }
}
```

**Widget implementation (nearly identical):**
```csharp
// WinUI 3
public sealed partial class CpuTile : WidgetBase
{
    public CpuTile(IWidgetApi api) : base(api, "cpu")
    {
        InitializeComponent();
        Metrics.PropertyChanged += OnMetricsChanged;
        Refresh();
    }
    
    public override void Refresh()
    {
        var cpu = Metrics.Metrics.Cpu;
        TempText.Text = cpu.TempC.HasValue ? $"{cpu.TempC}°C" : "N/A";
    }
}

// Avalonia
public partial class CpuTile : WidgetBase
{
    public CpuTile(IWidgetApi api) : base(api, "cpu")
    {
        InitializeComponent();
        Metrics.PropertyChanged += OnMetricsChanged;
        Refresh();
    }
    
    public override void Refresh()
    {
        var cpu = Metrics.Metrics.Cpu;
        TempText.Text = cpu.TempC.HasValue ? $"{cpu.TempC}°C" : "N/A";
    }
}
```

**The C# code is 95% identical!** Only the XAML differs.

### Avalonia Linux Integration

**KDE (Qt) Integration:**
- ✅ DBus integration (KDE menus, notifications)
- ✅ X11/Wayland support
- ✅ Native file dialogs
- ✅ System tray support

**GNOME (GTK) Integration:**
- ✅ GTK file picker integration
- ✅ Freedesktop notifications
- ✅ HiDPI support
- ✅ Wayland native

### Pros and Cons of Avalonia

**Pros:**
- ✅ Leverage existing C# skills
- ✅ Share contracts and logic (C#)
- ✅ Mature framework — Avalonia 11 is stable
- ✅ Good Linux integration (KDE/GNOME)
- ✅ Self-contained deployment
- ✅ Similar to WinUI 3 — XAML-based

**Cons:**
- ⚠️ Two UI codebases (WinUI 3 + Avalonia XAML)
- ⚠️ Styling differences — need to maintain two style systems
- ⚠️ Not 100% native feel — Avalonia has its own rendering
- ⚠️ Smaller ecosystem — fewer third-party controls

---

## Avalonia Native Theming

### How Avalonia Theming Works

**Avalonia doesn't use native controls.** It renders everything itself using Skia (2D graphics library), then applies **themes** to mimic the native look. It's more like "native-inspired" than truly native.

```
Your XAML (AXAML)
    ↓
Avalonia Layout System
    ↓
Skia Renderer (draws pixels)
    ↓
Theme styles applied
```

### Theme System

Avalonia uses **theme packages** that define styles for all controls:

**Fluent Theme** (Windows 11 style):
```xml
<Application.Styles>
    <FluentTheme />
</Application.Styles>
```

**Simple Theme** (basic controls):
```xml
<Application.Styles>
    <SimpleTheme />
</Application.Styles>
```

**Community Themes:** Material Design, Dracula, Nord, etc.

### System Theme Detection

Avalonia can **detect** system preferences and adapt:

**Light/Dark Mode:**
```csharp
private ThemeVariant DetectSystemTheme()
{
    // Windows
    if (OperatingSystem.IsWindows())
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        var value = key?.GetValue("AppsUseLightTheme");
        return value is int i && i == 0 ? ThemeVariant.Dark : ThemeVariant.Light;
    }
    
    // Linux (GNOME/KDE)
    if (OperatingSystem.IsLinux())
    {
        return DetectLinuxTheme();
    }
    
    return ThemeVariant.Light;
}
```

### What Avalonia CAN Do

✅ **Detect system preferences:**
- Light/dark mode
- Accent color (Windows)
- Font family
- DPI/scaling

✅ **Use native dialogs:**
- File open/save
- Folder picker
- Message boxes

✅ **Integrate with system:**
- System tray
- Notifications
- DBus (Linux)

### What Avalonia CANNOT Do

❌ **Match exact native appearance:**
- Controls are drawn by Avalonia, not the OS
- Spacing, borders, animations differ
- Won't update when system theme changes (unless you handle it)

❌ **Use native widget toolkit:**
- Not GTK on GNOME
- Not Qt on KDE
- Not WinUI on Windows

---

## Matching Native Themes with C# + FFI

### The Core Challenge

FFI alone doesn't give you theming. To match native themes, you need to:
1. **Query the theme** — get colors, fonts, spacing from KDE/GTK
2. **Apply the theme** — render your widgets using those values
3. **Update dynamically** — react when user changes theme

### Option 1: Query Theme APIs + Apply Manually (Most Practical)

Use FFI to call native theme APIs, extract values, apply to your C# widgets.

**KDE (Qt) Theme Extraction:**
```csharp
public class KdeThemeExtractor
{
    [DllImport("libQt6Gui.so.6")]
    private static extern IntPtr QApplication_palette();
    
    [DllImport("libQt6Gui.so.6")]
    private static extern void QPalette_color(
        IntPtr palette, int colorGroup, int colorRole, 
        out byte r, out byte g, out byte b);

    public KdeTheme ExtractTheme()
    {
        var palette = QApplication_palette();
        // Extract colors from Qt palette
        // Extract font from QFont
        return new KdeTheme { /* ... */ };
    }
}
```

**GNOME (GTK) Theme Extraction:**
```csharp
public class GtkThemeExtractor
{
    [DllImport("libgtk-4.so.0")]
    private static extern IntPtr gtk_style_context_new();
    
    [DllImport("libgtk-4.so.0")]
    private static extern void gtk_style_context_get_background_color(
        IntPtr context, out float red, out float green, out float blue, out float alpha);

    public GtkTheme ExtractTheme()
    {
        var context = gtk_style_context_new();
        // Extract colors from GTK style context
        // Extract font from GTK settings
        return new GtkTheme { /* ... */ };
    }
}
```

**Apply Theme to Widgets:**
```csharp
public abstract class ThemedWidgetBase : UserControl
{
    protected IThemeExtractor ThemeExtractor { get; }
    
    protected ThemedWidgetBase(IThemeExtractor extractor)
    {
        ThemeExtractor = extractor;
        ApplyTheme();
        ThemeExtractor.ThemeChanged += OnThemeChanged;
    }
    
    protected virtual void ApplyTheme()
    {
        var theme = ThemeExtractor.ExtractTheme();
        Resources["WindowBackground"] = theme.WindowBackground;
        Resources["WindowText"] = theme.WindowText;
        // ... apply other theme values
    }
}
```

**Pros:**
- ✅ Works with any C# UI framework
- ✅ Follows system theme changes
- ✅ Gets real theme values from KDE/GTK

**Cons:**
- ❌ Huge FFI effort — need to wrap hundreds of Qt/GTK APIs
- ❌ Never pixel-perfect — you get colors/fonts but not exact spacing, borders, animations
- ❌ Maintenance burden — Qt/GTK APIs change between versions

---

### Option 2: Embed Native Widgets via FFI (Perfect Match, Huge Complexity)

Create actual Qt/GTK widgets and embed them in your C# app.

```
C# Widget Host (WinUI/Avalonia)
└── Native Widget Container
    └── Actual Qt Widget (KDE) or GTK Widget (GNOME)
```

**KDE Implementation:**
```csharp
public class NativeKdeWidget : IDisposable
{
    [DllImport("libQt6Widgets.so.6")]
    private static extern IntPtr QPushButton_new(IntPtr parent);
    
    [DllImport("libQt6Widgets.so.6")]
    private static extern void QPushButton_setText(IntPtr button, IntPtr text);
    
    private IntPtr _qtWidget;
    
    public NativeKdeWidget()
    {
        _qtWidget = QPushButton_new(IntPtr.Zero);
    }
    
    public IntPtr GetNativeHandle()
    {
        // Get X11/Wayland window ID for embedding
        return QWidget_winId(_qtWidget);
    }
}
```

**Pros:**
- ✅ Pixel-perfect native look — actual Qt/GTK widgets
- ✅ Automatic theme updates
- ✅ All native behaviors

**Cons:**
- ❌ Extremely complex — need to wrap entire Qt/GTK API
- ❌ Window embedding issues — X11/Wayland embedding is painful
- ❌ Event handling — need to forward events between C# and native
- ❌ Memory management — two object hierarchies

---

### Option 3: Hybrid — Native Shell, C# Content (Recommended Compromise)

Use native toolkit for widget shell, C# for content/logic.

```
Native Qt/GTK Widget Shell
├── Handles theming
├── Handles native look/feel
└── C# Content Layer
    ├── Widget logic
    ├── Data binding
    └── Business logic
```

**KDE Example:**
```cpp
// Linux/Kde/Widgets/cpu_tile_widget.cpp
CpuTileWidget::CpuTileWidget(QWidget* parent)
    : QWidget(parent)
{
    auto layout = new QVBoxLayout(this);
    
    // Qt widgets automatically use KDE theme
    tempLabel = new QLabel("N/A", this);
    utilLabel = new QLabel("N/A", this);
    powerLabel = new QLabel("N/A", this);
    
    layout->addWidget(tempLabel);
    layout->addWidget(utilLabel);
    layout->addWidget(powerLabel);
}

void CpuTileWidget::UpdateDisplay(float tempC, float utilPercent, float powerW)
{
    tempLabel->setText(QString("%1°C").arg(tempC));
    utilLabel->setText(QString("%1%").arg(utilPercent));
    powerLabel->setText(QString("%1W").arg(powerW));
}
```

**C# Bridge:**
```csharp
public class CpuTileBridge : IDisposable
{
    [DllImport("libcputilewidget.so")]
    private static extern IntPtr cputilewidget_create(IntPtr parent);
    
    [DllImport("libcputilewidget.so")]
    private static extern void cputilewidget_update_display(
        IntPtr widget, float tempC, float utilPercent, float powerW);
    
    private IntPtr _nativeWidget;
    
    public CpuTileBridge()
    {
        _nativeWidget = cputilewidget_create(IntPtr.Zero);
    }
    
    public void UpdateDisplay(float tempC, float utilPercent, float powerW)
    {
        cputilewidget_update_display(_nativeWidget, tempC, utilPercent, powerW);
    }
}
```

**C# Widget Logic:**
```csharp
public class CpuTileLogic
{
    private readonly MetricsService _metrics;
    
    public CpuTileLogic(MetricsService metrics)
    {
        _metrics = metrics;
        _metrics.PropertyChanged += OnMetricsChanged;
    }
    
    private void OnMetricsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MetricsService.Metrics))
        {
            var cpu = _metrics.Metrics.Cpu;
            UpdateDisplay?.Invoke(cpu.TempC ?? 0, cpu.UtilPercent, cpu.PackageWatts ?? 0);
        }
    }
    
    public event Action<float, float, float>? UpdateDisplay;
}
```

**Pros:**
- ✅ Native look — actual Qt/GTK widgets
- ✅ Shared logic — C# business logic reused
- ✅ Automatic theming — native widgets handle it
- ✅ Manageable scope — only wrap what you need

**Cons:**
- ⚠️ Still complex — need C++ for KDE, C/Vala for GNOME
- ⚠️ Two codebases — native shell + C# logic
- ⚠️ Event forwarding — need to handle click events, etc.
- ⚠️ Build complexity — need to compile native libraries

---

### Option 4: Theme Extraction Library (Most Realistic)

Use existing libraries that extract theme information.

**For KDE:**
```csharp
public class KdeThemeReader
{
    [DllImport("libKF5ConfigCore.so.5")]
    private static extern IntPtr KSharedConfig_openConfig(IntPtr name);
    
    public string GetKdeThemeName()
    {
        var config = KSharedConfig_openConfig(IntPtr.Zero);
        // Read theme name from KDE config
        return "breeze";
    }
}
```

**For GNOME:**
```csharp
public class GtkThemeReader
{
    public string GetGtkThemeName()
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "gsettings",
            Arguments = "get org.gnome.desktop.interface gtk-theme",
            RedirectStandardOutput = true
        });
        
        return process.StandardOutput.ReadToEnd().Trim().Trim('\'');
    }
}
```

**Map theme names to predefined styles:**
```csharp
public static class ThemeMappings
{
    public static WidgetStyle GetStyleForGtkTheme(string themeName)
    {
        return themeName switch
        {
            "Adwaita" => AdwaitaLightStyle,
            "Adwaita-dark" => AdwaitaDarkStyle,
            "Yaru" => YaruLightStyle,
            "Yaru-dark" => YaruDarkStyle,
            _ => DefaultStyle
        };
    }
    
    private static readonly WidgetStyle AdwaitaLightStyle = new()
    {
        Background = Color.FromHex("#F6F5F4"),
        Foreground = Color.FromHex("#2E3436"),
        Accent = Color.FromHex("#3584E4"),
        FontFamily = "Cantarell",
        FontSize = 11,
        CornerRadius = 8,
        Padding = 12
    };
}
```

**Pros:**
- ✅ Simplest approach — just read theme name, apply predefined styles
- ✅ No complex FFI — use command-line tools or simple libraries
- ✅ Good enough — looks close to native
- ✅ Maintainable — add new themes as needed

**Cons:**
- ❌ Not pixel-perfect — predefined styles are approximations
- ❌ Manual updates — need to add new themes manually
- ❌ Limited detail — can't get every subtle style detail

---

### Recommendation: Option 4 (Theme Extraction)

For a hardware monitoring app, **Option 4 is the most practical**:

1. **Detect theme name** — use `gsettings` (GNOME) or KConfig (KDE)
2. **Map to predefined styles** — maintain a library of theme styles
3. **Apply to widgets** — use those styles in your C# widgets

**Why This Works:**
- ✅ 80/20 rule — gets you 80% of the way with 20% of the effort
- ✅ Maintainable — add themes as users request them
- ✅ No complex FFI — use simple command-line tools
- ✅ Good enough — users won't notice subtle differences

**Implementation:**
```csharp
public class ThemeDetector
{
    public string DetectTheme()
    {
        if (IsKde()) return DetectKdeTheme();
        else if (IsGnome()) return DetectGnomeTheme();
        return "default";
    }
    
    private bool IsKde()
    {
        return Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") == "KDE";
    }
    
    private bool IsGnome()
    {
        return Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") == "GNOME";
    }
    
    private string DetectGnomeTheme()
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "gsettings",
            Arguments = "get org.gnome.desktop.interface gtk-theme",
            RedirectStandardOutput = true
        });
        return process.StandardOutput.ReadToEnd().Trim().Trim('\'');
    }
}

public class ThemeApplier
{
    private readonly ThemeDetector _detector;
    
    public void ApplyTheme(Application app)
    {
        var themeName = _detector.DetectTheme();
        var style = ThemeMappings.GetStyleForGtkTheme(themeName);
        
        app.Resources["WidgetBackground"] = style.Background;
        app.Resources["WidgetForeground"] = style.Foreground;
        app.Resources["WidgetAccent"] = style.Accent;
    }
}
```

---

## Bottom Line

**Perfect native theming is extremely difficult.** Even big companies (Microsoft with VS Code, Spotify, Discord) don't achieve it.

**Realistic options:**

1. **Good enough (recommended):** Detect theme, apply predefined styles → 80% native look, 20% effort
2. **Better:** Extract theme colors/fonts via FFI → 90% native look, 50% effort
3. **Perfect:** Embed actual Qt/GTK widgets → 100% native look, 200% effort

**For your app:** Option 1 or 2 is probably fine. Users care more about functionality than pixel-perfect widgets.
