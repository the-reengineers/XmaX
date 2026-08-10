using System.Text.Json.Serialization;

namespace XmaX.Models;

/// <summary>
/// A fan curve mapping temperature to fan speed.
/// The backend interpolates linearly between points.
/// </summary>
public sealed class FanCurve
{
    /// <summary>Immutable slug ID (lowercase, hyphenated, derived from name at creation).</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>User-visible display name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>
    /// Temperature-to-speed mapping points.
    /// Rules: min 2, max 10 points. Must be sorted by ascending temp_c.
    /// Below first point: use first point's speed.
    /// Above last point: use last point's speed.
    /// Between points: linear interpolation.
    /// </summary>
    [JsonPropertyName("points")]
    public List<FanCurvePoint> Points { get; set; } = new();

    public override string ToString()
    {
        var pointsStr = string.Join(", ", Points.Select(p => $"{p.TempC}°C→{p.SpeedPercent}%"));
        return $"FanCurve({Id}: \"{Name}\", [{pointsStr}])";
    }
}

/// <summary>A single point on a fan curve: temperature → fan speed.</summary>
public sealed class FanCurvePoint
{
    /// <summary>Temperature in Celsius.</summary>
    [JsonPropertyName("temp_c")]
    public int TempC { get; set; }

    /// <summary>Fan speed percentage (0–100).</summary>
    [JsonPropertyName("speed_pct")]
    public int SpeedPercent { get; set; }

    public override string ToString() => $"{TempC}°C→{SpeedPercent}%";
}
