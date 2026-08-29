using XmaX.Services;
using XmaX.WidgetFramework;

namespace XmaX.Widgets;

/// <summary>
/// VRAM metric tile showing GPU memory usage.
/// </summary>
public sealed partial class VramTile : WidgetBase
{
    public VramTile(IWidgetApi api) : base(api, "vram")
    {
        this.InitializeComponent();

        // Subscribe to metrics updates
        Metrics.PropertyChanged += OnMetricsChanged;
        Refresh();
    }

    private void OnMetricsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MetricsService.Metrics))
        {
            ScheduleRefresh();
        }
    }

    public override void Refresh()
    {
        var gpu = Metrics.Metrics.Gpu;
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
