using System.Collections.ObjectModel;
using System.ComponentModel;
using XmaX.Models;
using XmaX.Services;

namespace XmaX.ViewModels;

/// <summary>
/// ViewModel for the Profiles page. Manages profile CRUD and fan curve CRUD.
/// </summary>
public sealed class ProfilesViewModel : INotifyPropertyChanged
{
    private readonly ProfileService _profileService;

    // Selected items for editing
    private Profile? _selectedProfile;

    public ProfilesViewModel(ProfileService profileService)
    {
        _profileService = profileService;
        _profileService.PropertyChanged += OnProfileServiceChanged;
    }

    // ===== Observable properties =====

    /// <summary>All saved profiles (from ProfileService).</summary>
    public ObservableCollection<Profile> Profiles => _profileService.Profiles;

    /// <summary>All saved fan curves (from ProfileService).</summary>
    public ObservableCollection<FanCurve> FanCurves => _profileService.FanCurves;

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
    /// Create a new fixed profile with the given name, TDP values, fan curve, and optional power state.
    /// </summary>
    public async Task CreateFixedProfileAsync(string name, int stapm, int fast, int slow, string fanCurveId, string? powerState = null)
    {
        var slug = GenerateSlug(name);
        var profile = new Profile
        {
            Id = slug,
            Name = name,
            Type = "fixed",
            PowerState = powerState,
            Tdp = new TdpLimits { Stapm = stapm, Fast = fast, Slow = slow },
            FanCurve = fanCurveId,
        };

        await _profileService.SaveProfileAsync(profile).ConfigureAwait(false);
    }

    /// <summary>
    /// Create a new adaptive profile with the given config and optional power state.
    /// </summary>
    public async Task CreateAdaptiveProfileAsync(string name, string tuning, int targetTempC, int tdpMaxW, int fanMaxPct, string? powerState = null)
    {
        var slug = GenerateSlug(name);
        var profile = new Profile
        {
            Id = slug,
            Name = name,
            Type = "adaptive",
            PowerState = powerState,
            Tuning = tuning,
            TargetTempC = targetTempC,
            TdpMaxW = tdpMaxW,
            FanMaxPercent = fanMaxPct,
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
    /// Delete a profile by ID.
    /// </summary>
    public async Task DeleteProfileAsync(string profileId)
    {
        await _profileService.DeleteProfileAsync(profileId).ConfigureAwait(false);

        if (SelectedProfile?.Id == profileId)
        {
            SelectedProfile = null;
        }
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
