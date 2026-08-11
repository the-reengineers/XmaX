using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XmaX.Services;

/// <summary>
/// Named pipe client for IPC communication with the backend (xmaxsvc).
/// Handles command/response correlation, unsolicited events, and auto-reconnection.
/// </summary>
/// <remarks>
/// Thread safety: all public methods are safe to call from any thread.
/// Events are raised on background threads -- marshal to UI thread in handlers.
/// </remarks>
public sealed class PipeClient : IDisposable
{
    // Pipe name must match backend's platform_win32.cpp
    private const string PipeName = "xmaxsvc";

    // Command timeout per PROJECT.md: FE applies 5s timeout to all commands
    private const int CommandTimeoutMs = 5000;

    // Reconnect backoff bounds
    private const int ReconnectInitialDelayMs = 500;
    private const int ReconnectMaxDelayMs = 5000;
    private const double ReconnectBackoffMultiplier = 2.0;

    // Request ID counter (monotonic, thread-safe via Interlocked)
    private int _requestCounter;

    // Pending command correlation: request id -> completion source
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new();

    // Underlying pipe
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    // Background tasks
    private CancellationTokenSource? _cts;
    private Task? _reconnectTask;

    // State
    private bool _connected;
    private readonly object _connectLock = new();
    private bool _disposed;

    // ===== Events =====

    /// <summary>Raised when successfully connected to backend.</summary>
    public event Action? Connected;

    /// <summary>Raised when disconnected from backend.</summary>
    public event Action? Disconnected;

    /// <summary>Raised when an unsolicited event is received from backend.</summary>
    /// <param name="eventName">Event name (e.g., "button_press", "metrics").</param>
    /// <param name="data">Event payload as JsonObject.</param>
    public event Action<string, JsonObject>? EventReceived;

    /// <summary>
    /// Connect to the backend pipe and start the reader loop.
    /// If connection fails, automatically retries with exponential backoff.
    /// </summary>
    public void Connect()
    {
        lock (_connectLock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PipeClient));
            if (_connected) return;
        }

        _cts = new CancellationTokenSource();
        _reconnectTask = Task.Run(() => ReconnectLoop(_cts.Token));
    }

    /// <summary>
    /// Disconnect from backend and stop all background tasks.
    /// </summary>
    public void Disconnect()
    {
        lock (_connectLock)
        {
            if (!_connected && _reconnectTask == null) return;
        }

        _cts?.Cancel();
        ClosePipe();
        FailAllPending("Disconnected");

        lock (_connectLock)
        {
            _connected = false;
        }
    }

    /// <summary>
    /// Whether currently connected to the backend.
    /// </summary>
    public bool IsConnected
    {
        get { lock (_connectLock) return _connected; }
    }

    /// <summary>
    /// Send a command to the backend and await the response.
    /// </summary>
    /// <param name="method">Command method name (e.g., "get_metrics", "ping").</param>
    /// <param name="payload">Optional command payload (merged into the JSON).</param>
    /// <returns>Response data as JsonObject.</returns>
    /// <exception cref="TimeoutException">No response within 5 seconds.</exception>
    /// <exception cref="InvalidOperationException">Backend returned an error response.</exception>
    /// <exception cref="ObjectDisposedException">Client is disposed.</exception>
    public async Task<JsonObject> SendCommandAsync(string method, JsonObject? payload = null, CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PipeClient));

        var requestId = $"req_{Interlocked.Increment(ref _requestCounter)}";

        // Build command JSON
        var cmd = new JsonObject
        {
            ["type"] = "command",
            ["method"] = method,
            ["id"] = requestId
        };

        // Attach payload as a nested "params" object to avoid key collisions
        // (e.g., payload "id" would overwrite the correlation "id")
        if (payload != null)
        {
            cmd["params"] = payload.DeepClone();
        }

        // Register pending request before sending
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;

        try
        {
            // Send the command
            await WriteJsonAsync(cmd).ConfigureAwait(false);

            // Wait for response with timeout
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(CommandTimeoutMs);

            using var registration = timeoutCts.Token.Register(() =>
                tcs.TrySetException(new TimeoutException($"Command '{method}' timed out after {CommandTimeoutMs}ms")));

            return await tcs.Task.ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(requestId, out _);
            throw;
        }
    }

    // ===== Reconnect loop =====

    private async Task ReconnectLoop(CancellationToken ct)
    {
        var delayMs = ReconnectInitialDelayMs;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Attempt connection
                var pipe = new NamedPipeClientStream(
                    ".",
                    PipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous
                );

                await pipe.ConnectAsync(ct).ConfigureAwait(false);

                var reader = new StreamReader(pipe, Encoding.UTF8);
                var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };

                lock (_connectLock)
                {
                    _pipe = pipe;
                    _reader = reader;
                    _writer = writer;
                    _connected = true;
                }

                // Reset backoff on successful connection
                delayMs = ReconnectInitialDelayMs;

                Connected?.Invoke();

                // Run the read loop — server uses overlapped I/O so data is
                // delivered immediately without client-initiated pings.
                await ReadLoop(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Clean shutdown
                break;
            }
            catch (Exception)
            {
                // Connection failed or read loop errored -- fall through to reconnect
            }

            // Notify disconnect
            bool wasConnected;
            lock (_connectLock)
            {
                wasConnected = _connected;
                _connected = false;
            }
            if (wasConnected)
            {
                ClosePipe();
                FailAllPending("Disconnected");
                Disconnected?.Invoke();
            }

            // Wait before reconnecting (exponential backoff)
            try
            {
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
                delayMs = Math.Min((int)(delayMs * ReconnectBackoffMultiplier), ReconnectMaxDelayMs);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // ===== Read loop =====

    private async Task ReadLoop(CancellationToken ct)
    {
        if (_reader == null) return;

        while (!ct.IsCancellationRequested && _pipe?.IsConnected == true)
        {
            string? line;
            try
            {
                line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                break;
            }

            if (line == null)
            {
                break;
            }

            ProcessMessage(line);
        }
    }

    // ===== Message processing =====

    private void ProcessMessage(string json)
    {
        JsonObject? msg;
        try
        {
            msg = JsonNode.Parse(json)?.AsObject();
        }
        catch
        {
            return;
        }

        if (msg == null) return;

        var type = msg["type"]?.GetValue<string>();

        switch (type)
        {
            case "response":
                HandleResponse(msg);
                break;

            case "event":
                HandleEvent(msg);
                break;

            case "error":
                break;
        }
    }

    private void HandleResponse(JsonObject msg)
    {
        var id = msg["id"]?.GetValue<string>();
        if (id == null || !_pending.TryRemove(id, out var tcs))
        {
            // No matching pending request -- ignore (stale response or unknown id)
            return;
        }

        var ok = msg["ok"]?.GetValue<bool>() ?? false;
        if (ok)
        {
            var data = msg["data"]?.AsObject() ?? new JsonObject();
            tcs.TrySetResult(data);
        }
        else
        {
            var errorCode = msg["error"]?.GetValue<string>() ?? "unknown_error";
            tcs.TrySetException(new InvalidOperationException($"Backend error: {errorCode}"));
        }
    }

    private void HandleEvent(JsonObject msg)
    {
        var eventName = msg["event"]?.GetValue<string>();
        if (eventName == null) return;

        var data = msg["data"]?.AsObject() ?? new JsonObject();
        EventReceived?.Invoke(eventName, data);
    }

    // ===== Helpers =====

    private async Task WriteJsonAsync(JsonObject obj)
    {
        if (_writer == null) throw new InvalidOperationException("Not connected");

        var json = obj.ToJsonString();
        await _writer.WriteLineAsync(json).ConfigureAwait(false);
    }

    private void ClosePipe()
    {
        try { _writer?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        _writer = null;
        _reader = null;
        _pipe = null;
    }

    private void FailAllPending(string reason)
    {
        foreach (var kvp in _pending)
        {
            if (_pending.TryRemove(kvp.Key, out var tcs))
            {
                tcs.TrySetException(new InvalidOperationException(reason));
            }
        }
    }

    // ===== IDisposable =====

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();
        ClosePipe();
        FailAllPending("Disposed");
        _cts?.Dispose();
    }
}
