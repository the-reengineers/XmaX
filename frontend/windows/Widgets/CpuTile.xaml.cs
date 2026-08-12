using Microsoft.UI.Xaml.Controls;
using XmaX.Services;

namespace XmaX.Widgets;

/// <summary>
/// CPU metric tile showing temperature, utilization, and power.
/// </summary>
public sealed partial class CpuTile : UserControl, IHomeWidget
{
    private readonly MetricsService _metricsService;

    public string WidgetId => "cpu";
    public WidgetConfig Config => WidgetConfig.TransparentTile;
    public object Control => this;

    public CpuTile()
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
        var cpu = _metricsService.Metrics.Cpu;
        TempText.Text = cpu.TempC.HasValue ? Loc.F("metrics.temp_format", cpu.TempC.Value) : Loc.Metrics_NoData;
        UtilText.Text = Loc.F("metrics.util_format", $"{cpu.UtilPercent:F0}");
        PowerText.Text = cpu.PackageWatts.HasValue ? Loc.F("metrics.power_format", cpu.PackageWatts.Value) : Loc.Metrics_NoData;
    }
}
