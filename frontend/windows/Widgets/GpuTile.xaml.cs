using Microsoft.UI.Xaml.Controls;
using XmaX.Services;

namespace XmaX.Widgets;

/// <summary>
/// GPU metric tile showing temperature, utilization, and power.
/// </summary>
public sealed partial class GpuTile : UserControl, IHomeWidget
{
    private readonly MetricsService _metricsService;

    public string WidgetId => "gpu";
    public WidgetConfig Config => WidgetConfig.TransparentTile;
    public object Control => this;
    public string? Title => null;
    public int GetRequiredRows(int availableColumns) => Config.Rows;

    public GpuTile()
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
        TempText.Text = gpu.TempC.HasValue ? Loc.F("metrics.temp_format", gpu.TempC.Value) : Loc.Metrics_NoData;
        UtilText.Text = Loc.F("metrics.util_format", $"{gpu.UtilPercent:F0}");
        PowerText.Text = gpu.PowerW.HasValue ? Loc.F("metrics.power_format", gpu.PowerW.Value) : Loc.Metrics_NoData;
    }
}
