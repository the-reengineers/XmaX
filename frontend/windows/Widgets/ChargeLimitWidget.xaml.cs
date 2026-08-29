using Microsoft.UI.Xaml.Input;
using XmaX.Services;
using XmaX.WidgetFramework;

namespace XmaX.Widgets;

/// <summary>
/// Charge limit widget - entire widget is clickable to cycle charge limits.
/// Card background applied by HomePage (IsInteractiveCard = true).
/// </summary>
public sealed partial class ChargeLimitWidget : WidgetBase
{
    /// <summary>Charge limit steps in percent, cycling order.</summary>
    private static readonly int[] ChargeSteps = { 75, 80, 85, 90, 95, 100 };

    public ChargeLimitWidget(IWidgetApi api) : base(api, "charge_limit")
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
        var limit = Metrics.Metrics.Power.ChargeLimitPercent;
        ChargeLimitText.Text = limit.HasValue ? Loc.F("widget.charge_format", limit.Value) : Loc.Metrics_NoData;
        ChargeGauge.Value = limit ?? 0;
    }

    private void OnWidgetTapped(object sender, TappedRoutedEventArgs e)
    {
        _ = CycleChargeLimitAsync();
    }

    private void OnPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        // Hover effect - slightly brighter
        Opacity = 0.8;
    }

    private void OnPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        Opacity = 1.0;
    }

    private async Task CycleChargeLimitAsync()
    {
        var current = Metrics.Metrics.Power.ChargeLimitPercent ?? 100;
        var next = GetNextLimit(current);

        var result = await Api.SetChargeLimitAsync(next);
        if (!result.IsSuccess)
        {
            // Command failed (e.g., persist=false) — UI will revert on next metrics update
            // Could show error toast here in the future
        }
    }

    /// <summary>
    /// Get the next charge limit in the cycle, wrapping around at 100.
    /// </summary>
    private static int GetNextLimit(int current)
    {
        for (int i = 0; i < ChargeSteps.Length; i++)
        {
            if (ChargeSteps[i] >= current)
            {
                // Return the next step, wrapping around
                return ChargeSteps[(i + 1) % ChargeSteps.Length];
            }
        }
        // current > 100 (shouldn't happen), wrap to first
        return ChargeSteps[0];
    }
}
