using System.Text.Json.Serialization;

namespace XmaX.Models;

/// <summary>
/// Application configuration (config.json).
/// </summary>
public sealed class AppConfig
{
    /// <summary>Language: "auto", "en", or "zh".</summary>
    [JsonPropertyName("language")]
    public string Language { get; set; } = "auto";

    /// <summary>Theme: "system", "light", or "dark".</summary>
    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "system";

    /// <summary>
    /// Whether to apply user-configured settings on startup.
    /// false = no hardware writes, hardware at BIOS defaults.
    /// true = apply charge limit, power-state profile, adaptive config from config.json.
    /// </summary>
    [JsonPropertyName("persist")]
    public bool Persist { get; set; }

    /// <summary>Battery charge limit percentage (75–100). Applied on startup when persist=true.</summary>
    [JsonPropertyName("charge_limit_pct")]
    public int ChargeLimitPercent { get; set; } = 100;

    /// <summary>Launch at user logon.</summary>
    [JsonPropertyName("auto_start")]
    public bool AutoStart { get; set; }

    /// <summary>Global adaptive controller config. Null if never configured.</summary>
    [JsonPropertyName("auto_tune")]
    public AutoTuneConfig? AutoTune { get; set; }

    /// <summary>
    /// Power state → profile + adaptive TDP ceiling mapping.
    /// All four states are required (no nulls).
    /// </summary>
    [JsonPropertyName("power_state_profiles")]
    public PowerStateProfiles PowerStateProfiles { get; set; } = new();

    /// <summary>Home page widget layout (order, visibility, columns).</summary>
    [JsonPropertyName("home_layout")]
    public HomeLayout HomeLayout { get; set; } = new();

    public override string ToString() =>
        $"Config(lang:{Language}, theme:{Theme}, persist:{Persist}, " +
        $"chargeLimit:{ChargeLimitPercent}%, autoStart:{AutoStart}, " +
        $"autoTune:{AutoTune?.ToString() ?? "none"}, " +
        $"layout:{HomeLayout})";
}

/// <summary>
/// Maps each power state to a profile slug and adaptive TDP ceiling.
/// All four states are required — no null values allowed.
/// </summary>
public sealed class PowerStateProfiles
{
    [JsonPropertyName("battery")]
    public PowerStateAssignment Battery { get; set; } = new();

    [JsonPropertyName("usb_c_slow")]
    public PowerStateAssignment UsbCSlow { get; set; } = new();

    [JsonPropertyName("usb_c_fast")]
    public PowerStateAssignment UsbCFast { get; set; } = new();

    [JsonPropertyName("dc_in")]
    public PowerStateAssignment DcIn { get; set; } = new();

    public override string ToString() =>
        $"PowerStates(battery:{Battery.Profile}, usb_c_slow:{UsbCSlow.Profile}, " +
        $"usb_c_fast:{UsbCFast.Profile}, dc_in:{DcIn.Profile})";
}

/// <summary>
/// Assignment of a profile and adaptive TDP ceiling to a power state.
/// </summary>
public sealed class PowerStateAssignment
{
    /// <summary>Profile slug reference.</summary>
    [JsonPropertyName("profile")]
    public string Profile { get; set; } = "";

    /// <summary>Adaptive TDP hard ceiling in watts for this power state.</summary>
    [JsonPropertyName("tdp_max_w")]
    public int TdpMaxW { get; set; } = 25;

    public override string ToString() => $"{Profile} (TDP≤{TdpMaxW}W)";
}

/// <summary>
/// Home page widget layout: display order, visibility, and column count.
/// </summary>
public sealed class HomeLayout
{
    /// <summary>Widget IDs in display order. Widgets not in this list use default position.</summary>
    [JsonPropertyName("widget_order")]
    public List<string> WidgetOrder { get; set; } = new();

    /// <summary>Widget ID → visible. Widgets not in this map default to visible.</summary>
    [JsonPropertyName("widget_visibility")]
    public Dictionary<string, bool> WidgetVisibility { get; set; } = new();

    /// <summary>Number of columns in the home page grid (3–5).</summary>
    [JsonPropertyName("columns")]
    public int Columns { get; set; } = 3;

    public override string ToString() =>
        $"HomeLayout(order:[{string.Join(",", WidgetOrder)}], cols:{Columns})";
}
