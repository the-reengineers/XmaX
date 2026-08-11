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

        // Check profiles only
        while (Profiles.Any(p => p.Id == slug))
        {
            slug = $"{baseSlug}-{counter}";
            counter++;
        }

        return slug;
    }
}
