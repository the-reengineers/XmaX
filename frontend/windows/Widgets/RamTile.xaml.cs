using Microsoft.UI.Xaml.Controls;
using XmaX.Services;

namespace XmaX.Widgets;

/// <summary>
/// RAM metric tile showing memory usage and load percentage.
/// </summary>
public sealed partial class RamTile : UserControl
{
    private readonly MetricsService _metricsService;

    public RamTile()
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
        var ram = _metricsService.Metrics.Ram;
        double usedGb = ram.UsedBytes / (1024.0 * 1024.0 * 1024.0);
        double totalGb = ram.TotalBytes / (1024.0 * 1024.0 * 1024.0);
        UsageText.Text = Loc.F("metrics.ram_format", $"{usedGb:F1}", $"{totalGb:F0}");
        LoadText.Text = Loc.F("metrics.load_format", $"{ram.LoadPercent:F0}");
    }
}
