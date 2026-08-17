using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    /// <summary>
    /// Load and display hidden widgets with "+" buttons using WidgetGridHost.
    /// </summary>
    private void LoadHiddenWidgets()
    {
        var hiddenIds = App.WidgetService.HiddenWidgetIds;
        var gridWidgets = new List<WidgetFramework.GridWidget>();

        foreach (var widgetId in hiddenIds)
        {
            var widget = WidgetFramework.HomeWidgetFactory.CreateWidget(widgetId);
            if (widget == null) continue;

            var content = widget.Content as FrameworkElement;
            if (content == null) continue;

            // Create "+" button (top-right corner)
            var addButton = new Button
            {
                Width = 24,
                Height = 24,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 6, 6, 0),
                Content = new FontIcon
                {
                    Glyph = "",  // Segoe MDL2 Assets Add icon
                    FontSize = 10,
                },
            };
            addButton.Click += (_, _) => OnAddWidgetClick(widgetId);

            // Wrap content + button in a Grid
            var containerGrid = new Grid
            {
                Children = { content, addButton },
            };

            // Create a new GridWidget with the wrapped content
            var editorWidget = new WidgetFramework.GridWidget(widgetId, 1, 1, false)
            {
                Content = containerGrid,
                MinColumnSpan = 1,
                MaxColumnSpan = 1,
                MinRowSpan = 1,
                MaxRowSpan = 1,
            };

            gridWidgets.Add(editorWidget);
        }

        // Set widgets in the grid host (3 columns for editor window)
        HiddenWidgetsHost.Columns = 3;
        HiddenWidgetsHost.SetWidgets(gridWidgets);
    }

    /// <summary>
    /// Refresh the hidden widgets display (called when widgets are hidden from home page).
    /// </summary>
    public void RefreshHiddenWidgets()
    {
        LoadHiddenWidgets();
    }


    /// <summary>
    /// Called when "+" button is clicked on a hidden widget.
    /// Moves widget from hidden to visible list.
    /// </summary>
    private async void OnAddWidgetClick(string widgetId)
    {
        // Show widget in service
        App.WidgetService.ShowWidget(widgetId);
        await App.WidgetService.SaveLayoutAsync();

        // Refresh home page to show the new widget
        App.MainWindow?.RefreshHomePage();

        // Reload hidden widgets in editor
        LoadHiddenWidgets();
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

    /// <summary>
    /// Compute window height in DIPs from row count.
    /// Uses the same formula as MainWindow so both windows have the same
    /// total height and the widget grid areas align identically.
    /// </summary>
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

        // Set window size: 3 columns wide, minimum 3 rows tall
        var columns = App.WidgetService.Columns > 0 ? 3 : 3; // Always 3 columns for hidden widgets
        var columnWidth = App.WidgetService.ColumnWidth;
        const int minEditorRows = 3;
        var windowHeightRows = Math.Max(minEditorRows, App.WidgetService.WindowHeightRows);
        var width = GetScaledWindowWidth(3, columnWidth);
        var height = GetScaledWindowHeight(windowHeightRows);
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
    /// Bottom edges are aligned so the editor sits at the same distance from the
    /// screen bottom as the main window. Places to the left of main window, or
    /// above/below if too close to left screen edge.
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
        var mainHeight = mainAppWindow.Size.Height;

        var hiddenWidth = this.AppWindow.Size.Width;
        var hiddenHeight = this.AppWindow.Size.Height;

        // Align bottom edges: editor bottom at same Y as main window bottom
        var mainBottomY = mainPosY + mainHeight;
        var hiddenPosX = mainPosX - hiddenWidth - WindowMargin;
        var hiddenPosY = mainBottomY - hiddenHeight;

        // Check if too close to left edge (less than 50px from left edge)
        if (hiddenPosX < workArea.X + 50)
        {
            // Position above main window instead
            hiddenPosX = mainPosX;
            hiddenPosY = mainPosY - hiddenHeight - WindowMargin;

            // If also too close to top, position below main window
            if (hiddenPosY < workArea.Y + 50)
            {
                hiddenPosY = mainBottomY + WindowMargin;
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

        // Load hidden widgets when window is shown (not in constructor)
        LoadHiddenWidgets();

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
