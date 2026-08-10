using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using XmaX.Models;

namespace XmaX.Services;

/// <summary>
/// Service that wraps PipeClient for the adaptive controller.
/// Exposes the current auto_tune state and config, handles set_auto_tune commands.
/// </summary>
public sealed class AutoTuneService : INotifyPropertyChanged, IDisposable
{
    private readonly PipeClient _pipe;
    private AutoTuneState _state = new();
    private bool _disposed;

    public AutoTuneService(PipeClient pipe)
    {
        _pipe = pipe;
        _pipe.Connected += OnConnected;
        _pipe.EventReceived += OnEventReceived;
    }

    /// <summary>
    /// Current adaptive controller state (active, tuning, temps, TDP, fan).
    /// Includes the effective TDP ceiling (after power state clamping).
    /// </summary>
    public AutoTuneState State
    {
        get => _state;
        private set
        {
            if (ReferenceEquals(_state, value)) return;
            _state = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
        }
    }

    /// <summary>Whether the adaptive controller is currently the active mode.</summary>
    public bool IsActive => _state.Active;

    /// <summary>Current tuning preset name.</summary>
    public string Tuning => _state.Tuning;

    /// <summary>Effective TDP ceiling in watts (after power state clamping).</summary>
    public int EffectiveTdpMaxW => _state.EffectiveTdpMaxW;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Fetch the current auto_tune state from the backend.
    /// Call on connect to populate state.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AutoTuneService));

        var data = await _pipe.SendCommandAsync("get_auto_tune").ConfigureAwait(false);
        UpdateStateFromJson(data);
    }

    /// <summary>
    /// Activate or reconfigure the adaptive controller.
    /// This is a hardware write -- rejected when persist=false.
    /// Deactivates the active profile (mutually exclusive).
    /// </summary>
    /// <param name="tuning">Preset: "silent", "default", or "performance".</param>
    /// <param name="targetTempC">Target temperature in Celsius.</param>
    /// <param name="tdpMaxW">TDP ceiling in watts (clamped by power state at runtime).</param>
    /// <param name="fanMaxPct">Maximum fan speed percentage.</param>
    public async Task SetAutoTuneAsync(string tuning, int targetTempC, int tdpMaxW, int fanMaxPct)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AutoTuneService));

        var payload = new JsonObject
        {
            ["tuning"] = tuning,
            ["target_temp_c"] = targetTempC,
            ["tdp_max_w"] = tdpMaxW,
            ["fan_max_pct"] = fanMaxPct
        };

        await _pipe.SendCommandAsync("set_auto_tune", payload).ConfigureAwait(false);

        // Update local state optimistically -- backend will send auto_tune_adjust events
        // with actual values as the controller runs
        State = new AutoTuneState
        {
            Active = true,
            Tuning = tuning,
            TargetTempC = targetTempC,
            TdpMaxW = tdpMaxW,
            EffectiveTdpMaxW = tdpMaxW, // May be clamped by power state
            FanMaxPercent = fanMaxPct
        };
    }

    /// <summary>
    /// Deactivate the adaptive controller (revert to profile mode).
    /// The user must then select a profile via ProfileService.ApplyProfileAsync.
    /// </summary>
    public async Task DeactivateAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AutoTuneService));

        // Send set_auto_tune with enabled=false equivalent.
        // Per protocol, the backend deactivates adaptive when a profile is applied.
        // There's no explicit "disable" command -- applying a profile deactivates adaptive.
        // This method is a convenience for UI that wants to explicitly turn off adaptive.
        // The actual deactivation happens when the user selects a profile.
        // We just update local state here.
        State = new AutoTuneState
        {
            Active = false,
            Tuning = _state.Tuning,
            TargetTempC = _state.TargetTempC,
            TdpMaxW = _state.TdpMaxW,
            EffectiveTdpMaxW = _state.EffectiveTdpMaxW,
            FanMaxPercent = _state.FanMaxPercent
        };
    }

    private void OnConnected()
    {
        _ = RefreshAsync();
    }

    private void OnEventReceived(string eventName, JsonObject data)
    {
        if (eventName == "auto_tune_adjust")
        {
            // Controller applied a change -- update effective TDP and fan
            var effectiveTdp = data["effective_tdp_max_w"]?.GetValue<int>() ?? _state.EffectiveTdpMaxW;
            var tdpW = data["tdp_w"]?.GetValue<int>() ?? _state.TdpMaxW;
            var fanPct = data["fan_pct"]?.GetValue<int>() ?? _state.FanMaxPercent;

            State = new AutoTuneState
            {
                Active = true,
                Tuning = data["tuning"]?.GetValue<string>() ?? _state.Tuning,
                TargetTempC = data.ContainsKey("target_temp_c")
                    ? data["target_temp_c"]!.GetValue<int>()
                    : _state.TargetTempC,
                TdpMaxW = tdpW,
                EffectiveTdpMaxW = effectiveTdp,
                FanMaxPercent = fanPct
            };
        }
        else if (eventName == "auto_tune_state")
        {
            // Adaptive became active or inactive
            var active = data["active"]?.GetValue<bool>() ?? false;
            State = new AutoTuneState
            {
                Active = active,
                Tuning = _state.Tuning,
                TargetTempC = _state.TargetTempC,
                TdpMaxW = _state.TdpMaxW,
                EffectiveTdpMaxW = _state.EffectiveTdpMaxW,
                FanMaxPercent = _state.FanMaxPercent
            };
        }
    }

    private void UpdateStateFromJson(JsonObject data)
    {
        try
        {
            var state = JsonSerializer.Deserialize<AutoTuneState>(
                data.ToJsonString(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = false }
            );

            if (state != null)
            {
                State = state;
            }
        }
        catch
        {
            // Malformed JSON -- ignore
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _pipe.Connected -= OnConnected;
        _pipe.EventReceived -= OnEventReceived;
    }
}
