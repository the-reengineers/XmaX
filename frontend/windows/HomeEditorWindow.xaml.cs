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

    // Add button constants
    private const double AddButtonSize = 28.0;
    private const double AddButtonIconFontSizeRatio = 0.55;

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

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    private const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

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
            // Uses accent color background with white foreground for contrast
            var accentBrush = Application.Current.Resources["SystemControlHighlightAccentBrush"] as Microsoft.UI.Xaml.Media.Brush;
            var addButton = new Button
            {
                Width = AddButtonSize,
                Height = AddButtonSize,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(AddButtonSize / 2),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 6, 6, 0),
                Background = accentBrush,
                Content = new FontIcon
                {
                    Glyph = "",  // Segoe MDL2 Assets Add icon
                    FontSize = AddButtonSize * AddButtonIconFontSizeRatio,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
                },
            };
            addButton.Click += (_, _) => OnAddWidgetClick(widgetId);

            // Wrap content + button in a Grid
            var containerGrid = new Grid
            {
                Children = { content, addButton },
            };

            // Update the widget's content to include the add button
            // The widget already has proper min/max constraints from the factory
            widget.Content = containerGrid;

            gridWidgets.Add(widget);
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
        const int columnSpacing = 8;
        const int gridPadding = 12;
        var baseWidth = (columns * columnWidth)
                      + ((columns - 1) * columnSpacing)
                      + (2 * gridPadding);

        var scale = GetDpiScale();
        return (int)(baseWidth * scale);
    }

    /// <summary>
    /// Convert a pixel width to the nearest column count.
    /// Inverse of GetScaledWindowWidth.
    /// </summary>
    private int WidthToColumns(int widthPixels)
    {
        var scale = GetDpiScale();
        var widthDIPs = widthPixels / scale;

        // From GetScaledWindowWidth:
        // width = columns * columnWidth + (columns - 1) * spacing + 2 * padding
        //       = columns * (columnWidth + spacing) - spacing + 2 * padding
        // columns = (width + spacing - 2 * padding) / (columnWidth + spacing)
        const double columnSpacing = 8.0;
        const double gridPadding = 12.0;
        var columnWidth = (double)App.WidgetService.ColumnWidth;

        var rawColumns = (widthDIPs + columnSpacing - 2 * gridPadding) / (columnWidth + columnSpacing);
        var columns = (int)Math.Round(rawColumns);
        return Math.Clamp(columns, Services.WidgetService.MinColumns, Services.WidgetService.MaxColumns);
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

    /// <summary>
    /// Convert a pixel height to the maximum number of whole rows that fit.
    /// Inverse of GetScaledWindowHeight. Floors to nearest whole row.
    /// </summary>
    private int HeightToRows(int heightPixels)
    {
        var scale = GetDpiScale();
        var heightDIPs = heightPixels / scale;

        // From ComputeWindowHeightDIPs:
        // height = bottomBarHeight + 2*gridPadding + rows*cellHeight + (rows-1)*spacing
        //        = 50 + 24 + rows*cellHeight + (rows-1)*8
        //        = 66 + rows*(cellHeight + 8)
        // rows = (height - 66) / (cellHeight + 8)
        const double bottomBarHeight = 50.0;
        const double gridPadding = 12.0;
        const double spacing = 8.0;
        var cellHeight = (double)App.WidgetService.ColumnWidth;

        var fixedHeight = bottomBarHeight + 2 * gridPadding;
        var rowUnit = cellHeight + spacing;
        var rows = (heightDIPs - fixedHeight + spacing) / rowUnit;
        return Math.Max(1, (int)Math.Floor(rows));
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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

    private const uint SPI_GETWORKAREA = 0x0030;

    /// <summary>
    /// Position this window relative to the main window.
    /// Resizes editor to match main window height, then aligns bottom edges.
    /// Places to the left of main window, or above/below if too close to left screen edge.
    /// </summary>
    public void PositionRelativeToMainWindow()
    {
        if (App.MainWindow == null) return;

        var mainWindow = App.MainWindow;

        // Get primary monitor's work area directly from Win32 API
        var workAreaRect = new RECT();
        SystemParametersInfo(SPI_GETWORKAREA, 0, ref workAreaRect, 0);
        var workArea = new RectInt32(workAreaRect.Left, workAreaRect.Top,
            workAreaRect.Right - workAreaRect.Left,
            workAreaRect.Bottom - workAreaRect.Top);

        var mainAppWindow = mainWindow.AppWindow;
        var mainPosX = mainAppWindow.Position.X;
        var mainPosY = mainAppWindow.Position.Y;
        var mainHeight = mainAppWindow.Size.Height;

        // Calculate editor width to match main window's column count
        var mainColumns = App.WidgetService.Columns;
        var columnWidth = App.WidgetService.ColumnWidth;
        var hiddenWidth = GetScaledWindowWidth(mainColumns, columnWidth);

        // Update grid to match main window columns
        HiddenWidgetsHost.Columns = mainColumns;
        HiddenWidgetsHost.LayoutWidgets(animate: false);

        // Resize editor to match main window height, with a minimum of 3 rows
        const int minEditorRows = 3;
        var minHeight = GetScaledWindowHeight(minEditorRows);
        var editorHeight = Math.Max(minHeight, mainHeight);
        this.AppWindow.Resize(new SizeInt32(hiddenWidth, editorHeight));
        var hiddenHeight = editorHeight;

        // Align bottom edges: editor bottom at same Y as main window bottom
        var mainBottomY = mainPosY + mainHeight;
        var hiddenPosX = mainPosX - hiddenWidth - WindowMargin;
        var hiddenPosY = mainBottomY - hiddenHeight;

        // Check if there's not enough space on the left for the editor window
        // (editor would be cut off or too close to left edge)
        var minMargin = 20;
        if (hiddenPosX < workArea.X + minMargin)
        {
            // Position above main window, matching main window width
            var mainWidth = mainAppWindow.Size.Width;
            hiddenWidth = mainWidth;
            hiddenPosX = mainPosX;

            // Cap height at 3 rows when above main window
            const int maxEditorRows = 3;
            hiddenHeight = GetScaledWindowHeight(maxEditorRows);

            hiddenPosY = mainPosY - hiddenHeight - WindowMargin;

            // Calculate column count based on new width and update grid
            var columns = WidthToColumns(hiddenWidth);
            HiddenWidgetsHost.Columns = columns;
            HiddenWidgetsHost.LayoutWidgets(animate: false);

            // Resize editor to match main window width, max 3 rows height
            this.AppWindow.Resize(new SizeInt32(hiddenWidth, hiddenHeight));

            // If editor's top edge would be above the work area, shrink height to fit
            if (hiddenPosY < workArea.Y)
            {
                // Calculate available height above main window
                var availableHeight = mainPosY - workArea.Y - WindowMargin;

                // Floor to nearest whole row count (minimum 1 row)
                var rows = HeightToRows(availableHeight);
                rows = Math.Max(1, rows);

                // Resize editor to fit
                hiddenHeight = GetScaledWindowHeight(rows);
                this.AppWindow.Resize(new SizeInt32(hiddenWidth, hiddenHeight));

                // Position at top of work area
                hiddenPosY = workArea.Y;
            }
        }
        // else: Positioned to the left - columns already set to mainColumns above

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
