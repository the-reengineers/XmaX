using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using XmaX.WidgetFramework;

namespace XmaX.Pages;

/// <summary>
/// Widget layout playground sub-page — testbed for the v2 widget framework with drag-reflow.
/// Displays colored placeholder widgets in a 3-column grid.
/// Navigated from Settings page drill-down.
/// </summary>
public sealed partial class WidgetPlaygroundSubPage : Page
{
    private static readonly Random _random = new();

    public WidgetPlaygroundSubPage()
    {
        this.InitializeComponent();

        this.Loaded += OnPageLoaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        var widgets = new List<GridWidget>
        {
            CreatePlaceholder("w1", 1, 1, false, Colors.Red),
            CreatePlaceholder("w2", 1, 1, false, Colors.Green),
            CreatePlaceholder("w3", 1, 1, false, Colors.Blue),
            CreatePlaceholder("w4", 3, 1, true,  Colors.Gold),
            CreatePlaceholder("w5", 3, 2, true,  Colors.Purple),
            CreatePlaceholder("w6", 1, 2, false, Colors.Orange),
            CreatePlaceholder("w7", 1, 1, false, Colors.Cyan),
        };

        GridHost.Columns = 3;
        GridHost.SetWidgets(widgets);

        var controller = new DragReflowController(GridHost);
        GridHost.SetDragController(controller);
    }

    private static GridWidget CreatePlaceholder(
        string id, int colSpan, int rowSpan, bool alwaysFillRow, Windows.UI.Color bgColor)
    {
        var letter = (char)('A' + _random.Next(26));

        var textBlock = new TextBlock
        {
            Text = letter.ToString(),
            FontSize = 28,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = new SolidColorBrush(Colors.White),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var content = new Grid
        {
            Background = new SolidColorBrush(bgColor),
            Children = { textBlock },
        };

        var widget = new GridWidget(id, colSpan, rowSpan, alwaysFillRow);
        widget.Content = content;
        return widget;
    }
}
