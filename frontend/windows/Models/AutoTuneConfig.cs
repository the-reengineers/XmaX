using System.Text.Json.Serialization;

namespace XmaX.Models;

/// <summary>
/// Global adaptive controller configuration.
/// The adaptive controller dynamically adjusts TDP and fan speed to track a target temperature.
/// There is one adaptive configuration — it operates independently of user profiles
/// and across all power states.
/// </summary>
public sealed class AutoTuneConfig
{
    /// <summary>Whether the adaptive controller is active.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>
    /// Tuning preset: "silent", "default", or "performance".
    /// Same algorithm, different PID parameters and priorities.
    /// </summary>
    [JsonPropertyName("tuning")]
    public string Tuning { get; set; } = "default";

    /// <summary>Target temperature in Celsius that the controller tracks.</summary>
    [JsonPropertyName("target_temp_c")]
    public int TargetTempC { get; set; } = 85;

    /// <summary>
    /// User's desired TDP ceiling in watts.
    /// At runtime, this is clamped by the current power state's tdp_max_w:
    /// effective_tdp_max = min(this.TdpMaxW, power_state.tdp_max_w)
    /// </summary>
    [JsonPropertyName("tdp_max_w")]
    public int TdpMaxW { get; set; } = 55;

    /// <summary>Maximum fan speed percentage the controller is allowed to use.</summary>
    [JsonPropertyName("fan_max_pct")]
    public int FanMaxPercent { get; set; } = 100;

    public override string ToString() =>
        $"AutoTune({(Enabled ? "active" : "inactive")}, {Tuning}, target:{TargetTempC}°C, " +
        $"TDP≤{TdpMaxW}W, fan≤{FanMaxPercent}%)";
}

/// <summary>
/// Response from get_auto_tune command.
/// Includes runtime state beyond the config.
/// </summary>
public sealed class AutoTuneState
{
    /// <summary>Whether adaptive is the current active mode (vs a profile).</summary>
    [JsonPropertyName("active")]
    public bool Active { get; set; }

    /// <summary>Tuning preset name.</summary>
    [JsonPropertyName("tuning")]
    public string Tuning { get; set; } = "default";

    /// <summary>Configured target temperature.</summary>
    [JsonPropertyName("target_temp_c")]
    public int TargetTempC { get; set; }

    /// <summary>Configured TDP ceiling (before power state clamping).</summary>
    [JsonPropertyName("tdp_max_w")]
    public int TdpMaxW { get; set; }

    /// <summary>Effective TDP ceiling (after power state clamping).</summary>
    [JsonPropertyName("effective_tdp_max_w")]
    public int EffectiveTdpMaxW { get; set; }

    /// <summary>Maximum fan speed percentage.</summary>
    [JsonPropertyName("fan_max_pct")]
    public int FanMaxPercent { get; set; }

    public override string ToString() =>
        $"AutoTuneState({(Active ? "active" : "inactive")}, {Tuning}, target:{TargetTempC}°C, " +
        $"TDP≤{EffectiveTdpMaxW}W effective, fan≤{FanMaxPercent}%)";
}
