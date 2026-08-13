using Microsoft.UI.Xaml.Controls;
using XmaX.Services;

namespace XmaX.Widgets;

/// <summary>
/// Power state tile showing current power source and TDP limit.
/// </summary>
public sealed partial class PowerWidget : UserControl, IHomeWidget
{
    private readonly MetricsService _metricsService;
    private readonly AutoTuneService _autoTuneService;

    public string WidgetId => "power";
    public WidgetConfig Config => WidgetConfig.TransparentTile;  // 1x1 transparent tile
    public object Control => this;
    public string? Title => null;
    public int GetRequiredRows(int availableColumns) => Config.Rows;

    public PowerWidget()
    {
        this.InitializeComponent();
        _metricsService = App.MetricsService;
        _autoTuneService = App.AutoTuneService;
        _metricsService.PropertyChanged += OnMetricsChanged;
        _autoTuneService.PropertyChanged += OnAutoTuneChanged;
        UpdateDisplay();
    }

    private void OnMetricsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MetricsService.Metrics))
        {
            DispatcherQueue.TryEnqueue(UpdateDisplay);
        }
    }

    private void OnAutoTuneChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AutoTuneService.EffectiveTdpMaxW))
        {
            DispatcherQueue.TryEnqueue(UpdateDisplay);
        }
    }

    private void UpdateDisplay()
    {
        var power = _metricsService.Metrics.Power;

        // Map power mode to locale string
        StateText.Text = power.Mode switch
        {
            "battery" => Loc.Power_Battery,
            "usb_c_slow" => Loc.Power_UsbCSlow,
            "usb_c_fast" => Loc.Power_UsbCFast,
            "dc_in" => Loc.Power_DcIn,
            _ => power.Label ?? power.Mode,
        };

        // Show TDP limit
        var tdp = _autoTuneService.EffectiveTdpMaxW;
        TdpText.Text = tdp > 0 ? Loc.F("metrics.power_format", tdp) : "";
    }
}
