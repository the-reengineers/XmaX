using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
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

    // Window size
    private const int WindowWidth = 420;
    private const int WindowHeight = 600;

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

    public MainWindow()
    {
        this.InitializeComponent();

        ConfigureWindow();
        PositionBottomRight();
        SetupClickOutsideToHide();
        SetupBackendIntegration();
        SetupNavigationLabels();

        // Navigate to home page by default
        RootFrame.Navigate(typeof(HomePage));
        NavView.SelectedItem = NavView.MenuItems[0];

        // Load config and update test mode banner
        _ = LoadConfigAndUpdateBannerAsync();
    }

    // ===== Navigation Labels =====

    private void SetupNavigationLabels()
    {
        // Set tab labels from Loc
        var items = NavView.MenuItems;
        if (items.Count >= 4)
        {
            ((NavigationViewItem)items[0]).Content = Loc.Nav_Home;
            ((NavigationViewItem)items[1]).Content = Loc.Nav_Profiles;
            ((NavigationViewItem)items[2]).Content = Loc.Nav_Cooling;
            ((NavigationViewItem)items[3]).Content = Loc.Nav_Settings;
        }

        // Set test mode banner labels
        TestModeText.Text = Loc.Nav_TestMode;
        SessionPersistToggle.OnContent = Loc.Nav_Apply;
        SessionPersistToggle.OffContent = "";
    }

    // ===== Test Mode Banner =====

    private async Task LoadConfigAndUpdateBannerAsync()
    {
        try
        {
            var data = await App.Pipe.SendCommandAsync("get_config").ConfigureAwait(true);
            var config = System.Text.Json.JsonSerializer.Deserialize<XmaX.Models.AppConfig>(
                data.ToJsonString(),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = false }
            );

            if (config != null)
            {
                // Show banner only when persist=false
                TestModeBanner.Visibility = config.Persist ? Visibility.Collapsed : Visibility.Visible;

                // Update toggle state (without triggering event)
                SessionPersistToggle.IsOn = config.SessionPersist;
            }
        }
        catch
        {
            // Failed to load config — hide banner
            TestModeBanner.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnSessionPersistToggled(object sender, RoutedEventArgs e)
    {
        var value = SessionPersistToggle.IsOn;
        try
        {
            var payload = new System.Text.Json.Nodes.JsonObject
            {
                ["value"] = value
            };
            await App.Pipe.SendCommandAsync("set_session_persist", payload).ConfigureAwait(true);
        }
        catch
        {
            // Failed to send command — revert toggle
            SessionPersistToggle.IsOn = !value;
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

        // Set window size
        appWindow.Resize(new SizeInt32(WindowWidth, WindowHeight));

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

        // Set Mica backdrop for translucent effect
        SystemBackdrop = new MicaBackdrop();
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

        // Position bottom-right with margin
        var x = workArea.X + workArea.Width - WindowWidth - WindowMargin;
        var y = workArea.Y + workArea.Height - WindowHeight - WindowMargin;

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

    // ===== Navigation =====

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            var pageType = tag switch
            {
                "home" => typeof(HomePage),
                "profiles" => typeof(ProfilesPage),
                "cooling" => typeof(CoolingPage),
                "settings" => typeof(SettingsPage),
                _ => typeof(HomePage),
            };

            // Only navigate if different page
            if (RootFrame.CurrentSourcePageType != pageType)
            {
                RootFrame.Navigate(pageType);
            }
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
