using Microsoft.UI.Xaml.Controls;
using XmaX.Models;
using XmaX.Services;

namespace XmaX.Widgets;

/// <summary>
/// Power state tile showing current power source and TDP limit.
/// Displays a Material icon below the power state text.
/// </summary>
public sealed partial class PowerWidget : UserControl, IHomeWidget
{
    private readonly MetricsService _metricsService;
    private readonly AutoTuneService _autoTuneService;

    // Glyph codes for Material Design Icons
    private static readonly string GlyphUnknown = char.ConvertFromUtf32(0xF0091);
    private static readonly string GlyphBattery0 = char.ConvertFromUtf32(0xF008E);
    private static readonly string GlyphBattery10 = char.ConvertFromUtf32(0xF007A);
    private static readonly string GlyphBattery20 = char.ConvertFromUtf32(0xF007B);
    private static readonly string GlyphBattery30 = char.ConvertFromUtf32(0xF007C);
    private static readonly string GlyphBattery40 = char.ConvertFromUtf32(0xF007D);
    private static readonly string GlyphBattery50 = char.ConvertFromUtf32(0xF007E);
    private static readonly string GlyphBattery60 = char.ConvertFromUtf32(0xF007F);
    private static readonly string GlyphBattery70 = char.ConvertFromUtf32(0xF0080);
    private static readonly string GlyphBattery80 = char.ConvertFromUtf32(0xF0081);
    private static readonly string GlyphBattery90 = char.ConvertFromUtf32(0xF0082);
    private static readonly string GlyphBattery100 = char.ConvertFromUtf32(0xF0079);
    private static readonly string GlyphUsbC = char.ConvertFromUtf32(0xF1CBF);
    private static readonly string GlyphFlash = char.ConvertFromUtf32(0xF140B);
    private static readonly string GlyphDcIn = char.ConvertFromUtf32(0xF1C3B);

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

        // Update icon based on power state
        UpdateIcon(power);
    }

    private void UpdateIcon(PowerStatus power)
    {
        SecondaryIcon.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;

        switch (power.Mode)
        {
            case "battery":
                UpdateBatteryIcon(power.BatteryPercent);
                break;
            case "usb_c_slow":
                PrimaryIcon.Text = GlyphUsbC;
                break;
            case "usb_c_fast":
                PrimaryIcon.Text = GlyphUsbC;
                SecondaryIcon.Text = GlyphFlash;
                SecondaryIcon.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
                break;
            case "dc_in":
                PrimaryIcon.Text = GlyphDcIn;
                break;
            default:
                PrimaryIcon.Text = GlyphUnknown;
                break;
        }
    }

    private void UpdateBatteryIcon(int? batteryPercent)
    {
        if (!batteryPercent.HasValue)
        {
            PrimaryIcon.Text = GlyphBattery0;
            return;
        }

        // Floor to nearest 10% increment
        var level = (batteryPercent.Value / 10) * 10;
        PrimaryIcon.Text = level switch
        {
            >= 100 => GlyphBattery100,
            90 => GlyphBattery90,
            80 => GlyphBattery80,
            70 => GlyphBattery70,
            60 => GlyphBattery60,
            50 => GlyphBattery50,
            40 => GlyphBattery40,
            30 => GlyphBattery30,
            20 => GlyphBattery20,
            10 => GlyphBattery10,
            _ => GlyphBattery0,
        };
    }
}
