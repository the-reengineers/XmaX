using System.Text.Json.Serialization;

namespace XmaX.Models;

/// <summary>
/// A named profile combining TDP limits and a fan curve reference.
/// Profiles are static hardware configs — they set fixed TDP values and fan behavior.
/// </summary>
public sealed class Profile
{
    /// <summary>Immutable slug ID (lowercase, hyphenated, derived from name at creation).</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>User-visible display name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>TDP limits in watts (nested object matching backend JSON: tdp.stapm/fast/slow).</summary>
    [JsonPropertyName("tdp")]
    public TdpLimits Tdp { get; set; } = new();

    /// <summary>Fan curve slug reference. Mandatory — every profile must have a fan curve.</summary>
    [JsonPropertyName("fan_curve")]
    public string FanCurve { get; set; } = "";

    public override string ToString() =>
        $"Profile({Id}: \"{Name}\", TDP:{Tdp.Stapm}/{Tdp.Fast}/{Tdp.Slow}W, Fan:{FanCurve ?? "auto"})";
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
