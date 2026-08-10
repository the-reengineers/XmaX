using XmaX.Models;
using XmaX.ViewModels;

namespace XmaX.Tests;

/// <summary>
/// Tests for ProfilesViewModel validation and constraint checking.
/// These test the static validation methods and constraint logic without requiring a backend.
/// </summary>
public class ProfilesViewModelTests
{
    // ===== Fan curve validation =====

    [Fact]
    public void ValidateFanCurvePoints_ValidPoints_ReturnsTrue()
    {
        var points = new List<FanCurvePoint>
        {
            new() { TempC = 40, SpeedPercent = 20 },
            new() { TempC = 60, SpeedPercent = 50 },
            new() { TempC = 80, SpeedPercent = 100 },
        };

        var valid = ProfilesViewModel.ValidateFanCurvePoints(points, out var error);

        Assert.True(valid);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateFanCurvePoints_TooFewPoints_ReturnsFalse()
    {
        var points = new List<FanCurvePoint>
        {
            new() { TempC = 40, SpeedPercent = 20 },
        };

        var valid = ProfilesViewModel.ValidateFanCurvePoints(points, out var error);

        Assert.False(valid);
        Assert.NotNull(error);
        Assert.Contains("at least 2", error);
    }

    [Fact]
    public void ValidateFanCurvePoints_TooManyPoints_ReturnsFalse()
    {
        var points = Enumerable.Range(0, 11)
            .Select(i => new FanCurvePoint { TempC = 30 + i * 5, SpeedPercent = 10 + i * 5 })
            .ToList();

        var valid = ProfilesViewModel.ValidateFanCurvePoints(points, out var error);

        Assert.False(valid);
        Assert.NotNull(error);
        Assert.Contains("at most 10", error);
    }

    [Fact]
    public void ValidateFanCurvePoints_UnsortedTemps_ReturnsFalse()
    {
        var points = new List<FanCurvePoint>
        {
            new() { TempC = 60, SpeedPercent = 50 },
            new() { TempC = 40, SpeedPercent = 20 }, // Out of order
            new() { TempC = 80, SpeedPercent = 100 },
        };

        var valid = ProfilesViewModel.ValidateFanCurvePoints(points, out var error);

        Assert.False(valid);
        Assert.NotNull(error);
        Assert.Contains("sorted", error);
    }

    [Fact]
    public void ValidateFanCurvePoints_DuplicateTemps_ReturnsFalse()
    {
        var points = new List<FanCurvePoint>
        {
            new() { TempC = 40, SpeedPercent = 20 },
            new() { TempC = 40, SpeedPercent = 30 }, // Duplicate temp
            new() { TempC = 60, SpeedPercent = 50 },
        };

        var valid = ProfilesViewModel.ValidateFanCurvePoints(points, out var error);

        Assert.False(valid);
        Assert.NotNull(error);
        Assert.Contains("sorted", error); // Duplicate temps fail the ascending check
    }

    [Fact]
    public void ValidateFanCurvePoints_SpeedOutOfRange_ReturnsFalse()
    {
        var points = new List<FanCurvePoint>
        {
            new() { TempC = 40, SpeedPercent = 20 },
            new() { TempC = 60, SpeedPercent = 150 }, // Over 100
        };

        var valid = ProfilesViewModel.ValidateFanCurvePoints(points, out var error);

        Assert.False(valid);
        Assert.NotNull(error);
        Assert.Contains("0 and 100", error);
    }

    [Fact]
    public void ValidateFanCurvePoints_NegativeSpeed_ReturnsFalse()
    {
        var points = new List<FanCurvePoint>
        {
            new() { TempC = 40, SpeedPercent = 20 },
            new() { TempC = 60, SpeedPercent = -10 }, // Negative
        };

        var valid = ProfilesViewModel.ValidateFanCurvePoints(points, out var error);

        Assert.False(valid);
        Assert.NotNull(error);
        Assert.Contains("0 and 100", error);
    }

    [Fact]
    public void ValidateFanCurvePoints_BoundaryValues_ReturnsTrue()
    {
        // Min points (2), boundary speeds (0 and 100)
        var points = new List<FanCurvePoint>
        {
            new() { TempC = 0, SpeedPercent = 0 },
            new() { TempC = 100, SpeedPercent = 100 },
        };

        var valid = ProfilesViewModel.ValidateFanCurvePoints(points, out var error);

        Assert.True(valid);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateFanCurvePoints_MaxPoints_ReturnsTrue()
    {
        var points = Enumerable.Range(0, 10)
            .Select(i => new FanCurvePoint { TempC = 30 + i * 5, SpeedPercent = 10 + i * 5 })
            .ToList();

        var valid = ProfilesViewModel.ValidateFanCurvePoints(points, out var error);

        Assert.True(valid);
        Assert.Null(error);
    }
}
