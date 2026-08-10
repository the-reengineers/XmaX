using Microsoft.UI.Xaml.Controls;
using XmaX.Services;

namespace XmaX.Widgets;

/// <summary>
/// Metrics widget showing CPU, GPU, and RAM stats in 3 tiles.
/// </summary>
public sealed partial class MetricsWidget : UserControl, IHomeWidget
{
    private readonly MetricsService _metricsService;

    public string WidgetId => "metrics";
    public object Control => this;

    public MetricsWidget()
    {
        this.InitializeComponent();
        TitleText.Text = Loc.Title_Metrics;
        CpuLabel.Text = Loc.Metrics_Cpu;
        GpuLabel.Text = Loc.Metrics_Gpu;
        RamLabel.Text = Loc.Metrics_Ram;
        _metricsService = App.MetricsService;
        _metricsService.PropertyChanged += OnMetricsChanged;
        UpdateDisplay();
    }

    private void OnMetricsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MetricsService.Metrics))
        {
            // Marshal to UI thread
            DispatcherQueue.TryEnqueue(UpdateDisplay);
        }
    }

    private void UpdateDisplay()
    {
        var m = _metricsService.Metrics;

        // CPU
        CpuTemp.Text = m.Cpu.TempC.HasValue ? Loc.F("metrics.temp_format", m.Cpu.TempC.Value) : Loc.Metrics_NoData;
        CpuUtil.Text = Loc.F("metrics.util_format", $"{m.Cpu.UtilPercent:F0}");
        CpuPower.Text = m.Cpu.PackageWatts.HasValue ? Loc.F("metrics.power_format", m.Cpu.PackageWatts.Value) : Loc.Metrics_NoData;

        // GPU
        GpuTemp.Text = m.Gpu.TempC.HasValue ? Loc.F("metrics.temp_format", m.Gpu.TempC.Value) : Loc.Metrics_NoData;
        GpuUtil.Text = Loc.F("metrics.util_format", $"{m.Gpu.UtilPercent:F0}");
        GpuPower.Text = m.Gpu.PowerW.HasValue ? Loc.F("metrics.power_format", m.Gpu.PowerW.Value) : Loc.Metrics_NoData;

        // RAM
        RamUsage.Text = Loc.F("metrics.ram_format", $"{m.Ram.UsedGb:F1}", $"{m.Ram.TotalGb:F0}");
        RamLoad.Text = Loc.F("metrics.load_format", $"{m.Ram.LoadPercent:F0}");
    }
}
