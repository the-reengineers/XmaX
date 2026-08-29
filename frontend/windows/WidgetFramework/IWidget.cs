using Windows.Foundation;

namespace XmaX.WidgetFramework;

/// <summary>
/// Contract for all home-page widgets. Implemented by <see cref="WidgetBase"/>;
/// consumed by the host, factory, and editor.
///
/// Widgets expose their identity and respond to lifecycle/environment changes.
/// Data flow (metrics in) and commands out are handled separately via services
/// and <see cref="IWidgetApi"/>.
/// </summary>
public interface IWidget
{
    /// <summary>Unique widget identifier (matches the key used in <see cref="HomeWidgetFactory"/>).</summary>
    string Id { get; }

    /// <summary>
    /// Refresh the widget's display from its data source. Called by the framework
    /// when underlying data changes. Implementations should be idempotent and
    /// thread-safe (typically dispatched to the UI thread via
    /// <c>DispatcherQueue.TryEnqueue</c>).
    /// </summary>
    void Refresh();

    /// <summary>
    /// Called when the grid's column count changes (e.g. window resize). Widgets
    /// that lay out differently at different column counts override this to
    /// rebuild their internal layout.
    /// </summary>
    /// <param name="columns">The new column count.</param>
    void OnColumnsChanged(int columns);

    /// <summary>
    /// Called when the widget's allocated size changes. Widgets that need to
    /// react to their actual rendered size (beyond column count) override this.
    /// </summary>
    /// <param name="size">The widget's new size in device-independent pixels.</param>
    void OnWidgetResized(Size size);
}
