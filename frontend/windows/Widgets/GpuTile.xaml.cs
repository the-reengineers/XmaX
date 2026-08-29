using XmaX.Services;
using XmaX.WidgetFramework;

namespace XmaX.Widgets;

/// <summary>
/// GPU metric tile showing temperature, utilization, and power.
/// </summary>
public sealed partial class GpuTile : WidgetBase
{
    public GpuTile(IWidgetApi api) : base(api, "gpu")
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
        TempText.Text = gpu.TempC.HasValue ? Loc.F("metrics.temp_format", gpu.TempC.Value) : Loc.Metrics_NoData;
        UtilText.Text = Loc.F("metrics.util_format", $"{gpu.UtilPercent:F0}");
        PowerText.Text = gpu.PowerW.HasValue ? Loc.F("metrics.power_format", gpu.PowerW.Value) : Loc.Metrics_NoData;
    }
}
