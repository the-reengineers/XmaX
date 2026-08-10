namespace XmaX.Widgets;

/// <summary>
/// Interface for home page widgets.
/// Each widget has a unique ID and provides a UI control for display.
/// </summary>
public interface IHomeWidget
{
    /// <summary>Unique widget identifier (e.g., "profiles", "metrics", "adaptive").</summary>
    string WidgetId { get; }

    /// <summary>The UI control to display. Typically a UserControl.</summary>
    object Control { get; }
}
