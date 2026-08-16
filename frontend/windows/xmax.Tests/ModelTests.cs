using System.Text.Json;
using XmaX.Models;

namespace XmaX.Tests;

/// <summary>
/// Tests for model JSON deserialization — verifies C# models match backend JSON structures.
/// </summary>
public class ModelTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    // ===== Metrics deserialization =====

    [Fact]
    public void Metrics_FullPayload_DeserializesCorrectly()
    {
        var json = """
        {
            "cpu": { "util_pct": 7.8, "clock_mhz": 3000, "temp_c": 79, "package_watts": 45.2 },
            "gpu": { "util_pct": 93.0, "clock_mhz": 1783, "temp_c": 73, "power_w": 47.0, "vram_used_mb": 4096, "vram_total_mb": 16384 },
            "ram": { "used_gb": 25.8, "total_gb": 111.6, "avail_gb": 85.8, "load_pct": 23.0 },
            "fan": { "mode": "auto", "speed_pct": 75.0, "rpm": 3200 },
            "power": { "mode": "dc_in", "label": "DC-In (dedicated charger)", "battery_pct": 91, "charge_limit_pct": 90 },
            "ts": 1722000000
        }
        """;

        var metrics = JsonSerializer.Deserialize<Metrics>(json, JsonOptions);

        Assert.NotNull(metrics);
        // CPU
        Assert.Equal(7.8, metrics.Cpu.UtilPercent);
        Assert.Equal(3000u, metrics.Cpu.ClockMhz);
        Assert.Equal(79, metrics.Cpu.TempC);
        Assert.Equal(45.2, metrics.Cpu.PackageWatts);
        // GPU
        Assert.Equal(93.0, metrics.Gpu.UtilPercent);
        Assert.Equal(1783u, metrics.Gpu.ClockMhz);
        Assert.Equal(73, metrics.Gpu.TempC);
        Assert.Equal(47.0, metrics.Gpu.PowerW);
        Assert.Equal(4096u, metrics.Gpu.VramUsedMb);
        Assert.Equal(16384u, metrics.Gpu.VramTotalMb);
        // RAM
        Assert.Equal(25.8, metrics.Ram.UsedGb);
        Assert.Equal(111.6, metrics.Ram.TotalGb);
        Assert.Equal(85.8, metrics.Ram.AvailGb);
        Assert.Equal(23.0, metrics.Ram.LoadPercent);
        // Fan
        Assert.Equal("auto", metrics.Fan.Mode);
        Assert.Equal(75.0, metrics.Fan.SpeedPercent);
        Assert.Equal(3200, metrics.Fan.Rpm);
        // Power
        Assert.Equal("dc_in", metrics.Power.Mode);
        Assert.Equal("DC-In (dedicated charger)", metrics.Power.Label);
        Assert.Equal(91, metrics.Power.BatteryPercent);
        Assert.Equal(90, metrics.Power.ChargeLimitPercent);
        // Timestamp
        Assert.Equal(1722000000, metrics.Timestamp);
    }

    [Fact]
    public void Metrics_NullableFields_NullWhenAbsent()
    {
        var json = """
        {
            "cpu": { "util_pct": 5.0, "clock_mhz": 2000, "temp_c": null, "package_watts": null },
            "gpu": { "util_pct": 0.0, "clock_mhz": 0, "temp_c": null, "power_w": null, "vram_used_mb": null, "vram_total_mb": null },
            "ram": { "used_gb": 0, "total_gb": 0, "avail_gb": 0, "load_pct": 0 },
            "fan": { "mode": "auto", "speed_pct": 0, "rpm": 0 },
            "power": { "mode": "battery", "label": "Battery only", "battery_pct": null, "charge_limit_pct": null },
            "ts": 0
        }
        """;

        var metrics = JsonSerializer.Deserialize<Metrics>(json, JsonOptions);

        Assert.NotNull(metrics);
        Assert.Null(metrics.Cpu.TempC);
        Assert.Null(metrics.Cpu.PackageWatts);
        Assert.Null(metrics.Gpu.TempC);
        Assert.Null(metrics.Gpu.PowerW);
        Assert.Null(metrics.Gpu.VramUsedMb);
        Assert.Null(metrics.Gpu.VramTotalMb);
        Assert.Null(metrics.Power.BatteryPercent);
        Assert.Null(metrics.Power.ChargeLimitPercent);
    }

    [Fact]
    public void Metrics_DefaultValues_WhenFieldsMissing()
    {
        // Backend always sends all fields, but test resilience to missing optional fields
        var json = """
        {
            "cpu": {},
            "gpu": {},
            "ram": {},
            "fan": {},
            "power": {}
        }
        """;

        var metrics = JsonSerializer.Deserialize<Metrics>(json, JsonOptions);

        Assert.NotNull(metrics);
        Assert.Equal(0.0, metrics.Cpu.UtilPercent);
        Assert.Equal(0u, metrics.Cpu.ClockMhz);
        Assert.Null(metrics.Cpu.TempC);  // nullable defaults to null
        Assert.Null(metrics.Power.BatteryPercent);
    }

    [Fact]
    public void Metrics_ToString_DoesNotThrow()
    {
        var metrics = new Metrics
        {
            Cpu = { UtilPercent = 50, TempC = 75 },
            Gpu = { UtilPercent = 80, TempC = 70 },
        };

        var str = metrics.ToString();
        Assert.NotNull(str);
        Assert.Contains("CPU", str);
        Assert.Contains("GPU", str);
    }

    // ===== Profile deserialization =====

    [Fact]
    public void Profile_Fixed_DeserializesCorrectly()
    {
        var json = """
        {
            "id": "gaming",
            "name": "Gaming",
            "type": "fixed",
            "tdp": { "stapm": 45, "fast": 50, "slow": 45 },
            "fan_curve": "aggressive"
        }
        """;

        var profile = JsonSerializer.Deserialize<Profile>(json, JsonOptions);

        Assert.NotNull(profile);
        Assert.Equal("gaming", profile.Id);
        Assert.Equal("Gaming", profile.Name);
        Assert.Equal("fixed", profile.Type);
        Assert.False(profile.IsAdaptive);
        Assert.Equal(45, profile.Tdp.Stapm);
        Assert.Equal(50, profile.Tdp.Fast);
        Assert.Equal(45, profile.Tdp.Slow);
        Assert.Equal("aggressive", profile.FanCurve);
    }

    [Fact]
    public void Profile_Adaptive_DeserializesCorrectly()
    {
        var json = """
        {
            "id": "quiet-adaptive",
            "name": "Quiet Adaptive",
            "type": "adaptive",
            "power_state": "battery",
            "tuning": "silent",
            "target_temp_c": 75,
            "tdp_max_w": 40,
            "fan_max_pct": 80
        }
        """;

        var profile = JsonSerializer.Deserialize<Profile>(json, JsonOptions);

        Assert.NotNull(profile);
        Assert.Equal("quiet-adaptive", profile.Id);
        Assert.Equal("Quiet Adaptive", profile.Name);
        Assert.Equal("adaptive", profile.Type);
        Assert.True(profile.IsAdaptive);
        Assert.Equal("battery", profile.PowerState);
        Assert.Equal("silent", profile.Tuning);
        Assert.Equal(75, profile.TargetTempC);
        Assert.Equal(40, profile.TdpMaxW);
        Assert.Equal(80, profile.FanMaxPercent);
    }

    [Fact]
    public void Profile_NullFanCurve_DeserializesAsEmpty()
    {
        var json = """
        {
            "id": "max-perf",
            "name": "Max Performance",
            "type": "fixed",
            "tdp": { "stapm": 55, "fast": 65, "slow": 55 },
            "fan_curve": null
        }
        """;

        var profile = JsonSerializer.Deserialize<Profile>(json, JsonOptions);

        Assert.NotNull(profile);
        Assert.Null(profile.FanCurve); // JSON null deserializes as null
    }

    [Fact]
    public void Profile_ToString_DoesNotThrow()
    {
        var profile = new Profile { Id = "test", Name = "Test", Tdp = new TdpLimits { Stapm = 30, Fast = 35, Slow = 30 } };
        Assert.NotNull(profile.ToString());
    }

    [Fact]
    public void Profile_IsDefault_DeserializesCorrectly()
    {
        var json = """
        {
            "id": "battery-default",
            "name": "Battery Default",
            "type": "fixed",
            "power_state": "battery",
            "is_default": true,
            "tdp": { "stapm": 25, "fast": 30, "slow": 25 },
            "fan_curve": "default"
        }
        """;

        var profile = JsonSerializer.Deserialize<Profile>(json, JsonOptions);

        Assert.NotNull(profile);
        Assert.Equal("battery", profile.PowerState);
        Assert.True(profile.IsDefault);
    }

    [Fact]
    public void Profile_IsDefault_FalseByDefault()
    {
        var json = """
        {
            "id": "no-default",
            "name": "No Default",
            "type": "fixed",
            "power_state": "dc_in",
            "tdp": { "stapm": 45, "fast": 50, "slow": 45 },
            "fan_curve": "default"
        }
        """;

        var profile = JsonSerializer.Deserialize<Profile>(json, JsonOptions);

        Assert.NotNull(profile);
        Assert.False(profile.IsDefault); // Missing field → defaults to false
    }

    // ===== FanCurve deserialization =====

    [Fact]
    public void FanCurve_DeserializesCorrectly()
    {
        var json = """
        {
            "id": "quiet",
            "name": "Quiet",
            "points": [
                { "temp_c": 40, "speed_pct": 15 },
                { "temp_c": 60, "speed_pct": 25 },
                { "temp_c": 75, "speed_pct": 35 },
                { "temp_c": 85, "speed_pct": 40 }
            ]
        }
        """;

        var curve = JsonSerializer.Deserialize<FanCurve>(json, JsonOptions);

        Assert.NotNull(curve);
        Assert.Equal("quiet", curve.Id);
        Assert.Equal("Quiet", curve.Name);
        Assert.Equal(4, curve.Points.Count);
        Assert.Equal(40, curve.Points[0].TempC);
        Assert.Equal(15, curve.Points[0].SpeedPercent);
        Assert.Equal(85, curve.Points[3].TempC);
        Assert.Equal(40, curve.Points[3].SpeedPercent);
    }

    [Fact]
    public void FanCurvePoint_ToString_DoesNotThrow()
    {
        var point = new FanCurvePoint { TempC = 60, SpeedPercent = 50 };
        Assert.Equal("60°C→50%", point.ToString());
    }

    // ===== AppConfig deserialization =====

    [Fact]
    public void AppConfig_FullPayload_DeserializesCorrectly()
    {
        var json = """
        {
            "language": "en",
            "theme": "dark",
            "persist": true,
            "charge_limit_pct": 85,
            "auto_start": true
        }
        """;

        var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);

        Assert.NotNull(config);
        Assert.Equal("en", config.Language);
        Assert.Equal("dark", config.Theme);
        Assert.True(config.Persist);
        Assert.Equal(85, config.ChargeLimitPercent);
        Assert.True(config.AutoStart);
    }

    [Fact]
    public void AppConfig_DefaultValues_WhenFieldsMissing()
    {
        var json = "{}";

        var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);

        Assert.NotNull(config);
        Assert.Equal("auto", config.Language);
        Assert.Equal("system", config.Theme);
        Assert.False(config.Persist);
        Assert.Equal(100, config.ChargeLimitPercent);
        Assert.False(config.AutoStart);
    }

    [Fact]
    public void AppConfig_ToString_DoesNotThrow()
    {
        var config = new AppConfig();
        Assert.NotNull(config.ToString());
        Assert.Contains("lang:auto", config.ToString());
    }
}
