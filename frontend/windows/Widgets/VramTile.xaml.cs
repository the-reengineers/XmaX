using Microsoft.UI.Xaml.Controls;
using XmaX.Services;

namespace XmaX.Widgets;

/// <summary>
/// VRAM metric tile showing GPU memory usage.
/// </summary>
public sealed partial class VramTile : UserControl
{
    private readonly MetricsService _metricsService;

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
        if (gpu.VramUsedBytes.HasValue && gpu.VramTotalBytes.HasValue)
        {
            double vramUsedGb = gpu.VramUsedBytes.Value / (1024.0 * 1024.0 * 1024.0);
            double vramTotalGb = gpu.VramTotalBytes.Value / (1024.0 * 1024.0 * 1024.0);
            UsageText.Text = Loc.F("metrics.vram_format", $"{vramUsedGb:F1}", $"{vramTotalGb:F1}");
        }
        else
        {
            UsageText.Text = Loc.Metrics_NoData;
        }
    }
}
