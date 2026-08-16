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
    /// true = apply charge limit and assigned profile from config.json.
    /// </summary>
    [JsonPropertyName("persist")]
    public bool Persist { get; set; }

    /// <summary>
    /// Session-level persist flag. In-memory only, not saved to disk.
    /// Initialized from Persist on backend startup. When true, allows hardware writes
    /// even if Persist is false (for testing). Lost when backend service stops.
    /// </summary>
    [JsonPropertyName("session_persist")]
    public bool SessionPersist { get; set; }

    /// <summary>Battery charge limit percentage (75–100). Applied on startup when persist=true.</summary>
    [JsonPropertyName("charge_limit_pct")]
    public int ChargeLimitPercent { get; set; } = 100;

    /// <summary>Launch at user logon.</summary>
    [JsonPropertyName("auto_start")]
    public bool AutoStart { get; set; }

    /// <summary>Home page widget layout (order, visibility, columns).</summary>
    [JsonPropertyName("home_layout")]
    public HomeLayout HomeLayout { get; set; } = new();

    public override string ToString() =>
        $"Config(lang:{Language}, theme:{Theme}, persist:{Persist}, sessionPersist:{SessionPersist}, " +
        $"chargeLimit:{ChargeLimitPercent}%, autoStart:{AutoStart}, " +
        $"layout:{HomeLayout})";
}

/// <summary>
/// A single widget entry in the home layout, with ID and size.
/// </summary>
public sealed class WidgetEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("col_span")]
    public int ColSpan { get; set; } = 1;

    [JsonPropertyName("row_span")]
    public int RowSpan { get; set; } = 1;

    public override string ToString() => $"{Id}({ColSpan}x{RowSpan})";
}

/// <summary>
/// Home page widget layout: widget list (order + size), and grid dimensions.
/// </summary>
public sealed class HomeLayout
{
    /// <summary>Widgets in display order, each with ID and size.</summary>
    [JsonPropertyName("widgets")]
    public List<WidgetEntry> Widgets { get; set; } = new();

    /// <summary>Number of columns in the home page grid (3–4).</summary>
    [JsonPropertyName("columns")]
    public int Columns { get; set; } = 3;

    /// <summary>Base column width in pixels (at 100% DPI). Default 140.</summary>
    [JsonPropertyName("column_width")]
    public int ColumnWidth { get; set; } = 140;

    /// <summary>Window height in pixels (at 100% DPI). Default 600.</summary>
    [JsonPropertyName("window_height")]
    public int WindowHeight { get; set; } = 600;

    public override string ToString() =>
        $"HomeLayout(widgets:[{string.Join(",", Widgets)}], cols:{Columns}, colWidth:{ColumnWidth}, height:{WindowHeight})";
}
