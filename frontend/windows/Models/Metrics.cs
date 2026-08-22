using System.Text.Json.Serialization;

namespace XmaX.Models;

/// <summary>
/// Full metrics snapshot from the backend.
/// Matches the JSON structure sent by get_metrics and metrics events.
/// </summary>
public sealed class Metrics
{
    [JsonPropertyName("cpu")]
    public CpuMetrics Cpu { get; set; } = new();

    [JsonPropertyName("gpu")]
    public GpuMetrics Gpu { get; set; } = new();

    [JsonPropertyName("ram")]
    public RamMetrics Ram { get; set; } = new();

    [JsonPropertyName("fan")]
    public FanStatus Fan { get; set; } = new();

    [JsonPropertyName("power")]
    public PowerStatus Power { get; set; } = new();

    /// <summary>Unix timestamp of this snapshot.</summary>
    [JsonPropertyName("ts")]
    public long Timestamp { get; set; }

    public override string ToString() =>
        $"Metrics(CPU:{Cpu.UtilPercent:F0}% {Cpu.TempC}°C, GPU:{Gpu.UtilPercent:F0}% {Gpu.TempC}°C, " +
        $"RAM:{Ram.UsedBytes / (1024.0 * 1024.0 * 1024.0):F1}/{Ram.TotalBytes / (1024.0 * 1024.0 * 1024.0):F1}GB, Fan:{Fan.Mode} {Fan.Rpm}RPM, " +
        $"Power:{Power.Mode} {Power.BatteryPercent}%)";
}

/// <summary>CPU metrics.</summary>
public sealed class CpuMetrics
{
    [JsonPropertyName("util_pct")]
    public double UtilPercent { get; set; }

    [JsonPropertyName("clock_mhz")]
    public uint ClockMhz { get; set; }

    /// <summary>Temperature in Celsius. Null if sensor unavailable.</summary>
    [JsonPropertyName("temp_c")]
    public int? TempC { get; set; }

    /// <summary>Package power in watts. Null if sensor unavailable.</summary>
    [JsonPropertyName("package_watts")]
    public double? PackageWatts { get; set; }

    public override string ToString() =>
        $"CPU({UtilPercent:F0}%, {ClockMhz}MHz, {TempC?.ToString() ?? "?"}°C, {PackageWatts?.ToString("F1") ?? "?"}W)";
}

/// <summary>GPU metrics.</summary>
public sealed class GpuMetrics
{
    [JsonPropertyName("util_pct")]
    public double UtilPercent { get; set; }

    [JsonPropertyName("clock_mhz")]
    public uint ClockMhz { get; set; }

    /// <summary>Temperature in Celsius. Null if sensor unavailable.</summary>
    [JsonPropertyName("temp_c")]
    public int? TempC { get; set; }

    /// <summary>Power in watts. Null if sensor unavailable.</summary>
    [JsonPropertyName("power_w")]
    public double? PowerW { get; set; }

    /// <summary>VRAM used in bytes. Null if sensor unavailable.</summary>
    [JsonPropertyName("vram_used_bytes")]
    public ulong? VramUsedBytes { get; set; }

    /// <summary>VRAM total in bytes. Null if sensor unavailable.</summary>
    [JsonPropertyName("vram_total_bytes")]
    public ulong? VramTotalBytes { get; set; }

    public override string ToString() =>
        $"GPU({UtilPercent:F0}%, {ClockMhz}MHz, {TempC?.ToString() ?? "?"}°C, {PowerW?.ToString("F1") ?? "?"}W, " +
        $"VRAM:{FormatBytes(VramUsedBytes)}/{FormatBytes(VramTotalBytes)})";

    private static string FormatBytes(ulong? bytes) =>
        bytes.HasValue ? $"{bytes.Value / (1024.0 * 1024.0 * 1024.0):F1}GB" : "?";
}

/// <summary>RAM metrics.</summary>
public sealed class RamMetrics
{
    [JsonPropertyName("used_bytes")]
    public ulong UsedBytes { get; set; }

    [JsonPropertyName("total_bytes")]
    public ulong TotalBytes { get; set; }

    [JsonPropertyName("avail_bytes")]
    public ulong AvailBytes { get; set; }

    [JsonPropertyName("load_pct")]
    public double LoadPercent { get; set; }

    public override string ToString() =>
        $"RAM({UsedBytes / (1024.0 * 1024.0 * 1024.0):F1}/{TotalBytes / (1024.0 * 1024.0 * 1024.0):F1}GB, {LoadPercent:F0}% load)";
}

/// <summary>Fan status snapshot.</summary>
public sealed class FanStatus
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "auto";

    [JsonPropertyName("speed_pct")]
    public double SpeedPercent { get; set; }

    [JsonPropertyName("rpm")]
    public ushort Rpm { get; set; }

    public override string ToString() => $"Fan({Mode}, {SpeedPercent:F0}%, {Rpm}RPM)";
}

/// <summary>Power state snapshot.</summary>
public sealed class PowerStatus
{
    /// <summary>Power source mode: battery, usb_c_slow, usb_c_fast, dc_in, unknown.</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "unknown";

    /// <summary>Human-readable label for the power source.</summary>
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    /// <summary>Battery charge percentage (0-100). Null if no battery.</summary>
    [JsonPropertyName("battery_pct")]
    public int? BatteryPercent { get; set; }

    /// <summary>Current charge limit percentage (75-100). Null if unavailable.</summary>
    [JsonPropertyName("charge_limit_pct")]
    public int? ChargeLimitPercent { get; set; }

    public override string ToString() =>
        $"Power({Mode}: {Label}, Battery:{BatteryPercent?.ToString() ?? "?"}%, Limit:{ChargeLimitPercent?.ToString() ?? "?"}%)";
}
