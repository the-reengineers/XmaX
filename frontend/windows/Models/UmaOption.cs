namespace XmaX.Models;

/// <summary>
/// UMA (Unified Memory Architecture) option for Variable Graphics Memory.
/// Represents a preset for RAM/VRAM split on AMD APUs.
/// </summary>
public sealed class UmaOption
{
    /// <summary>Unique identifier (e.g., "auto:0.0", "custom:2.0"). Used for matching and set commands.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Human-readable name (e.g., "Auto", "Custom").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Mode: "auto" or "custom".</summary>
    public string Mode { get; set; } = "auto";

    /// <summary>System RAM dedicated to VRAM in GB.</summary>
    public double MemoryCarvedGb { get; set; }

    /// <summary>Remaining system RAM in GB.</summary>
    public double MemoryRemainingGb { get; set; }

    /// <summary>Whether this is the currently selected option.</summary>
    public bool IsSelected { get; set; }

    public override string ToString()
    {
        if (Mode == "auto")
            return $"Auto (system managed)";
        return $"{MemoryCarvedGb:F1} GB VRAM / {MemoryRemainingGb:F1} GB RAM remaining";
    }
}

/// <summary>
/// Response from get_uma_options command.
/// </summary>
public sealed class UmaOptionsResponse
{
    /// <summary>Whether Variable Graphics Memory is supported on this system.</summary>
    public bool Supported { get; set; }

    /// <summary>List of available UMA presets.</summary>
    public List<UmaOption> AvailableOptions { get; set; } = new();

    /// <summary>Currently selected UMA option.</summary>
    public UmaOption? CurrentOption { get; set; }
}
