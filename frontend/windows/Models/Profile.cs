using System.Text.Json.Serialization;

namespace XmaX.Models;

/// <summary>
/// A named profile that can be either fixed (TDP limits + fan curve) or adaptive (PID controller config).
/// Multiple profiles can share a power state, but one of them must be marked as IsDefault.
///</summary>
public sealed class Profile
{
    /// <summary>Immutable slug ID (lowercase, hyphenated, derived from name at creation).</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>User-visible display name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Profile type: "fixed" or "adaptive".</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "fixed";

    /// <summary>Assigned power state: "battery", "usb_c_slow", "usb_c_fast", "dc_in", or null.</summary>
    [JsonPropertyName("power_state")]
    public string? PowerState { get; set; }

    /// <summary>Whether this is the default profile for its assigned power state.</summary>
    [JsonPropertyName("is_default")]
    public bool IsDefault { get; set; }

    /// <summary>Whether this is an adaptive profile.</summary>
    public bool IsAdaptive => Type == "adaptive";

    // Fixed profile fields (used when Type == "fixed")

    /// <summary>TDP limits in watts (nested object matching backend JSON: tdp.stapm/fast/slow).</summary>
    [JsonPropertyName("tdp")]
    public TdpLimits Tdp { get; set; } = new();

    /// <summary>Fan curve slug reference. Mandatory for fixed profiles.</summary>
    [JsonPropertyName("fan_curve")]
    public string FanCurve { get; set; } = "";

    // Adaptive profile fields (used when Type == "adaptive")

    /// <summary>Tuning preset: "silent", "default", or "performance".</summary>
    [JsonPropertyName("tuning")]
    public string Tuning { get; set; } = "default";

    /// <summary>Target temperature in Celsius (50–100).</summary>
    [JsonPropertyName("target_temp_c")]
    public int TargetTempC { get; set; } = 85;

    /// <summary>Maximum TDP in watts (6–120, capped by power state max at runtime).</summary>
    [JsonPropertyName("tdp_max_w")]
    public int TdpMaxW { get; set; } = 55;

    /// <summary>Maximum fan speed percentage (0–100).</summary>
    [JsonPropertyName("fan_max_pct")]
    public int FanMaxPercent { get; set; } = 100;

    public override string ToString() => IsAdaptive
        ? $"Profile({Id}: \"{Name}\", Adaptive, {Tuning}, target:{TargetTempC}°C, TDP≤{TdpMaxW}W, fan≤{FanMaxPercent}%)"
        : $"Profile({Id}: \"{Name}\", TDP:{Tdp.Stapm}/{Tdp.Fast}/{Tdp.Slow}W, Fan:{FanCurve ?? "auto"})";
}

/// <summary>TDP limits: STAPM, Fast boost, and Slow sustained, in watts.</summary>
public sealed class TdpLimits
{
    /// <summary>STAPM TDP limit in watts.</summary>
    [JsonPropertyName("stapm")]
    public int Stapm { get; set; }

    /// <summary>Fast boost TDP limit in watts.</summary>
    [JsonPropertyName("fast")]
    public int Fast { get; set; }

    /// <summary>Slow sustained TDP limit in watts.</summary>
    [JsonPropertyName("slow")]
    public int Slow { get; set; }

    public override string ToString() => $"{Stapm}/{Fast}/{Slow}W";
}
