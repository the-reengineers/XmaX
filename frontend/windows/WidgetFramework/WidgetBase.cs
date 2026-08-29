using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using XmaX.Services;

namespace XmaX.WidgetFramework;

/// <summary>
/// Base class for all home-page widgets. Provides:
/// - Protected access to <see cref="IWidgetApi"/> for commands
/// - Protected access to high-frequency push-data services (<see cref="MetricsService"/>, <see cref="ProfileService"/>)
/// - Automatic subscription to <see cref="WidgetService.Columns"/> changes → calls <see cref="OnColumnsChanged"/>
/// - <see cref="ScheduleRefresh"/> helper for thread-safe UI updates
/// - Lifecycle management (subscribe/unsubscribe on Loaded/Unloaded)
///
/// Subclasses override <see cref="Refresh"/>, <see cref="OnColumnsChanged"/>, etc.
/// </summary>
public partial class WidgetBase : UserControl, IWidget
{
    /// <summary>Widget API for commands (apply profile, set charge limit, etc.).</summary>
    protected IWidgetApi Api { get; }

    /// <summary>Unique widget identifier.</summary>
    public string Id { get; }

    /// <summary>Metrics service for high-frequency push data (CPU temp, GPU load, etc.).</summary>
    protected MetricsService Metrics { get; }

    /// <summary>Profile service for profile list, active profile, adaptive state.</summary>
    protected ProfileService Profiles { get; }

    /// <summary>Widget service for layout configuration (Columns, etc.).</summary>
    protected WidgetService WidgetService { get; }

    protected WidgetBase(IWidgetApi api, string id)
    {
        Api = api;
        Id = id;
        Metrics = App.MetricsService;
        Profiles = App.ProfileService;
        WidgetService = App.WidgetService;

        // Subscribe to lifecycle events — actual service subscription happens in OnLoaded
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // ── IWidget implementation ──────────────────────────────────────────

    /// <summary>
    /// Refresh the widget's display from its data source. Called by the framework
    /// when underlying data changes. Subclasses override this to update their UI.
    /// Default implementation is a no-op.
    /// </summary>
    public virtual void Refresh() { }

    /// <summary>
    /// Called when the grid's column count changes. Subclasses that lay out
    /// differently at different column counts override this to rebuild their
    /// internal layout. Default implementation is a no-op.
    /// </summary>
    /// <param name="columns">The new column count.</param>
    public virtual void OnColumnsChanged(int columns) { }

    /// <summary>
    /// Called when the widget's allocated size changes. Subclasses that need to
    /// react to their actual rendered size override this. Default implementation
    /// is a no-op.
    /// </summary>
    /// <param name="size">The widget's new size in device-independent pixels.</param>
    public virtual void OnWidgetResized(Size size) { }

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Schedule a refresh on the UI thread. Wraps <see cref="Refresh"/> in
    /// <c>DispatcherQueue.TryEnqueue</c> for thread-safe UI updates.
    /// </summary>
    protected void ScheduleRefresh()
    {
        DispatcherQueue.TryEnqueue(Refresh);
    }

    // ── Lifecycle ───────────────────────────────────────────────────────

    private void OnWidgetServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WidgetService.Columns))
        {
            OnColumnsChanged(WidgetService.Columns);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Subscribe to widget service for column changes
        WidgetService.PropertyChanged += OnWidgetServicePropertyChanged;
        OnLoaded();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Unsubscribe to prevent memory leaks
        WidgetService.PropertyChanged -= OnWidgetServicePropertyChanged;
        OnUnloaded();
    }

    /// <summary>
    /// Called when the widget is loaded into the visual tree. Subclasses can
    /// override this to perform initialization. Default implementation is a no-op.
    /// </summary>
    protected virtual void OnLoaded() { }

    /// <summary>
    /// Called when the widget is removed from the visual tree. Subclasses can
    /// override this to perform cleanup. Default implementation is a no-op.
    /// </summary>
    protected virtual void OnUnloaded() { }
}
