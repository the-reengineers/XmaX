using Microsoft.UI.Xaml.Controls;
using XmaX.Models;
using XmaX.Services;

namespace XmaX.Widgets;

/// <summary>
/// Power state tile showing current power source and TDP limit.
/// Displays a Material icon below the power state text.
/// </summary>
public sealed partial class PowerWidget : UserControl
{
    private readonly MetricsService _metricsService;
    private readonly ProfileService _profileService;

    // Hardcoded max TDP per power state (must match backend power_state_max_tdp)
    private static readonly Dictionary<string, int> MaxTdpByState = new()
    {
        ["battery"] = 55,
        ["usb_c_slow"] = 20,
        ["usb_c_fast"] = 55,
        ["dc_in"] = 80,
    };

    // Glyph codes for Tabler Icons
    private static readonly string GlyphUnknown = char.ConvertFromUtf32(0xEC9D);
    private static readonly string GlyphBattery0 = char.ConvertFromUtf32(0xEA34);
    private static readonly string GlyphBattery25 = char.ConvertFromUtf32(0xEA2F);
    private static readonly string GlyphBattery50 = char.ConvertFromUtf32(0xEA30);
    private static readonly string GlyphBattery75 = char.ConvertFromUtf32(0xEA31);
    private static readonly string GlyphBattery100 = char.ConvertFromUtf32(0xEA32);
    private static readonly string GlyphUsbC = char.ConvertFromUtf32(0xF00C);
    private static readonly string GlyphBolt = char.ConvertFromUtf32(0xEA38);
    private static readonly string GlyphDcIn = char.ConvertFromUtf32(0xEBD9);

    public PowerWidget()
    {
        this.InitializeComponent();
        _metricsService = App.MetricsService;
        _profileService = App.ProfileService;
        _metricsService.PropertyChanged += OnMetricsChanged;
        _profileService.PropertyChanged += OnProfileServiceChanged;
        UpdateDisplay();
    }

    private void OnMetricsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MetricsService.Metrics))
        {
            DispatcherQueue.TryEnqueue(UpdateDisplay);
        }
    }

    private void OnProfileServiceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfileService.ActiveProfileId) ||
            e.PropertyName == nameof(ProfileService.IsAdaptiveActive) ||
            e.PropertyName == nameof(ProfileService.Profiles))
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

        // Show TDP limit — from active adaptive profile's TdpMaxW capped by power state max
        var tdp = ComputeEffectiveTdpMax(power.Mode);
        TdpText.Text = tdp > 0 ? Loc.F("metrics.power_format", tdp) : "";

        // Update icon based on power state
        UpdateIcon(power);
    }

    /// <summary>
    /// Compute effective TDP max: if an adaptive profile is active, use its TdpMaxW
    /// capped by the hardcoded power state max. Otherwise use the hardcoded max.
    /// </summary>
    private int ComputeEffectiveTdpMax(string powerMode)
    {
        var stateMax = MaxTdpByState.TryGetValue(powerMode, out var m) ? m : 55;

        if (_profileService.IsAdaptiveActive && !string.IsNullOrEmpty(_profileService.ActiveProfileId))
        {
            var activeProfile = _profileService.Profiles
                .FirstOrDefault(p => p.Id == _profileService.ActiveProfileId);
            if (activeProfile != null && activeProfile.IsAdaptive)
            {
                return Math.Min(activeProfile.TdpMaxW, stateMax);
            }
        }

        return stateMax;
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
                SecondaryIcon.Text = GlyphBolt;
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

        // Floor to nearest 25% increment, show >=90% as 100%
        var level = batteryPercent.Value >= 90 ? 100 : (batteryPercent.Value / 25) * 25;
        PrimaryIcon.Text = level switch
        {
            >= 100 => GlyphBattery100,
            75 => GlyphBattery75,
            50 => GlyphBattery50,
            25 => GlyphBattery25,
            _ => GlyphBattery0,
        };
    }
}
