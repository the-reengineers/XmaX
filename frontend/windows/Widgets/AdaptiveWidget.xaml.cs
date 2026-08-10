using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using XmaX.Services;

namespace XmaX.Widgets;

/// <summary>
/// Adaptive controller widget with 3-button tuning preset selector and TDP ceiling slider.
/// </summary>
public sealed partial class AdaptiveWidget : UserControl, IHomeWidget
{
    private readonly AutoTuneService _autoTuneService;

    // Prevents slider change from re-triggering when syncing from service state
    private bool _suppressSliderChange;

    public string WidgetId => "adaptive";
    public object Control => this;

    public AdaptiveWidget()
    {
        this.InitializeComponent();
        _suppressSliderChange = true;
        TdpSlider.Minimum = 6;
        TdpSlider.Maximum = 120;
        _suppressSliderChange = false;
        TitleText.Text = Loc.Title_Adaptive;
        BtnSilent.Content = Loc.Button_Silent;
        BtnDefault.Content = Loc.Button_Default;
        BtnPerformance.Content = Loc.Button_Performance;
        TdpCeilingLabel.Text = Loc.Form_TdpCeiling;
        _autoTuneService = App.AutoTuneService;
        _autoTuneService.PropertyChanged += OnAutoTuneChanged;
        UpdateDisplay();
    }

    private void OnAutoTuneChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AutoTuneService.State) ||
            e.PropertyName == nameof(AutoTuneService.IsActive) ||
            e.PropertyName == nameof(AutoTuneService.Tuning) ||
            e.PropertyName == nameof(AutoTuneService.EffectiveTdpMaxW))
        {
            DispatcherQueue.TryEnqueue(UpdateDisplay);
        }
    }

    private void UpdateDisplay()
    {
        var state = _autoTuneService.State;
        var isActive = _autoTuneService.IsActive;
        var tuning = _autoTuneService.Tuning;

        // Highlight active tuning preset button
        BtnSilent.Style = tuning == "silent" && isActive
            ? (Microsoft.UI.Xaml.Style)Application.Current.Resources["AccentButtonStyle"]
            : (Microsoft.UI.Xaml.Style)Application.Current.Resources["DefaultButtonStyle"];
        BtnDefault.Style = tuning == "default" && isActive
            ? (Microsoft.UI.Xaml.Style)Application.Current.Resources["AccentButtonStyle"]
            : (Microsoft.UI.Xaml.Style)Application.Current.Resources["DefaultButtonStyle"];
        BtnPerformance.Style = tuning == "performance" && isActive
            ? (Microsoft.UI.Xaml.Style)Application.Current.Resources["AccentButtonStyle"]
            : (Microsoft.UI.Xaml.Style)Application.Current.Resources["DefaultButtonStyle"];

        // Sync TDP slider
        _suppressSliderChange = true;
        TdpSlider.Value = state.TdpMaxW;
        TdpValue.Text = Loc.F("widget.tdp_format", state.EffectiveTdpMaxW);
        _suppressSliderChange = false;
    }

    private void OnTuningClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tuning)
        {
            var state = _autoTuneService.State;
            _ = _autoTuneService.SetAutoTuneAsync(
                tuning,
                state.TargetTempC,
                state.TdpMaxW,
                state.FanMaxPercent);
        }
    }

    private void OnTdpSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSliderChange) return;

        var tdpW = (int)e.NewValue;
        TdpValue.Text = Loc.F("widget.tdp_format", tdpW);

        var state = _autoTuneService.State;
        _ = _autoTuneService.SetAutoTuneAsync(
            state.Tuning,
            state.TargetTempC,
            tdpW,
            state.FanMaxPercent);
    }
}
