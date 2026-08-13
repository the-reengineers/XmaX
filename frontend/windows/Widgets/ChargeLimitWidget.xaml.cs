using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using XmaX.Services;

namespace XmaX.Widgets;

/// <summary>
/// Charge limit widget - entire widget is clickable to cycle charge limits.
/// Card background applied by HomePage (IsInteractiveCard = true).
/// </summary>
public sealed partial class ChargeLimitWidget : UserControl, IHomeWidget
{
    /// <summary>Charge limit steps in percent, cycling order.</summary>
    private static readonly int[] ChargeSteps = { 75, 80, 85, 90, 95, 100 };

    private readonly MetricsService _metricsService;
    private readonly PipeClient _pipe;

    public string WidgetId => "charge_limit";
    public WidgetConfig Config => WidgetConfig.TransparentTile;  // 1x1, transparent (widget handles its own card background)
    public object Control => this;
    public string? Title => null;
    public int GetRequiredRows(int availableColumns) => Config.Rows;

    public ChargeLimitWidget()
    {
        this.InitializeComponent();
        _metricsService = App.MetricsService;
        _pipe = App.Pipe;
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
        var limit = _metricsService.Metrics.Power.ChargeLimitPercent;
        ChargeLimitText.Text = limit.HasValue ? Loc.F("widget.charge_format", limit.Value) : Loc.Metrics_NoData;
    }

    private void OnWidgetTapped(object sender, TappedRoutedEventArgs e)
    {
        _ = CycleChargeLimitAsync();
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        // Hover effect - slightly brighter
        Opacity = 0.8;
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        Opacity = 1.0;
    }

    private async Task CycleChargeLimitAsync()
    {
        var current = _metricsService.Metrics.Power.ChargeLimitPercent ?? 100;
        var next = GetNextLimit(current);

        try
        {
            var payload = new System.Text.Json.Nodes.JsonObject { ["percent"] = next };
            await _pipe.SendCommandAsync("set_charge_limit", payload);
        }
        catch
        {
            // Command failed (e.g., persist=false) — UI will revert on next metrics update
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
