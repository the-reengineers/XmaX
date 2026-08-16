using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace XmaX;

/// <summary>
/// Home editor window: appears to the left of the main window when in edit mode.
/// Same styling and behavior as the main window (frameless, always-on-top, click-outside-to-hide).
/// </summary>
public sealed partial class HomeEditorWindow : Window
{
    // Window margin from main window
    private const int WindowMargin = 10;

    // Win32 constants
    private const int GWL_EXSTYLE = -20;
    private const int GWL_STYLE = -16;
    private const int WS_EX_APPWINDOW = 0x00040000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_BORDER = 0x00800000;
    private const int WS_DLGFRAME = 0x00400000;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00020000;
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    // Suppress deactivation handler briefly when showing window
    private bool _suppressDeactivation;
    private DateTime _lastShowTime;

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

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    public HomeEditorWindow()
    {
        this.InitializeComponent();
        ConfigureWindow();
        SetupClickOutsideToHide();
    }

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
        const int columnSpacing = 8;
        const int gridPadding = 12;
        var baseWidth = (columns * columnWidth)
                      + ((columns - 1) * columnSpacing)
                      + (2 * gridPadding);

        var scale = GetDpiScale();
        return (int)(baseWidth * scale);
    }

    private int GetScaledWindowHeight(int windowHeight)
    {
        var scale = GetDpiScale();
        return (int)(windowHeight * scale);
    }

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

        // Set window size: 3 columns wide, same height as main window
        var columns = App.WidgetService.Columns > 0 ? 3 : 3; // Always 3 columns for hidden widgets
        var columnWidth = App.WidgetService.ColumnWidth;
        var windowHeight = App.WidgetService.WindowHeight;
        var width = GetScaledWindowWidth(3, columnWidth);
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

        SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
    }

    private void SetupClickOutsideToHide()
    {
        this.Activated += OnWindowActivated;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        // Exit edit mode on main window (which hides this editor window)
        App.MainWindow?.ExitEditMode();
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs e)
    {
        // If window is being deactivated, check if we should hide.
        // Don't hide if the main window is gaining focus (user clicked on main window).
        if (e.WindowActivationState == WindowActivationState.Deactivated
            && !_suppressDeactivation
            && (DateTime.Now - _lastShowTime).TotalMilliseconds > 500)
        {
            // Use a timer to check if main window gained focus
            // This handles the case where focus moves from editor to main window
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                // Check if main window is visible and has focus
                if (App.MainWindow != null)
                {
                    var mainHwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                    var foregroundHwnd = GetForegroundWindow();

                    // If main window has focus, don't hide editor window
                    if (mainHwnd == foregroundHwnd)
                    {
                        return;
                    }
                }

                // Main window doesn't have focus — hide editor and exit edit mode
                HideWindow();
                App.MainWindow?.ExitEditMode();
            });
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>
    /// Position this window relative to the main window.
    /// Places to the left of main window, or above if too close to left screen edge.
    /// </summary>
    public void PositionRelativeToMainWindow()
    {
        if (App.MainWindow == null) return;

        var mainWindow = App.MainWindow;
        var mainWindowHwnd = WinRT.Interop.WindowNative.GetWindowHandle(mainWindow);
        var mainWindowId = Win32Interop.GetWindowIdFromWindow(mainWindowHwnd);
        var displayArea = DisplayArea.GetFromWindowId(mainWindowId, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;

        var mainAppWindow = mainWindow.AppWindow;
        var mainPosX = mainAppWindow.Position.X;
        var mainPosY = mainAppWindow.Position.Y;
        var mainWidth = mainAppWindow.Size.Width;
        var mainHeight = mainAppWindow.Size.Height;

        var hiddenWidth = this.AppWindow.Size.Width;
        var hiddenHeight = this.AppWindow.Size.Height;

        // Calculate position to the left of main window
        var hiddenPosX = mainPosX - hiddenWidth - WindowMargin;
        var hiddenPosY = mainPosY;

        // Check if too close to left edge (less than 50px from left edge)
        if (hiddenPosX < workArea.X + 50)
        {
            // Position above main window instead
            hiddenPosX = mainPosX;
            hiddenPosY = mainPosY - hiddenHeight - WindowMargin;

            // If also too close to top, position below main window
            if (hiddenPosY < workArea.Y + 50)
            {
                hiddenPosY = mainPosY + mainHeight + WindowMargin;
            }
        }

        this.AppWindow.Move(new PointInt32(hiddenPosX, hiddenPosY));
    }

    /// <summary>
    /// Show the window and bring it to front.
    /// </summary>
    public void ShowWindow()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        // Suppress deactivation handler briefly while showing
        _suppressDeactivation = true;
        _lastShowTime = DateTime.Now;

        // Position relative to main window before showing
        PositionRelativeToMainWindow();

        ShowWindow(hwnd, SW_SHOW);
        SetForegroundWindow(hwnd);

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
