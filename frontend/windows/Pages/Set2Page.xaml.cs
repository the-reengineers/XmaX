using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using XmaX.WidgetFramework;

namespace XmaX.Pages;

/// <summary>
/// Set2 page — testbed for the v2 widget framework with drag-reflow.
/// Displays 7 colored placeholder widgets in a 3-column grid.
/// </summary>
public sealed partial class Set2Page : Page
{
    private static readonly Random _random = new();

    public Set2Page()
    {
        this.InitializeComponent();
        this.Loaded += OnPageLoaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        // Build placeholder widgets
        var widgets = new List<GridWidget>
        {
            CreatePlaceholder("w1", 1, 1, false, Colors.Red),
            CreatePlaceholder("w2", 1, 1, false, Colors.Green),
            CreatePlaceholder("w3", 1, 1, false, Colors.Blue),
            CreatePlaceholder("w4", 3, 1, true,  Colors.Gold),       // Yellow
            CreatePlaceholder("w5", 3, 2, true,  Colors.Purple),
            CreatePlaceholder("w6", 1, 2, false, Colors.Orange),
            CreatePlaceholder("w7", 1, 1, false, Colors.Cyan),
        };

        GridHost.Columns = 3;
        GridHost.SetWidgets(widgets);

        // Attach drag controller
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
