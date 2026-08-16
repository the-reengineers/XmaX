using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using XmaX.Models;

namespace XmaX.Services;

/// <summary>
/// Service that wraps PipeClient for profile and fan curve management.
/// Exposes observable collections of profiles and fan curves, handles CRUD commands.
/// </summary>
public sealed class ProfileService : INotifyPropertyChanged, IDisposable
{
    private readonly PipeClient _pipe;
    private ObservableCollection<Profile> _profiles = new();
    private ObservableCollection<FanCurve> _fanCurves = new();
    private string? _activeProfileId;
    private bool _disposed;

    public ProfileService(PipeClient pipe)
    {
        _pipe = pipe;
        _pipe.Connected += OnConnected;
        _pipe.EventReceived += OnEventReceived;
    }

    /// <summary>All saved profiles.</summary>
    public ObservableCollection<Profile> Profiles
    {
        get => _profiles;
        private set
        {
            _profiles = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Profiles)));
        }
    }

    /// <summary>All saved fan curves.</summary>
    public ObservableCollection<FanCurve> FanCurves
    {
        get => _fanCurves;
        private set
        {
            _fanCurves = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FanCurves)));
        }
    }

    /// <summary>Slug of the currently active profile, or null if no profile is active.</summary>
    public string? ActiveProfileId
    {
        get => _activeProfileId;
        private set
        {
            if (_activeProfileId == value) return;
            _activeProfileId = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveProfileId)));
        }
    }

    /// <summary>Whether an adaptive profile is currently active.</summary>
    public bool IsAdaptiveActive
    {
        get => _isAdaptiveActive;
        private set
        {
            if (_isAdaptiveActive == value) return;
            _isAdaptiveActive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAdaptiveActive)));
        }
    }

    private bool _isAdaptiveActive;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Fetch all profiles and fan curves from the backend.
    /// Call on connect to populate the collections.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ProfileService));

        // Fetch profiles
        var profilesData = await _pipe.SendCommandAsync("get_profiles").ConfigureAwait(false);
        var profiles = DeserializeList<Profile>(profilesData, "profiles");
        Profiles = new ObservableCollection<Profile>(profiles);

        // Fetch fan curves
        var curvesData = await _pipe.SendCommandAsync("get_fan_curves").ConfigureAwait(false);
        var curves = DeserializeList<FanCurve>(curvesData, "fan_curves");
        FanCurves = new ObservableCollection<FanCurve>(curves);
    }

    /// <summary>
    /// Apply a profile by slug ID. Sets TDP + fan curve on hardware.
    /// This is a hardware write -- rejected when persist=false.
    /// Deactivates adaptive controller if active.
    /// </summary>
    public async Task ApplyProfileAsync(string profileId)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ProfileService));

        var payload = new JsonObject { ["id"] = profileId };
        await _pipe.SendCommandAsync("set_profile", payload).ConfigureAwait(false);
        ActiveProfileId = profileId;
    }

    /// <summary>
    /// Save (create or update) a profile.
    /// </summary>
    public async Task SaveProfileAsync(Profile profile)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ProfileService));

        var payload = new JsonObject
        {
            ["id"] = profile.Id,
            ["name"] = profile.Name,
            ["type"] = profile.Type,
            ["power_state"] = profile.PowerState,
            ["is_default"] = profile.IsDefault,
        };

        if (profile.IsAdaptive)
        {
            payload["tuning"] = profile.Tuning;
            payload["target_temp_c"] = profile.TargetTempC;
            payload["tdp_max_w"] = profile.TdpMaxW;
            payload["fan_max_pct"] = profile.FanMaxPercent;
        }
        else
        {
            payload["stapm"] = profile.Tdp.Stapm;
            payload["fast"] = profile.Tdp.Fast;
            payload["slow"] = profile.Tdp.Slow;
            payload["fan_curve"] = profile.FanCurve;
        }

        await _pipe.SendCommandAsync("save_profile", payload).ConfigureAwait(false);
        await RefreshAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Delete a profile by slug ID.
    /// Fails if the profile is referenced by a power state.
    /// </summary>
    public async Task DeleteProfileAsync(string profileId)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ProfileService));

        var payload = new JsonObject { ["id"] = profileId };
        await _pipe.SendCommandAsync("delete_profile", payload).ConfigureAwait(false);

        if (ActiveProfileId == profileId)
        {
            ActiveProfileId = null;
        }

        await RefreshAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Save (create or update) a fan curve.
    /// </summary>
    public async Task SaveFanCurveAsync(FanCurve curve)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ProfileService));

        var pointsArray = new JsonArray();
        foreach (var point in curve.Points)
        {
            pointsArray.Add(new JsonObject
            {
                ["temp_c"] = point.TempC,
                ["speed_pct"] = point.SpeedPercent
            });
        }

        var payload = new JsonObject
        {
            ["id"] = curve.Id,
            ["name"] = curve.Name,
            ["points"] = pointsArray
        };

        await _pipe.SendCommandAsync("save_fan_curve", payload).ConfigureAwait(false);
        await RefreshAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Delete a fan curve by slug ID.
    /// Fails if the curve is referenced by any profile.
    /// </summary>
    public async Task DeleteFanCurveAsync(string curveId)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ProfileService));

        var payload = new JsonObject { ["id"] = curveId };
        await _pipe.SendCommandAsync("delete_fan_curve", payload).ConfigureAwait(false);
        await RefreshAsync().ConfigureAwait(false);
    }

    private void OnConnected()
    {
        // Fire-and-forget refresh -- errors are silently ignored
        _ = RefreshAsync();
    }

    private void OnEventReceived(string eventName, JsonObject data)
    {
        // Track adaptive controller state changes
        if (eventName == "auto_tune_state")
        {
            var active = data["active"]?.GetValue<bool>() ?? false;
            IsAdaptiveActive = active;
            if (active)
            {
                // Adaptive profile became active -- clear fixed profile ID
                ActiveProfileId = null;
            }
        }
        else if (eventName == "auto_tune_adjust")
        {
            // Adaptive controller is running -- ensure we're marked as active
            IsAdaptiveActive = true;
        }
    }

    /// <summary>
    /// Find the default profile assigned to a specific power state.
    /// </summary>
    public Profile? GetDefaultProfileForPowerState(string powerState)
    {
        return Profiles.FirstOrDefault(p => p.PowerState == powerState && p.IsDefault);
    }

    /// <summary>
    /// Get all profiles assigned to a specific power state.
    /// </summary>
    public IEnumerable<Profile> GetProfilesForPowerState(string powerState)
    {
        return Profiles.Where(p => p.PowerState == powerState);
    }

    private static List<T> DeserializeList<T>(JsonObject data, string key)
    {
        // Backend returns: { "profiles": [ { "id": "slug", ... }, ... ] }
        // or: { "fan_curves": [ { "id": "slug", ... }, ... ] }
        // Each element already includes its slug as the "id" field.
        var list = new List<T>();
        var array = data[key]?.AsArray();
        if (array == null) return list;

        foreach (var element in array)
        {
            var obj = element?.AsObject();
            if (obj == null) continue;

            var item = JsonSerializer.Deserialize<T>(
                obj.ToJsonString(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = false }
            );
            if (item != null) list.Add(item);
        }

        return list;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _pipe.Connected -= OnConnected;
        _pipe.EventReceived -= OnEventReceived;
    }
}
