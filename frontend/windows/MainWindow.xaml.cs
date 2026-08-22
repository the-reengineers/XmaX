using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
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

    // Window show animation
    private const int ShowAnimationDurationMs = 250;  // Similar to Quick Settings flyout
    private const int ShowAnimationOffsetPixels = 50; // Start offset below final position

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
    private const int HWND_TOPMOST = -1;
    private const int SWP_NOMOVE = 0x0002;
    private const int SWP_NOSIZE = 0x0001;
    private const int SWP_NOZORDER = 0x0004;
    private const int SWP_NOACTIVATE = 0x0010;
    private const int SWP_SHOWWINDOW = 0x0040;
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;

    // Resize handle constants
    private const int IDC_SIZENS = 32515;
    private const int IDC_SIZEWE = 32644;
    private const int IDC_ARROW = 32512;

    // Suppress deactivation handler briefly when showing window
    private bool _suppressDeactivation;
    private bool _modalDialogOpen;
    private DateTime _lastShowTime;

    // Top-edge resize state (edit mode only)
    private bool _isResizing;
    private int _resizeStartScreenY;
    private int _resizeStartHeightPhysical;

    // Left-edge resize state (edit mode only)
    private bool _isResizingWidth;
    private int _resizeStartScreenX;
    private int _resizeStartWidthPhysical;

    // Edit mode state
    private bool _isEditMode;

    // Low-level mouse hook — dismisses window on click-outside
    private IntPtr _mouseHookHandle;
    private LowLevelMouseProc? _mouseHookProc; // Prevent GC of delegate

    // Marquee animation state
    private Storyboard? _marqueeStoryboard;

    // Window show/hide animation state (uses CompositionTarget.Rendering for smooth frame sync)
    private bool _isAnimating;
    private bool _isHidingAnimation;
    private DateTime _animationStartTime;
    private int _animationStartY;
    private int _animationTargetY;
    private int _animationX;

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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

    private const uint SPI_GETWORKAREA = 0x0030;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", EntryPoint = "LoadCursorW")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int cursor);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    private const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;

    // Get DPI scale factor for the primary display
    private double GetDpiScale()
    {
        // Get the primary monitor's handle
        var primaryPoint = new POINT { X = 0, Y = 0 };
        var primaryMonitor = MonitorFromPoint(primaryPoint, MONITOR_DEFAULTTOPRIMARY);

        // Get DPI for the primary monitor
        if (GetDpiForMonitor(primaryMonitor, 0, out var dpiX, out _) == 0)
        {
            return dpiX / 96.0;
        }

        // Fallback to window DPI if monitor DPI fails
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

    /// <summary>Compute window height in DIPs from row count.</summary>
    private double ComputeWindowHeightDIPs(int rows)
    {
        const double bottomBarHeight = 50.0;
        const double gridPadding = 12.0;
        const double spacing = 8.0;
        var cellHeight = (double)App.WidgetService.ColumnWidth;
        return bottomBarHeight + 2 * gridPadding + rows * cellHeight + (rows - 1) * spacing;
    }

    private int GetScaledWindowHeight(int rows)
    {
        var heightDIPs = ComputeWindowHeightDIPs(rows);
        var scale = GetDpiScale();
        return (int)(heightDIPs * scale);
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

        // Apply theme from config
        await ApplyThemeFromConfig();

        // Navigate to home page by default (after config is loaded)
        RootFrame.Navigate(typeof(HomePage), null, new SuppressNavigationTransitionInfo());
    }

    /// <summary>
    /// Apply theme from config file.
    /// </summary>
    private async Task ApplyThemeFromConfig()
    {
        try
        {
            var data = await App.Pipe.SendCommandAsync("get_config").ConfigureAwait(true);
            var config = System.Text.Json.JsonSerializer.Deserialize<Models.AppConfig>(
                data.ToJsonString(),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = false }
            );

            if (config?.Theme != null)
            {
                ApplyTheme(config.Theme);
            }
        }
        catch
        {
            // Failed to load config — use system default
        }
    }

    /// <summary>
    /// Apply the specified theme to the application.
    /// </summary>
    /// <param name="theme">Theme name: "system", "light", or "dark"</param>
    public void ApplyTheme(string theme)
    {
        var elementTheme = theme.ToLower() switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        // Apply theme to the root frame
        RootFrame.RequestedTheme = elementTheme;
    }

    // ===== System Transparency Effects =====

    private void SetupTransparencyDetection()
    {
        TransparencyEffectsEnabled = _uiSettings.AdvancedEffectsEnabled;
        _uiSettings.ColorValuesChanged += OnColorValuesChanged;
        UpdateBottomBarTheme();
    }

    private void OnColorValuesChanged(UISettings sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            TransparencyEffectsEnabled = sender.AdvancedEffectsEnabled;
            UpdateBottomBarTheme();
        });
    }

    private void UpdateBottomBarTheme()
    {
        var bgColor = _uiSettings.GetColorValue(UIColorType.Background);
        var isDarkMode = bgColor.R < 128;

        if (isDarkMode)
        {
            BottomBar.Background = Application.Current.Resources["SystemControlBackgroundChromeBlackLowBrush"] as Microsoft.UI.Xaml.Media.Brush;
            BottomBar.BorderBrush = Application.Current.Resources["SystemControlBackgroundChromeBlackMediumBrush"] as Microsoft.UI.Xaml.Media.Brush;
            BottomBar.BorderThickness =  new Thickness(0, 0.4, 0, 0);   
        }
        else
        {
            BottomBar.Background = null;
            BottomBar.BorderBrush = Application.Current.Resources["DividerStrokeColorDefaultBrush"] as Microsoft.UI.Xaml.Media.Brush;
        }
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

    // ===== Keyboard Navigation =====

    private void OnGridKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        // Forward keyboard events to SettingsPage if it's the current page
        if (RootFrame.Content is Pages.SettingsPage settingsPage)
        {
            // Backspace or Alt+Left: Go back
            if (e.Key == Windows.System.VirtualKey.Back ||
                (e.Key == Windows.System.VirtualKey.Left && e.KeyStatus.IsMenuKeyDown))
            {
                settingsPage.HandleGoBack();
                e.Handled = true;
            }
            // Alt+Right: Go forward
            else if (e.Key == Windows.System.VirtualKey.Right && e.KeyStatus.IsMenuKeyDown)
            {
                settingsPage.HandleGoForward();
                e.Handled = true;
            }
        }
    }

    // ===== Bottom Bar Button Handlers =====

    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (_isEditMode)
        {
            ExitEditMode();
        }
        else
        {
            EnterEditMode();
        }
    }

    /// <summary>Enter edit mode: show the home editor window.</summary>
    public void EnterEditMode()
    {
        _isEditMode = true;
        ResizeHandle.Visibility = Visibility.Visible;
        ResizeHandleLeft.Visibility = Visibility.Visible;
        EditIcon.Glyph = "ﯿ";  // Change to close/done icon
        App.ShowEditorWindow();

        // Notify HomePage to show close buttons on widgets
        if (RootFrame.Content is Pages.HomePage homePage)
        {
            homePage.SetEditMode(true);
        }
    }

    /// <summary>Exit edit mode: hide the home editor window.</summary>
    public void ExitEditMode()
    {
        _isEditMode = false;
        ResizeHandle.Visibility = Visibility.Collapsed;
        ResizeHandleLeft.Visibility = Visibility.Collapsed;
        EditIcon.Glyph = "";  // Change back to pencil icon
        App.HideEditorWindow();

        // Notify HomePage to hide close buttons on widgets
        if (RootFrame.Content is Pages.HomePage homePage)
        {
            homePage.SetEditMode(false);
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (RootFrame.CurrentSourcePageType != typeof(SettingsPage))
        {
            // Navigate to settings page (entrance from bottom)
            RootFrame.Navigate(typeof(SettingsPage), null, new EntranceNavigationTransitionInfo());
            SettingsIcon.Glyph = "";
            UpdateUIForSettingsPage();
            ResizeWindowToCurrentConfig();
        }
        else
        {
            // Navigate back to home page (slide from left)
            RootFrame.Navigate(typeof(Pages.HomePage), null, new SlideNavigationTransitionInfo
            {
                Effect = SlideNavigationTransitionEffect.FromLeft
            });
            UpdateUIForHomePage();
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
            || e.PropertyName == nameof(Services.WidgetService.WindowHeightRows))
        {
            ResizeWindowToCurrentConfig();
        }
    }

    private void ResizeWindowToCurrentConfig()
    {
        var columns = App.WidgetService.Columns;
        var columnWidth = App.WidgetService.ColumnWidth;
        var rows = ClampRowsToDisplay(App.WidgetService.WindowHeightRows);
        var width = GetScaledWindowWidth(columns, columnWidth);
        var height = GetScaledWindowHeight(rows);

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
                App.WidgetService.WindowHeightRows = config.HomeLayout.WindowHeightRows;
                App.WidgetService.LoadWidgetSpans(config.HomeLayout.Widgets, config.HomeLayout.HiddenWidgets);
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
            // Keep border but remove title bar (matches Quick Settings flyout)
            presenter.SetBorderAndTitleBar(true, false);

            // Always on top
            presenter.IsAlwaysOnTop = true;

            // Not resizable
            presenter.IsResizable = false;

            // Not maximizable/minimizable
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        // Set window size based on current config (clamped to current display)
        var columns = App.WidgetService.Columns;
        var columnWidth = App.WidgetService.ColumnWidth;
        var rows = ClampRowsToDisplay(App.WidgetService.WindowHeightRows);
        var width = GetScaledWindowWidth(columns, columnWidth);
        var height = GetScaledWindowHeight(rows);
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

        // Get primary monitor's work area directly from Win32 API.
        // This is more reliable than DisplayArea.Primary which may return stale data
        // when the primary display changes while the app is running.
        var workAreaRect = new RECT();
        SystemParametersInfo(SPI_GETWORKAREA, 0, ref workAreaRect, 0);

        // Convert to RectInt32 for consistency with existing code
        var workArea = new RectInt32(workAreaRect.Left, workAreaRect.Top,
            workAreaRect.Right - workAreaRect.Left,
            workAreaRect.Bottom - workAreaRect.Top);

        // Get screen height from primary monitor (full bounds, not just work area)
        var screenHeight = workAreaRect.Bottom;

        // Get current window size (already scaled)
        var windowSize = appWindow.Size;

        // Position bottom-right with margin
        var x = workArea.X + workArea.Width - windowSize.Width - WindowMargin;
        var y = workArea.Y + workArea.Height - windowSize.Height - WindowMargin;

        // If WorkArea extends to the screen bottom (auto-hide taskbar mode), check
        // whether the taskbar is currently revealed. If so, move the window above it.
        // When the taskbar is permanently visible, WorkArea already excludes it and
        // workArea.Bottom < screenHeight — so this block is skipped.
        var workAreaBottom = workArea.Y + workArea.Height;
        if (workAreaBottom >= screenHeight)
        {
            var taskbarHwnd = FindWindow("Shell_TrayWnd", null);
            if (taskbarHwnd != IntPtr.Zero && IsWindowVisible(taskbarHwnd))
            {
                if (GetWindowRect(taskbarHwnd, out var taskbarRect))
                {
                    // Bottom-positioned taskbar: top edge is above screen bottom.
                    if (taskbarRect.Top < screenHeight && taskbarRect.Bottom == screenHeight)
                    {
                        y -= taskbarRect.Bottom - taskbarRect.Top;
                    }
                }
            }
        }

        // Use Win32 SetWindowPos to set position even when window is hidden.
        // This ensures the position is set before the window becomes visible,
        // avoiding a flash at the old position when the primary display changes.
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
            (uint)(SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE));
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
        // Don't hide if in edit mode (home editor window is visible) or if a modal dialog is open.
        if (e.WindowActivationState == WindowActivationState.Deactivated
            && !_suppressDeactivation
            && !_modalDialogOpen
            && (DateTime.Now - _lastShowTime).TotalMilliseconds > 500
            && !_isEditMode)
        {
            HideWindow();
        }
    }

    /// <summary>
    /// Suppress window deactivation while a modal dialog is open.
    /// Call with true before showing a dialog, false after it closes.
    /// </summary>
    public void SetModalDialogOpen(bool isOpen)
    {
        _modalDialogOpen = isOpen;
    }

    // --- Low-level mouse hook: dismisses window on click outside ---
    // Covers the case where the window was shown but never activated (e.g.
    // SetForegroundWindow failed from a background process), so the Activated
    // event never fires and OnWindowActivated never sees a Deactivation.

    private void InstallMouseHook()
    {
        if (_mouseHookHandle != IntPtr.Zero) return;
        _mouseHookProc = MouseHookCallback;
        _mouseHookHandle = SetWindowsHookEx(WH_MOUSE_LL, _mouseHookProc, GetModuleHandle(null), 0);
    }

    private void RemoveMouseHook()
    {
        if (_mouseHookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHookHandle);
            _mouseHookHandle = IntPtr.Zero;
        }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_isEditMode)
        {
            var msg = wParam.ToInt32();
            if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN)
            {
                var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var clickPt = new PointInt32(hookStruct.pt.X, hookStruct.pt.Y);

                // Check if click is outside our window
                var ourHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                if (GetWindowRect(ourHwnd, out var ourRect))
                {
                    if (clickPt.X < ourRect.Left || clickPt.X >= ourRect.Right
                        || clickPt.Y < ourRect.Top || clickPt.Y >= ourRect.Bottom)
                    {
                        // Click outside — enqueue hide on the UI thread
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (IsWindowVisible(ourHwnd))
                                HideWindow();
                        });
                    }
                }
            }
        }
        return CallNextHookEx(_mouseHookHandle, nCode, wParam, lParam);
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

    // ===== Top-edge resize (edit mode only) =====

    private void OnResizePointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        ((Microsoft.UI.Xaml.UIElement)sender).CapturePointer(e.Pointer);
        GetCursorPos(out var cursorPos);
        _isResizing = true;
        _resizeStartScreenY = cursorPos.Y;
        _resizeStartHeightPhysical = this.AppWindow.Size.Height;
        e.Handled = true;
    }

    private void OnResizePointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        // Set resize cursor while over the handle
        SetCursor(LoadCursor(IntPtr.Zero, IDC_SIZENS));

        if (!_isResizing) return;
        if (!GetCursorPos(out var cursorPos)) return;

        var dpiScale = GetDpiScale();

        // Delta in physical pixels: dragging up = positive = window grows
        var deltaPhysical = _resizeStartScreenY - cursorPos.Y;
        var rawHeightPhysical = _resizeStartHeightPhysical + deltaPhysical;

        // Snap to row count
        var rawHeightDIPs = rawHeightPhysical / dpiScale;
        var rows = HeightDIPsToRows(rawHeightDIPs);
        rows = ClampRowsToDisplay(rows);
        var snappedHeightPhysical = (int)(ComputeWindowHeightDIPs(rows) * dpiScale);

        // Resize window keeping bottom edge fixed
        var posX = this.AppWindow.Position.X;
        var windowSize = this.AppWindow.Size;
        var bottomY = this.AppWindow.Position.Y + windowSize.Height;
        var newY = bottomY - snappedHeightPhysical;
        this.AppWindow.Resize(new SizeInt32(windowSize.Width, snappedHeightPhysical));
        this.AppWindow.Move(new PointInt32(posX, newY));

        e.Handled = true;
    }

    private void OnResizePointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        ((Microsoft.UI.Xaml.UIElement)sender).ReleasePointerCapture(e.Pointer);

        if (_isResizing)
        {
            _isResizing = false;

            // Compute row count from current height and save
            var dpiScale = GetDpiScale();
            var heightDIPs = this.AppWindow.Size.Height / dpiScale;
            var rows = HeightDIPsToRows(heightDIPs);
            App.WidgetService.WindowHeightRows = rows;

            // Persist to config.json
            _ = App.WidgetService.SaveLayoutAsync();

            // Reposition editor window to match new main window height
            App.EditorWindow?.PositionRelativeToMainWindow();
        }
    }

    private void OnResizePointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_isResizing)
        {
            // Restore arrow cursor when leaving the handle (not during drag)
            SetCursor(LoadCursor(IntPtr.Zero, IDC_ARROW));
        }
    }

    // ===== Left-edge resize (column width) handlers =====

    private void OnWidthResizePointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        ((Microsoft.UI.Xaml.UIElement)sender).CapturePointer(e.Pointer);
        GetCursorPos(out var cursorPos);
        _isResizingWidth = true;
        _resizeStartScreenX = cursorPos.X;
        _resizeStartWidthPhysical = this.AppWindow.Size.Width;
        e.Handled = true;
    }

    private void OnWidthResizePointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        // Set horizontal resize cursor while over the handle
        SetCursor(LoadCursor(IntPtr.Zero, IDC_SIZEWE));

        if (!_isResizingWidth) return;
        if (!GetCursorPos(out var cursorPos)) return;

        var dpiScale = GetDpiScale();

        // Delta in physical pixels: dragging left = negative cursor delta = window grows
        // (left handle: dragging left extends the left edge outward)
        var deltaPhysical = _resizeStartScreenX - cursorPos.X;
        var rawWidthPhysical = _resizeStartWidthPhysical + deltaPhysical;

        // Snap to column count
        var rawWidthDIPs = rawWidthPhysical / dpiScale;
        var columns = WidthDIPsToColumns(rawWidthDIPs);

        // Update column count in memory immediately so the widget grid uses the
        // correct count when it re-layouts in response to SizeChanged. The setter
        // is idempotent — it only fires PropertyChanged when the value changes.
        // Config persistence happens on pointer release, not here.
        App.WidgetService.Columns = columns;

        var snappedWidthPhysical = GetScaledWindowWidth(columns, App.WidgetService.ColumnWidth);

        // Resize window keeping right edge fixed
        var posY = this.AppWindow.Position.Y;
        var windowSize = this.AppWindow.Size;
        var rightEdge = this.AppWindow.Position.X + windowSize.Width;
        var newX = rightEdge - snappedWidthPhysical;
        this.AppWindow.Resize(new SizeInt32(snappedWidthPhysical, windowSize.Height));
        this.AppWindow.Move(new PointInt32(newX, posY));

        e.Handled = true;
    }

    private void OnWidthResizePointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        ((Microsoft.UI.Xaml.UIElement)sender).ReleasePointerCapture(e.Pointer);

        if (_isResizingWidth)
        {
            _isResizingWidth = false;

            // Compute column count from current width and save
            var dpiScale = GetDpiScale();
            var widthDIPs = this.AppWindow.Size.Width / dpiScale;
            var columns = WidthDIPsToColumns(widthDIPs);
            App.WidgetService.Columns = columns;

            // Persist to config.json
            _ = App.WidgetService.SaveLayoutAsync();

            // Reposition editor window to match new main window width
            App.EditorWindow?.PositionRelativeToMainWindow();
        }
    }

    private void OnWidthResizePointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_isResizingWidth)
        {
            // Restore arrow cursor when leaving the handle (not during drag)
            SetCursor(LoadCursor(IntPtr.Zero, IDC_ARROW));
        }
    }

    /// <summary>
    /// Convert a raw width (DIPs) to the nearest column count.
    /// windowWidth(DIPs) = columns * columnWidth + (columns - 1) * spacing + 2 * gridPadding
    ///                   = columns * (columnWidth + spacing) - spacing + 2 * gridPadding
    /// columns = (widthDIPs + spacing - 2 * gridPadding) / (columnWidth + spacing)
    /// </summary>
    private int WidthDIPsToColumns(double widthDIPs)
    {
        var columnWidth = (double)App.WidgetService.ColumnWidth;
        var spacing = (double)ColumnSpacing;
        var padding = (double)GridPadding;

        var rawColumns = (widthDIPs + spacing - 2 * padding) / (columnWidth + spacing);
        var columns = (int)Math.Round(rawColumns);
        return Math.Clamp(columns, Services.WidgetService.MinColumns, Services.WidgetService.MaxColumns);
    }

    /// <summary>
    /// Convert a raw height (DIPs) to the nearest row count.
    /// windowHeight(DIPs) = bottomBar + canvasHeight
    ///                    = 50 + 2*gridPadding + N*cellHeight + (N-1)*spacing
    ///                    = 66 + N*(cellHeight + 8)
    /// </summary>
    private int HeightDIPsToRows(double heightDIPs)
    {
        const double bottomBarHeight = 50.0;
        const double gridPadding = 12.0;
        const double spacing = 8.0;
        var cellHeight = (double)App.WidgetService.ColumnWidth;

        var canvasHeight = heightDIPs - bottomBarHeight;
        var rowUnit = cellHeight + spacing;
        var n = (canvasHeight - 2 * gridPadding + spacing) / rowUnit;
        var rounded = (int)Math.Round(n);
        return Math.Clamp(rounded, Services.WidgetService.MinWindowHeightRows, Services.WidgetService.MaxWindowHeightRows);
    }

    /// <summary>
    /// Clamp row count to [1, min(5, displayMax)].
    /// Ensures the window fits when opened on a smaller monitor than where it was saved.
    /// </summary>
    private int ClampRowsToDisplay(int rows)
    {
        var maxRows = GetMaxRowCountForDisplay();
        return Math.Clamp(rows, Services.WidgetService.MinWindowHeightRows,
            Math.Min(Services.WidgetService.MaxWindowHeightRows, maxRows));
    }

    /// <summary>
    /// Max row count that fits on the current display, accounting for margins and
    /// a temporarily-visible taskbar (auto-hide mode). Result is floored to whole rows.
    /// </summary>
    private int GetMaxRowCountForDisplay()
    {
        const double bottomBarHeight = 50.0;
        const double gridPadding = 12.0;
        const double spacing = 8.0;
        var cellHeight = (double)App.WidgetService.ColumnWidth;

        // Get primary monitor's work area directly from Win32 API
        var workAreaRect = new RECT();
        SystemParametersInfo(SPI_GETWORKAREA, 0, ref workAreaRect, 0);

        var dpiScale = GetDpiScale();

        // Available height in physical pixels:
        // workArea already excludes a permanently-visible taskbar.
        // If the taskbar is auto-hide and currently revealed, subtract its height too.
        var screenHeight = workAreaRect.Bottom;
        var taskbarOffsetPx = 0;

        var workAreaBottom = workAreaRect.Bottom;
        if (workAreaBottom >= screenHeight)
        {
            // WorkArea extends to screen bottom → auto-hide taskbar mode.
            // Check if the taskbar is currently revealed.
            var taskbarHwnd = FindWindow("Shell_TrayWnd", null);
            if (taskbarHwnd != IntPtr.Zero && IsWindowVisible(taskbarHwnd))
            {
                if (GetWindowRect(taskbarHwnd, out var taskbarRect))
                {
                    if (taskbarRect.Top < screenHeight && taskbarRect.Bottom == screenHeight)
                    {
                        taskbarOffsetPx = taskbarRect.Bottom - taskbarRect.Top;
                    }
                }
            }
        }

        var availableHeightPx = (workAreaRect.Bottom - workAreaRect.Top) - 2 * WindowMargin - taskbarOffsetPx;
        var maxHeightDIPs = availableHeightPx / dpiScale;
        var canvasHeight = maxHeightDIPs - bottomBarHeight;
        var rowUnit = cellHeight + spacing;
        var n = (canvasHeight - 2 * gridPadding + spacing) / rowUnit;
        return Math.Max(1, (int)Math.Floor(n));
    }

    // ===== Public Navigation Methods =====

    /// <summary>
    /// Update the settings button glyph to show the settings icon (for home page).
    /// </summary>
    public void UpdateSettingsIconForHome()
    {
        SettingsIcon.Glyph = "";
    }

    /// <summary>
    /// Update UI elements for home page (show edit button, settings icon).
    /// </summary>
    public void UpdateUIForHomePage()
    {
        UpdateSettingsIconForHome();
        EditButton.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Update UI elements for settings page (hide edit button, show home icon).
    /// </summary>
    public void UpdateUIForSettingsPage()
    {
        EditButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>Navigate the main frame to a page type with an optional transition.</summary>
    public void NavigateToPage(Type pageType, NavigationTransitionInfo? transitionInfo = null)
    {
        var transition = transitionInfo ?? new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight };
        RootFrame.Navigate(pageType, null, transition);
    }

    /// <summary>Navigate the main frame back to the previous page with a slide-from-left transition.</summary>
    public void GoBack()
    {
        if (RootFrame.CanGoBack)
        {
            var transition = new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromLeft };
            RootFrame.GoBack(transition);
        }
    }

    /// <summary>Refresh the home page widgets (called from editor window).</summary>
    public void RefreshHomePage()
    {
        if (RootFrame.Content is Pages.HomePage homePage)
        {
            homePage.RefreshWidgets();
            // Reapply edit mode to show close buttons
            homePage.SetEditMode(_isEditMode);
        }
    }

    // ===== Public Methods =====

    /// <summary>
    /// Show the window and bring it to front with a fly-in animation from bottom.
    /// Respects system "Animation effects" setting.
    /// </summary>
    public void ShowWindow()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var animationsEnabled = _uiSettings.AnimationsEnabled;

        // Suppress deactivation handler briefly while showing
        _suppressDeactivation = true;
        _lastShowTime = DateTime.Now;

        // Calculate final position on primary display
        PositionBottomRight();
        var finalPos = this.AppWindow.Position;

        if (animationsEnabled)
        {
            // Start position is below the final position (fly-in from bottom)
            var startY = finalPos.Y + ShowAnimationOffsetPixels;
            SetWindowPos(hwnd, IntPtr.Zero, finalPos.X, startY, 0, 0,
                (uint)(SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE));
        }

        // Show the window
        ShowWindow(hwnd, SW_SHOW);

        // Re-assert TOPMOST on every show — after hide/show cycles Windows can
        // drop the z-order, and OverlappedPresenter.IsAlwaysOnTop alone is not
        // always sufficient. This also places us above the taskbar if it was
        // temporarily revealed.
        SetWindowPos(hwnd, new IntPtr(HWND_TOPMOST), 0, 0, 0, 0,
            (uint)(SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW));

        // Bring to foreground. SetForegroundWindow silently fails when called
        // from a background process (our case — triggered from tray/hardware
        // button). Attach our input thread to the foreground window's thread
        // to satisfy Windows' foreground lock, then detach immediately after.
        var foregroundHwnd = GetForegroundWindow();
        if (foregroundHwnd != IntPtr.Zero && foregroundHwnd != hwnd)
        {
            var fgThreadId = GetWindowThreadProcessId(foregroundHwnd, out _);
            var ourThreadId = GetCurrentThreadId();
            if (fgThreadId != ourThreadId)
            {
                AttachThreadInput(ourThreadId, fgThreadId, true);
                SetForegroundWindow(hwnd);
                AttachThreadInput(ourThreadId, fgThreadId, false);
            }
            else
            {
                SetForegroundWindow(hwnd);
            }
        }
        else
        {
            SetForegroundWindow(hwnd);
        }

        // Start fly-in animation (only if system animations are enabled)
        if (animationsEnabled)
        {
            var startY = finalPos.Y + ShowAnimationOffsetPixels;
            StartShowAnimation(finalPos.X, startY, finalPos.Y);
        }

        // Install mouse hook to dismiss on click-outside (covers the case where
        // SetForegroundWindow failed and the window was never activated)
        InstallMouseHook();

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
    /// Start the fly-in animation from bottom.
    /// Uses CompositionTarget.Rendering for smooth frame-synced animation.
    /// </summary>
    private void StartShowAnimation(int x, int startY, int targetY)
    {
        _animationX = x;
        _animationStartY = startY;
        _animationTargetY = targetY;
        _animationStartTime = DateTime.Now;
        _isHidingAnimation = false;
        _isAnimating = true;

        // Subscribe to rendering event for frame-synced updates
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnAnimationRendering;
    }

    /// <summary>
    /// Start the fly-out animation to bottom (before hiding).
    /// </summary>
    private void StartHideAnimation()
    {
        var pos = this.AppWindow.Position;
        _animationX = pos.X;
        _animationStartY = pos.Y;
        _animationTargetY = pos.Y + ShowAnimationOffsetPixels;
        _animationStartTime = DateTime.Now;
        _isHidingAnimation = true;
        _isAnimating = true;

        // Subscribe to rendering event for frame-synced updates
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnAnimationRendering;
    }

    /// <summary>
    /// Frame-synced animation handler using CompositionTarget.Rendering.
    /// Syncs to display refresh rate for smooth animation.
    /// </summary>
    private void OnAnimationRendering(object? sender, object e)
    {
        if (!_isAnimating) return;

        var elapsed = (DateTime.Now - _animationStartTime).TotalMilliseconds;
        var progress = Math.Min(1.0, elapsed / ShowAnimationDurationMs);

        // Apply easing based on animation type
        double easedProgress;
        if (_isHidingAnimation)
        {
            // Cubic ease-in for hide: t^3 (accelerates out)
            easedProgress = Math.Pow(progress, 3);
        }
        else
        {
            // Cubic ease-out for show: 1 - (1 - t)^3 (decelerates in)
            easedProgress = 1.0 - Math.Pow(1.0 - progress, 3);
        }

        // Calculate current Y position
        var deltaY = _animationTargetY - _animationStartY;
        var currentY = (int)(_animationStartY + deltaY * easedProgress);

        // Move window
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SetWindowPos(hwnd, IntPtr.Zero, _animationX, currentY, 0, 0,
            (uint)(SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE));

        // Stop animation when complete
        if (progress >= 1.0)
        {
            _isAnimating = false;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnAnimationRendering;

            // If this was a hide animation, finish hiding
            if (_isHidingAnimation)
            {
                FinishHide();
            }
        }
    }

    /// <summary>
    /// Complete the hide operation after animation finishes.
    /// </summary>
    private void FinishHide()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        ShowWindow(hwnd, SW_HIDE);

        // Remove mouse hook (no longer needed while hidden)
        RemoveMouseHook();

        // Reset to default state while hidden (so it's ready for next show)
        ResetToDefaultState();

        // Suppress metrics UI updates while hidden (data still collected)
        App.MetricsService.SuppressNotifications = true;
    }

    /// <summary>
    /// Reset the window to its default state: home page, not in edit mode, scrolled to top.
    /// </summary>
    private void ResetToDefaultState()
    {
        // Exit edit mode if in edit mode
        if (_isEditMode)
        {
            ExitEditMode();
        }

        // Hide editor window if visible
        App.HideEditorWindow();

        // Navigate to home page if not already there
        if (RootFrame.CurrentSourcePageType != typeof(Pages.HomePage))
        {
            RootFrame.Navigate(typeof(Pages.HomePage), null, new SlideNavigationTransitionInfo
            {
                Effect = SlideNavigationTransitionEffect.FromLeft
            });
            UpdateUIForHomePage();
            ResizeWindowToCurrentConfig();
        }

        // Scroll home page to top
        if (RootFrame.Content is Pages.HomePage homePage)
        {
            homePage.ScrollToTop();
        }
    }

    /// <summary>
    /// Hide the window with a fly-out animation to bottom.
    /// Respects system "Animation effects" setting.
    /// </summary>
    public void HideWindow()
    {
        // Stop any ongoing animation
        if (_isAnimating)
        {
            _isAnimating = false;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnAnimationRendering;
        }

        // Check if system animations are enabled
        if (_uiSettings.AnimationsEnabled)
        {
            // Start the hide animation (will call FinishHide when done)
            StartHideAnimation();
        }
        else
        {
            // Hide immediately without animation
            FinishHide();
        }
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
