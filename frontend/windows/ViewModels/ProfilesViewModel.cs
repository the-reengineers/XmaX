using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using XmaX.Models;
using XmaX.Services;

namespace XmaX.ViewModels;

/// <summary>
/// ViewModel for the Profiles page. Manages profile CRUD, fan curve CRUD, and power state assignments.
/// </summary>
public sealed class ProfilesViewModel : INotifyPropertyChanged
{
    private readonly ProfileService _profileService;
    private readonly PipeClient _pipe;

    // Config state (loaded from backend)
    private AppConfig _config = new();

    // Selected items for editing
    private Profile? _selectedProfile;
    private FanCurve? _selectedFanCurve;

    // Fan curve editor state
    private ObservableCollection<FanCurvePoint> _editingPoints = new();

    public ProfilesViewModel(ProfileService profileService, PipeClient pipe)
    {
        _profileService = profileService;
        _pipe = pipe;

        _profileService.PropertyChanged += OnProfileServiceChanged;

        // Load config on creation
        _ = LoadConfigAsync();
    }

    // ===== Observable properties =====

    /// <summary>All saved profiles (from ProfileService).</summary>
    public ObservableCollection<Profile> Profiles => _profileService.Profiles;

    /// <summary>All saved fan curves (from ProfileService).</summary>
    public ObservableCollection<FanCurve> FanCurves => _profileService.FanCurves;

    /// <summary>Current app config (includes power state assignments).</summary>
    public AppConfig Config
    {
        get => _config;
        private set
        {
            if (ReferenceEquals(_config, value)) return;
            _config = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Config)));
        }
    }

    /// <summary>Currently selected profile for editing.</summary>
    public Profile? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (_selectedProfile == value) return;
            _selectedProfile = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedProfile)));
        }
    }

    /// <summary>Currently selected fan curve for editing.</summary>
    public FanCurve? SelectedFanCurve
    {
        get => _selectedFanCurve;
        set
        {
            if (_selectedFanCurve == value) return;
            _selectedFanCurve = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedFanCurve)));

            // Load points into editor
            if (value != null)
            {
                EditingPoints = new ObservableCollection<FanCurvePoint>(value.Points);
            }
            else
            {
                EditingPoints.Clear();
            }
        }
    }

    /// <summary>Fan curve points being edited (for the fan curve editor UI).</summary>
    public ObservableCollection<FanCurvePoint> EditingPoints
    {
        get => _editingPoints;
        private set
        {
            if (ReferenceEquals(_editingPoints, value)) return;
            _editingPoints = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EditingPoints)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // ===== Profile CRUD =====

    /// <summary>
    /// Create a new profile with the given name, TDP values, and fan curve.
    /// </summary>
    public async Task CreateProfileAsync(string name, int stapm, int fast, int slow, string? fanCurveId)
    {
        var slug = GenerateSlug(name);
        var profile = new Profile
        {
            Id = slug,
            Name = name,
            Tdp = new TdpLimits { Stapm = stapm, Fast = fast, Slow = slow },
            FanCurve = fanCurveId,
        };

        await _profileService.SaveProfileAsync(profile).ConfigureAwait(false);
    }

    /// <summary>
    /// Update an existing profile.
    /// </summary>
    public async Task UpdateProfileAsync(Profile profile)
    {
        await _profileService.SaveProfileAsync(profile).ConfigureAwait(false);
    }

    /// <summary>
    /// Delete a profile by ID. Fails if referenced by a power state.
    /// </summary>
    public async Task DeleteProfileAsync(string profileId)
    {
        // Check if profile is referenced by a power state
        if (IsProfileInUse(profileId, out var stateName))
        {
            throw new InvalidOperationException(Loc.F("error.profile_in_use", stateName));
        }

        await _profileService.DeleteProfileAsync(profileId).ConfigureAwait(false);

        if (SelectedProfile?.Id == profileId)
        {
            SelectedProfile = null;
        }
    }

    /// <summary>
    /// Check if a profile is referenced by any power state assignment.
    /// </summary>
    public bool IsProfileInUse(string profileId, out string? stateName)
    {
        stateName = null;
        var psp = Config.PowerStateProfiles;

        if (psp.Battery.Profile == profileId) { stateName = Loc.Power_Battery; return true; }
        if (psp.UsbCSlow.Profile == profileId) { stateName = Loc.Power_UsbCSlow; return true; }
        if (psp.UsbCFast.Profile == profileId) { stateName = Loc.Power_UsbCFast; return true; }
        if (psp.DcIn.Profile == profileId) { stateName = Loc.Power_DcIn; return true; }

        return false;
    }

    // ===== Fan Curve CRUD =====

    /// <summary>
    /// Create a new fan curve with the given name and points.
    /// </summary>
    public async Task CreateFanCurveAsync(string name, List<FanCurvePoint> points)
    {
        if (!ValidateFanCurvePoints(points, out var error))
        {
            throw new ArgumentException(error);
        }

        var slug = GenerateSlug(name);
        var curve = new FanCurve
        {
            Id = slug,
            Name = name,
            Points = points,
        };

        await _profileService.SaveFanCurveAsync(curve).ConfigureAwait(false);
    }

    /// <summary>
    /// Update an existing fan curve.
    /// </summary>
    public async Task UpdateFanCurveAsync(FanCurve curve)
    {
        if (!ValidateFanCurvePoints(curve.Points, out var error))
        {
            throw new ArgumentException(error);
        }

        await _profileService.SaveFanCurveAsync(curve).ConfigureAwait(false);
    }

    /// <summary>
    /// Delete a fan curve by ID. Fails if referenced by any profile.
    /// </summary>
    public async Task DeleteFanCurveAsync(string curveId)
    {
        // Check if curve is referenced by any profile
        if (IsFanCurveInUse(curveId, out var profileName))
        {
            throw new InvalidOperationException(Loc.F("error.fan_curve_in_use", profileName));
        }

        await _profileService.DeleteFanCurveAsync(curveId).ConfigureAwait(false);

        if (SelectedFanCurve?.Id == curveId)
        {
            SelectedFanCurve = null;
        }
    }

    /// <summary>
    /// Check if a fan curve is referenced by any profile.
    /// </summary>
    public bool IsFanCurveInUse(string curveId, out string? profileName)
    {
        profileName = null;
        foreach (var profile in Profiles)
        {
            if (profile.FanCurve == curveId)
            {
                profileName = profile.Name;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Validate fan curve points (2-10 points, sorted by temp, speed 0-100).
    /// </summary>
    public static bool ValidateFanCurvePoints(List<FanCurvePoint> points, out string? error)
    {
        error = null;

        if (points.Count < 2)
        {
            error = Loc.Error_FanCurveMinPoints;
            return false;
        }

        if (points.Count > 10)
        {
            error = Loc.Error_FanCurveMaxPoints;
            return false;
        }

        // Check sorted by temp
        for (int i = 1; i < points.Count; i++)
        {
            if (points[i].TempC <= points[i - 1].TempC)
            {
                error = Loc.Error_FanCurveSorted;
                return false;
            }
        }

        // Check speed range
        foreach (var point in points)
        {
            if (point.SpeedPercent < 0 || point.SpeedPercent > 100)
            {
                error = Loc.Error_FanCurveSpeedRange;
                return false;
            }
        }

        return true;
    }

    // ===== Fan Curve Editor =====

    /// <summary>Add a point to the editing list.</summary>
    public void AddPoint(int tempC, int speedPercent)
    {
        EditingPoints.Add(new FanCurvePoint { TempC = tempC, SpeedPercent = speedPercent });
        // Sort by temp
        var sorted = EditingPoints.OrderBy(p => p.TempC).ToList();
        EditingPoints = new ObservableCollection<FanCurvePoint>(sorted);
    }

    /// <summary>Remove a point from the editing list.</summary>
    public void RemovePoint(FanCurvePoint point)
    {
        EditingPoints.Remove(point);
    }

    /// <summary>Save the current editing points to the selected fan curve.</summary>
    public async Task SaveEditingPointsToCurveAsync()
    {
        if (SelectedFanCurve == null) return;

        SelectedFanCurve.Points = new List<FanCurvePoint>(EditingPoints);
        await UpdateFanCurveAsync(SelectedFanCurve).ConfigureAwait(false);
    }

    // ===== Power State Assignments =====

    /// <summary>
    /// Set the profile and TDP ceiling for a power state.
    /// </summary>
    public async Task SetPowerStateAssignmentAsync(string stateId, string profileSlug, int tdpMaxW)
    {
        var payload = new JsonObject
        {
            ["state"] = stateId,
            ["profile"] = profileSlug,
            ["tdp_max_w"] = tdpMaxW,
        };

        await _pipe.SendCommandAsync("set_power_profile", payload).ConfigureAwait(false);

        // Update local config
        UpdatePowerStateInConfig(stateId, profileSlug, tdpMaxW);

        // Save config
        await SaveConfigAsync().ConfigureAwait(false);
    }

    private void UpdatePowerStateInConfig(string stateId, string profileSlug, int tdpMaxW)
    {
        var assignment = new PowerStateAssignment { Profile = profileSlug, TdpMaxW = tdpMaxW };
        var psp = Config.PowerStateProfiles;

        switch (stateId)
        {
            case "battery": psp.Battery = assignment; break;
            case "usb_c_slow": psp.UsbCSlow = assignment; break;
            case "usb_c_fast": psp.UsbCFast = assignment; break;
            case "dc_in": psp.DcIn = assignment; break;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Config)));
    }

    // ===== Config persistence =====

    /// <summary>Load config from backend.</summary>
    public async Task LoadConfigAsync()
    {
        try
        {
            var data = await _pipe.SendCommandAsync("get_config").ConfigureAwait(false);
            var config = JsonSerializer.Deserialize<AppConfig>(
                data.ToJsonString(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = false }
            );

            if (config != null)
            {
                Config = config;
            }
        }
        catch
        {
            // Failed to load config — use defaults
        }
    }

    /// <summary>Save current config to backend.</summary>
    public async Task SaveConfigAsync()
    {
        var payload = new JsonObject
        {
            ["power_state_profiles"] = JsonSerializer.SerializeToNode(Config.PowerStateProfiles)?.AsObject(),
        };

        await _pipe.SendCommandAsync("set_config", payload).ConfigureAwait(false);
    }

    // ===== Helpers =====

    private void OnProfileServiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfileService.Profiles) ||
            e.PropertyName == nameof(ProfileService.FanCurves))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Profiles)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FanCurves)));
        }
    }

    /// <summary>
    /// Generate a slug from a name (lowercase, spaces to hyphens, strip special chars).
    /// Handles collisions by appending -2, -3, etc.
    /// </summary>
    private string GenerateSlug(string name)
    {
        var slug = name.ToLowerInvariant()
            .Replace(" ", "-")
            .ReplaceAll(new[] { "_", ".", ",", "!", "@", "#", "$", "%", "^", "&", "*", "(", ")", "+", "=", "{", "}", "[", "]", "|", "\\", ":", ";", "\"", "'", "<", ">", "?", "/" }, "");

        // Check for collisions
        var baseSlug = slug;
        var counter = 2;

        // Check profiles
        while (Profiles.Any(p => p.Id == slug) || FanCurves.Any(f => f.Id == slug))
        {
            slug = $"{baseSlug}-{counter}";
            counter++;
        }

        return slug;
    }
}

/// <summary>Extension method for string replacement.</summary>
internal static class StringExtensions
{
    public static string ReplaceAll(this string str, string[] oldValues, string newValue)
    {
        foreach (var old in oldValues)
        {
            str = str.Replace(old, newValue);
        }
        return str;
    }
}
