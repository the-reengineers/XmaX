using Microsoft.UI.Xaml.Controls;
using XmaX.Services;

namespace XmaX.Widgets;

/// <summary>
/// RAM metric tile showing memory usage and load percentage.
/// </summary>
public sealed partial class RamTile : UserControl, IHomeWidget
{
    private readonly MetricsService _metricsService;

    public string WidgetId => "ram";
    public WidgetConfig Config => WidgetConfig.TransparentTile;
    public object Control => this;

    public RamTile()
    {
        this.InitializeComponent();
        _metricsService = App.MetricsService;
        _metricsService.PropertyChanged += OnMetricsChanged;
        UpdateDisplay();
    }

    private void OnMetricsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MetricsService.Metrics))
        {
            DispatcherQueue.TryEnqueue(UpdateDisplay);
        }
    }

    private void UpdateDisplay()
    {
        var ram = _metricsService.Metrics.Ram;
        UsageText.Text = Loc.F("metrics.ram_format", $"{ram.UsedGb:F1}", $"{ram.TotalGb:F0}");
        LoadText.Text = Loc.F("metrics.load_format", $"{ram.LoadPercent:F0}");
    }
}
