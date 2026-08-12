using Microsoft.UI.Xaml.Controls;
using XmaX.Services;

namespace XmaX.Widgets;

/// <summary>
/// VRAM metric tile showing GPU memory usage.
/// </summary>
public sealed partial class VramTile : UserControl, IHomeWidget
{
    private readonly MetricsService _metricsService;

    public string WidgetId => "vram";
    public WidgetConfig Config => WidgetConfig.TransparentTile;
    public object Control => this;

    public VramTile()
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
        var gpu = _metricsService.Metrics.Gpu;
        if (gpu.VramUsedMb.HasValue && gpu.VramTotalMb.HasValue)
        {
            double vramUsedGb = gpu.VramUsedMb.Value / 1024.0;
            double vramTotalGb = gpu.VramTotalMb.Value / 1024.0;
            UsageText.Text = Loc.F("metrics.vram_format", $"{vramUsedGb:F1}", $"{vramTotalGb:F1}");
        }
        else
        {
            UsageText.Text = Loc.Metrics_NoData;
        }
    }
}
