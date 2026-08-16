using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI.ViewManagement;
using XmaX.Pages;

namespace XmaX;

/// <summary>
/// Main application window. Quick Settings-style popup: frameless, always-on-top,
/// no taskbar entry, Mica backdrop, bottom-right positioned. Click-outside-to-hide.
/// </summary>
public sealed partial class MainWindow : Window
{
    // Window margin from screen edge (taskbar-aware)
    private const int WindowMargin = 10;

    // Base dimensions (unscaled, at 100% DPI)
    private const int GridPadding = 12;       // Each side (matches HomePage.xaml)
    private const int ColumnSpacing = 8;      // Between columns (matches HomePage.xaml)

    // Marquee speed in pixels per second
    private const int MarqueePixelsPerSecond = 50;

    // Win32 constants
    private const int GWL_EXSTYLE = -20;
    private const int GWL_STYLE = -16;
    private const int WS_EX_APPWINDOW = 0x00040000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_BORDER = 0x00800000;
    private const int WS_DLGFRAME = 0x00400000;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    // Suppress deactivation handler briefly when showing window
    private bool _suppressDeactivation;
    private DateTime _lastShowTime;

    // Marquee animation state
    private Storyboard? _marqueeStoryboard;

    // System transparency effects detection
    private readonly UISettings _uiSettings = new();
    public bool TransparencyEffectsEnabled { get; private set; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    // Get DPI scale factor for this window
    private double GetDpiScale()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(hwnd);
        return dpi / 96.0;
    }

    // Calculate scaled window dimensions
    private int GetScaledWindowWidth(int columns, int columnWidth)
    {
        // Width = (columns × columnWidth) + ((columns-1) × spacing) + (2 × padding)
        var baseWidth = (columns * columnWidth)
                      + ((columns - 1) * ColumnSpacing)
                      + (2 * GridPadding);

        var scale = GetDpiScale();
        return (int)(baseWidth * scale);
    }

    private int GetScaledWindowHeight(int windowHeight)
    {
        var scale = GetDpiScale();
        return (int)(windowHeight * scale);
    }

    public MainWindow()
    {
        this.InitializeComponent();

        ConfigureWindow();
        PositionBottomRight();
        SetupClickOutsideToHide();
        SetupBackendIntegration();
        SetupTransparencyDetection();
        SetupMarquee();
        SetupDynamicResizing();

        // Load config first (await to ensure layout is loaded before HomePage is created)
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        // Load config and apply home layout
        await LoadConfigAsync();

        // Navigate to home page by default (after config is loaded)
        RootFrame.Navigate(typeof(HomePage));
    }

    // ===== System Transparency Effects =====

    private void SetupTransparencyDetection()
    {
        TransparencyEffectsEnabled = _uiSettings.AdvancedEffectsEnabled;
        _uiSettings.ColorValuesChanged += OnColorValuesChanged;
    }

    private void OnColorValuesChanged(UISettings sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            TransparencyEffectsEnabled = sender.AdvancedEffectsEnabled;
        });
    }

    // ===== Marquee Banner =====

    private void SetupMarquee()
    {
        MarqueeContainer.SizeChanged += (_, _) => UpdateMarqueeAnimation();
        MarqueeContainer.Loaded += (_, _) => UpdateMarqueeAnimation();

        // For now: show session mode message
        MarqueeText.Text = Loc.Nav_TestMode;
    }

    private void UpdateMarqueeAnimation()
    {
        var containerWidth = MarqueeContainer.ActualWidth;
        if (containerWidth <= 0) return;

        // Measure text width
        MarqueeText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var textWidth = MarqueeText.DesiredSize.Width;

        // Apply clip to container
        MarqueeContainer.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, containerWidth, MarqueeContainer.ActualHeight)
        };

        // Stop any existing animation
        _marqueeStoryboard?.Stop();
        _marqueeStoryboard = null;

        if (textWidth <= containerWidth)
        {
            // Text fits — no animation, reset transform
            MarqueeText.RenderTransform = null;
            return;
        }

        // Text overflows — animate marquee (scroll right-to-left)
        var transform = new TranslateTransform();
        MarqueeText.RenderTransform = transform;

        var totalDistance = containerWidth + textWidth;
        var duration = TimeSpan.FromSeconds(totalDistance / (double)MarqueePixelsPerSecond);

        var animation = new DoubleAnimation
        {
            From = containerWidth,
            To = -textWidth,
            Duration = duration,
            RepeatBehavior = RepeatBehavior.Forever,
        };

        Storyboard.SetTarget(animation, transform);
        Storyboard.SetTargetProperty(animation, "X");

        _marqueeStoryboard = new Storyboard();
        _marqueeStoryboard.Children.Add(animation);
        _marqueeStoryboard.Begin();
    }

    // ===== Bottom Bar Button Handlers =====

    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        // TODO: implement edit mode toggle
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (RootFrame.CurrentSourcePageType != typeof(SettingsPage))
        {
            RootFrame.Navigate(typeof(SettingsPage));
            ResizeWindowToCurrentConfig();
        }
    }

    // ===== Dynamic Window Resizing =====

    private void SetupDynamicResizing()
    {
        // Handle column count changes
        App.WidgetService.PropertyChanged += OnWidgetServicePropertyChanged;
    }

    private void OnWidgetServicePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Services.WidgetService.Columns)
            || e.PropertyName == nameof(Services.WidgetService.ColumnWidth)
            || e.PropertyName == nameof(Services.WidgetService.WindowHeight))
        {
            ResizeWindowToCurrentConfig();
        }
    }

    private void ResizeWindowToCurrentConfig()
    {
        var columns = App.WidgetService.Columns;
        var columnWidth = App.WidgetService.ColumnWidth;
        var windowHeight = App.WidgetService.WindowHeight;
        var width = GetScaledWindowWidth(columns, columnWidth);
        var height = GetScaledWindowHeight(windowHeight);

        this.AppWindow.Resize(new SizeInt32(width, height));
        PositionBottomRight();
    }

    // ===== Config Loading =====

    private async Task LoadConfigAsync()
    {
        try
        {
            var data = await App.Pipe.SendCommandAsync("get_config").ConfigureAwait(true);
            var config = System.Text.Json.JsonSerializer.Deserialize<Models.AppConfig>(
                data.ToJsonString(),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = false }
            );

            if (config?.HomeLayout != null)
            {
                App.WidgetService.Columns = config.HomeLayout.Columns;
                App.WidgetService.ColumnWidth = config.HomeLayout.ColumnWidth;
                App.WidgetService.WindowHeight = config.HomeLayout.WindowHeight;
                App.WidgetService.LoadWidgetSpans(config.HomeLayout.Widgets);
            }
        }
        catch
        {
            // Failed to load config — use defaults
        }
    }

    // ===== Window Configuration =====

    private void ConfigureWindow()
    {
        var appWindow = this.AppWindow;

        // Use OverlappedPresenter for frameless window
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            // Remove title bar and border (frameless)
            presenter.SetBorderAndTitleBar(false, false);

            // Always on top
            presenter.IsAlwaysOnTop = true;

            // Not resizable
            presenter.IsResizable = false;

            // Not maximizable/minimizable
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        // Set window size based on current config
        var columns = App.WidgetService.Columns;
        var columnWidth = App.WidgetService.ColumnWidth;
        var windowHeight = App.WidgetService.WindowHeight;
        var width = GetScaledWindowWidth(columns, columnWidth);
        var height = GetScaledWindowHeight(windowHeight);
        appWindow.Resize(new SizeInt32(width, height));

        // Hide from task switchers
        appWindow.IsShownInSwitchers = false;

        // Remove from taskbar using Win32 API
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        exStyle &= ~WS_EX_APPWINDOW;  // Remove from taskbar
        exStyle |= WS_EX_TOOLWINDOW;  // Tool window (no taskbar)
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);

        // Remove window frame/border styles
        var style = GetWindowLong(hwnd, GWL_STYLE);
        style &= ~(WS_BORDER | WS_DLGFRAME | WS_CAPTION | WS_THICKFRAME);
        SetWindowLong(hwnd, GWL_STYLE, style);

        // Extend DWM frame completely to eliminate any remaining border
        var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);

        // Extend content into title bar area to remove default chrome
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(null); // No custom title bar

        SystemBackdrop = new DesktopAcrylicBackdrop();
    }

    // ===== Positioning =====

    private void PositionBottomRight()
    {
        var appWindow = this.AppWindow;

        // Get display area (work area excludes taskbar)
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);

        var workArea = displayArea.WorkArea;

        // Get current window size (already scaled)
        var windowSize = appWindow.Size;

        // Position bottom-right with margin
        var x = workArea.X + workArea.Width - windowSize.Width - WindowMargin;
        var y = workArea.Y + workArea.Height - windowSize.Height - WindowMargin;

        appWindow.Move(new PointInt32(x, y));
    }

    // ===== Click-outside-to-hide =====

    private void SetupClickOutsideToHide()
    {
        // When window loses activation (user clicks outside), hide it
        this.Activated += OnWindowActivated;
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs e)
    {
        // If window is being deactivated, hide it (unless suppressed during show
        // or the window was shown less than 500ms ago — prevents focus-stealing
        // from immediately hiding the window after a tray/button toggle).
        if (e.WindowActivationState == WindowActivationState.Deactivated
            && !_suppressDeactivation
            && (DateTime.Now - _lastShowTime).TotalMilliseconds > 500)
        {
            HideWindow();
        }
    }

    // ===== Backend Integration =====

    private void SetupBackendIntegration()
    {
        // Listen for show_toggle events from backend (tray icon or hardware button)
        App.Pipe.EventReceived += OnBackendEvent;
    }

    private void OnBackendEvent(string eventName, System.Text.Json.Nodes.JsonObject data)
    {
        if (eventName == "show_toggle")
        {
            DispatcherQueue.TryEnqueue(ToggleVisibility);
        }
    }

    // ===== Public Navigation Methods =====

    /// <summary>Navigate the main frame to a page type with an optional transition.</summary>
    public void NavigateToPage(Type pageType, NavigationTransitionInfo? transitionInfo = null)
    {
        if (transitionInfo != null)
            RootFrame.Navigate(pageType, null, transitionInfo);
        else
            RootFrame.Navigate(pageType);
    }

    /// <summary>Navigate the main frame back to the previous page with a slide-from-left transition.</summary>
    public void GoBack()
    {
        if (RootFrame.CanGoBack)
        {
            var transition = new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight };
            RootFrame.GoBack(transition);
        }
    }

    // ===== Public Methods =====

    /// <summary>
    /// Show the window and bring it to front.
    /// </summary>
    public void ShowWindow()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        // Suppress deactivation handler briefly while showing
        _suppressDeactivation = true;
        _lastShowTime = DateTime.Now;
        ShowWindow(hwnd, SW_SHOW);
        SetForegroundWindow(hwnd);
        PositionBottomRight(); // Re-position in case display changed

        // Resume metrics UI updates and refresh widgets with latest data
        App.MetricsService.SuppressNotifications = false;
        App.MetricsService.NotifyRefresh();

        // Re-enable deactivation handler after a short delay
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _suppressDeactivation = false;
        });
    }

    /// <summary>
    /// Hide the window.
    /// </summary>
    public void HideWindow()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ShowWindow(hwnd, SW_HIDE);

        // Suppress metrics UI updates while hidden (data still collected)
        App.MetricsService.SuppressNotifications = true;
    }

    /// <summary>
    /// Toggle window visibility.
    /// </summary>
    public void ToggleVisibility()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (IsWindowVisible(hwnd))
        {
            HideWindow();
        }
        else
        {
            ShowWindow();
        }
    }
}
