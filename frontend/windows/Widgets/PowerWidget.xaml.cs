using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using XmaX.Services;

namespace XmaX.Widgets;

/// <summary>
/// Power widget showing power source status, battery, and adaptive TDP ceiling slider.
/// This widget spans the full row in the home page grid.
/// </summary>
public sealed partial class PowerWidget : UserControl, IHomeWidget
{
    private readonly MetricsService _metricsService;
    private readonly AutoTuneService _autoTuneService;

    // Prevents slider change from re-triggering when syncing from service state
    private bool _suppressSliderChange;

    public string WidgetId => "power";
    public object Control => this;

    public PowerWidget()
    {
        this.InitializeComponent();
        _suppressSliderChange = true;
        TdpCeilingSlider.Minimum = 6;
        TdpCeilingSlider.Maximum = 120;
        _suppressSliderChange = false;
        TitleText.Text = Loc.Title_Power;
        TdpCeilingLabel.Text = Loc.Form_TdpCeiling;
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
            DispatcherQueue.TryEnqueue(UpdatePowerStatus);
        }
    }

    private void OnAutoTuneChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AutoTuneService.State) ||
            e.PropertyName == nameof(AutoTuneService.EffectiveTdpMaxW))
        {
            DispatcherQueue.TryEnqueue(UpdateTdpSlider);
        }
    }

    private void UpdateDisplay()
    {
        UpdatePowerStatus();
        UpdateTdpSlider();
    }

    private void UpdatePowerStatus()
    {
        var power = _metricsService.Metrics.Power;
        PowerSourceLabel.Text = !string.IsNullOrEmpty(power.Label) ? power.Label : power.Mode;

        if (power.BatteryPercent.HasValue)
        {
            BatteryText.Text = Loc.F("widget.charge_format", power.BatteryPercent.Value);
        }
        else
        {
            BatteryText.Text = "";
        }
    }

    private void UpdateTdpSlider()
    {
        _suppressSliderChange = true;
        var effectiveTdp = _autoTuneService.EffectiveTdpMaxW;
        TdpCeilingSlider.Value = effectiveTdp > 0 ? effectiveTdp : 55;
        TdpCeilingValue.Text = Loc.F("widget.tdp_format", effectiveTdp);
        _suppressSliderChange = false;
    }

    private void OnTdpCeilingChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSliderChange) return;

        var tdpW = (int)e.NewValue;
        TdpCeilingValue.Text = Loc.F("widget.tdp_format", tdpW);

        var state = _autoTuneService.State;
        _ = _autoTuneService.SetAutoTuneAsync(
            state.Tuning,
            state.TargetTempC,
            tdpW,
            state.FanMaxPercent);
    }
}
