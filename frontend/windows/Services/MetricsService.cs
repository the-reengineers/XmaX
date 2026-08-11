using System.ComponentModel;
using System.Text.Json.Nodes;
using XmaX.Models;

namespace XmaX.Services;

/// <summary>
/// Service that wraps PipeClient for metrics monitoring.
/// Subscribes to periodic metrics push on connect, exposes current Metrics via INotifyPropertyChanged.
/// </summary>
/// <remarks>
/// Thread safety: PropertyChanged is raised on the PipeClient's reader thread.
/// UI consumers should marshal to their dispatcher if needed.
/// </remarks>
public sealed class MetricsService : INotifyPropertyChanged, IDisposable
{
    private readonly PipeClient _pipe;
    private Metrics _metrics = new();
    private bool _subscribed;
    private bool _disposed;

    /// <summary>
    /// When true, metrics are still collected but PropertyChanged is suppressed.
    /// Set by MainWindow when the window is hidden to avoid unnecessary UI dispatch.
    /// </summary>
    public bool SuppressNotifications { get; set; }

    /// <summary>Default metrics push interval in milliseconds (matches backend default).</summary>
    public const int DefaultIntervalMs = 2000;

    public MetricsService(PipeClient pipe)
    {
        _pipe = pipe;
        _pipe.Connected += OnConnected;
        _pipe.Disconnected += OnDisconnected;
        _pipe.EventReceived += OnEventReceived;
    }

    /// <summary>Current metrics snapshot. Updates at the subscribed interval.</summary>
    public Metrics Metrics
    {
        get => _metrics;
        private set
        {
            if (ReferenceEquals(_metrics, value)) return;
            _metrics = value;
            if (!SuppressNotifications)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Metrics)));
            }
        }
    }

    /// <summary>Whether currently subscribed to metrics push.</summary>
    public bool IsSubscribed => _subscribed;

    /// <summary>
    /// Fire a PropertyChanged for Metrics to refresh all UI consumers.
    /// Called when the window becomes visible after a hidden period,
    /// so widgets display the latest collected data immediately.
    /// </summary>
    public void NotifyRefresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Metrics)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Subscribe to periodic metrics push.
    /// Call after connect to start receiving metrics events.
    /// </summary>
    public async Task SubscribeAsync(int intervalMs = DefaultIntervalMs)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MetricsService));

        var payload = new JsonObject { ["interval_ms"] = intervalMs };
        await _pipe.SendCommandAsync("subscribe_metrics", payload).ConfigureAwait(false);
        _subscribed = true;
    }

    /// <summary>
    /// Unsubscribe from metrics push.
    /// Call when live updates are no longer needed (e.g., dashboard closed).
    /// </summary>
    public async Task UnsubscribeAsync()
    {
        if (_disposed || !_subscribed) return;

        await _pipe.SendCommandAsync("unsubscribe_metrics").ConfigureAwait(false);
        _subscribed = false;
    }

    /// <summary>
    /// Fetch a one-shot metrics snapshot without subscribing.
    /// Useful for catching up after reconnect.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MetricsService));

        var data = await _pipe.SendCommandAsync("get_metrics").ConfigureAwait(false);
        UpdateMetricsFromJson(data);
    }

    private void OnConnected()
    {
        // On reconnect, subscription state is lost -- reset and auto-resubscribe
        _subscribed = false;
        _ = SubscribeAsync();
    }

    private void OnDisconnected()
    {
        _subscribed = false;
    }

    private void OnEventReceived(string eventName, JsonObject data)
    {
        if (eventName == "metrics")
        {
            UpdateMetricsFromJson(data);
        }
    }

    private void UpdateMetricsFromJson(JsonObject data)
    {
        try
        {
            var metrics = System.Text.Json.JsonSerializer.Deserialize<Metrics>(
                data.ToJsonString(),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = false }
            );

            if (metrics != null)
            {
                Metrics = metrics;
            }
        }
        catch
        {
            // Malformed metrics JSON -- ignore, keep last known state
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _pipe.Connected -= OnConnected;
        _pipe.Disconnected -= OnDisconnected;
        _pipe.EventReceived -= OnEventReceived;
    }
}
