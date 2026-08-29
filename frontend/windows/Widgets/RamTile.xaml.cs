using XmaX.Services;
using XmaX.WidgetFramework;

namespace XmaX.Widgets;

/// <summary>
/// RAM metric tile showing memory usage and load percentage.
/// </summary>
public sealed partial class RamTile : WidgetBase
{
    public RamTile(IWidgetApi api) : base(api, "ram")
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
        var ram = Metrics.Metrics.Ram;
        double usedGb = ram.UsedBytes / (1024.0 * 1024.0 * 1024.0);
        double totalGb = ram.TotalBytes / (1024.0 * 1024.0 * 1024.0);
        UsageText.Text = Loc.F("metrics.ram_format", $"{usedGb:F1}", $"{totalGb:F0}");
        LoadText.Text = Loc.F("metrics.load_format", $"{ram.LoadPercent:F0}");
    }
}
