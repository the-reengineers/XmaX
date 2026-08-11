using System.Collections.ObjectModel;
using System.ComponentModel;
using XmaX.Models;
using XmaX.Services;

namespace XmaX.ViewModels;

/// <summary>
/// ViewModel for the Cooling page. Manages fan curve CRUD.
/// </summary>
public sealed class CoolingViewModel : INotifyPropertyChanged
{
    private readonly ProfileService _profileService;

    // Selected items for editing
    private FanCurve? _selectedFanCurve;

    // Fan curve editor state
    private ObservableCollection<FanCurvePoint> _editingPoints = new();

    public CoolingViewModel(ProfileService profileService)
    {
        _profileService = profileService;
        _profileService.PropertyChanged += OnProfileServiceChanged;
    }

    // ===== Observable properties =====

    /// <summary>All saved fan curves (from ProfileService).</summary>
    public ObservableCollection<FanCurve> FanCurves => _profileService.FanCurves;

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

    // ===== Fan Curve CRUD =====

    /// <summary>
    /// Create a new fan curve with the given name and points.
    /// </summary>
    public async Task CreateFanCurveAsync(string name, List<FanCurvePoint> points)
    {
        // Sort points by temperature before validation
        var sortedPoints = points.OrderBy(p => p.TempC).ToList();

        if (!ValidateFanCurvePoints(sortedPoints, out var error))
        {
            throw new ArgumentException(error);
        }

        var slug = GenerateSlug(name);
        var curve = new FanCurve
        {
            Id = slug,
            Name = name,
            Points = sortedPoints,
        };

        await _profileService.SaveFanCurveAsync(curve).ConfigureAwait(false);
    }

    /// <summary>
    /// Update an existing fan curve.
    /// </summary>
    public async Task UpdateFanCurveAsync(FanCurve curve)
    {
        // Sort points by temperature before validation
        curve.Points = curve.Points.OrderBy(p => p.TempC).ToList();

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
        foreach (var profile in _profileService.Profiles)
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
    /// Validate fan curve points (2-10 points, sorted by temp, no duplicates, temp 0-100, speed 0-100).
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

        // Check each point's ranges and ordering
        for (int i = 0; i < points.Count; i++)
        {
            var point = points[i];

            // Check temperature range
            if (point.TempC < 0 || point.TempC > 100)
            {
                error = Loc.Error_FanCurveTempRange;
                return false;
            }

            // Check speed range
            if (point.SpeedPercent < 0 || point.SpeedPercent > 100)
            {
                error = Loc.Error_FanCurveSpeedRange;
                return false;
            }

            // Check sorted and no duplicates (points must already be sorted by caller)
            if (i > 0 && point.TempC <= points[i - 1].TempC)
            {
                error = Loc.Error_FanCurveSorted;
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

    // ===== Helpers =====

    private void OnProfileServiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfileService.FanCurves))
        {
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

        // Check fan curves only
        while (FanCurves.Any(f => f.Id == slug))
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
