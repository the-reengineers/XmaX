using XmaX.Models;

namespace XmaX.WidgetFramework;

/// <summary>
/// Single seam between widgets and the rest of the app. All widget commands
/// (apply profile, save fan curve, set charge limit, etc.) go through this
/// interface.
///
/// Widgets never call <see cref="Services.PipeClient"/> or services directly
/// for commands — they call <see cref="IWidgetApi"/> which handles validation,
/// error mapping, and logging centrally.
///
/// High-frequency data (metrics, power state) is still delivered via
/// <c>INotifyPropertyChanged</c> on the relevant services — that path is
/// push-based and doesn't belong here.
/// </summary>
public interface IWidgetApi
{
    // ── Profile operations ──────────────────────────────────────────────

    /// <summary>
    /// Apply a profile by ID. Sets TDP + fan curve on hardware and updates
    /// the active profile.
    /// </summary>
    Task<ApiResult> ApplyProfileAsync(string profileId);

    /// <summary>Save (create or update) a profile.</summary>
    Task<ApiResult> SaveProfileAsync(Profile profile);

    /// <summary>
    /// Delete a profile by ID. Fails if the profile is referenced by a
    /// power state or is currently active.
    /// </summary>
    Task<ApiResult> DeleteProfileAsync(string profileId);

    // ── Fan curve operations ────────────────────────────────────────────

    /// <summary>Save (create or update) a fan curve.</summary>
    Task<ApiResult> SaveFanCurveAsync(FanCurve curve);

    /// <summary>
    /// Delete a fan curve by ID. Fails if the curve is referenced by any
    /// profile.
    /// </summary>
    Task<ApiResult> DeleteFanCurveAsync(string curveId);

    // ── Hardware operations ─────────────────────────────────────────────

    /// <summary>
    /// Set the battery charge limit (percent). Used by
    /// <c>ChargeLimitWidget</c> to cycle through charge limit steps.
    /// </summary>
    Task<ApiResult> SetChargeLimitAsync(int percent);
}
