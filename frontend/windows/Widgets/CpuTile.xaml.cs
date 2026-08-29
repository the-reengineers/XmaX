using XmaX.Services;
using XmaX.WidgetFramework;

namespace XmaX.Widgets;

/// <summary>
/// CPU metric tile showing temperature, utilization, and power.
/// </summary>
public sealed partial class CpuTile : WidgetBase
{
    public CpuTile(IWidgetApi api) : base(api, "cpu")
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
        var cpu = Metrics.Metrics.Cpu;
        TempText.Text = cpu.TempC.HasValue ? Loc.F("metrics.temp_format", cpu.TempC.Value) : Loc.Metrics_NoData;
        UtilText.Text = Loc.F("metrics.util_format", $"{cpu.UtilPercent:F0}");
        PowerText.Text = cpu.PackageWatts.HasValue ? Loc.F("metrics.power_format", cpu.PackageWatts.Value) : Loc.Metrics_NoData;
    }
}
