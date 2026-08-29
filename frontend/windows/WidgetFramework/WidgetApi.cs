using System.Text.Json.Nodes;
using XmaX.Models;
using XmaX.Services;

namespace XmaX.WidgetFramework;

/// <summary>
/// Concrete implementation of <see cref="IWidgetApi"/>. Mediates between widgets
/// and the backend (via <see cref="ProfileService"/> and <see cref="PipeClient"/>).
///
/// Each method:
/// 1. Validates inputs
/// 2. Calls the appropriate service / backend IPC
/// 3. Catches exceptions and maps them to <see cref="ApiResult"/>
/// 4. Logs the action
///
/// Widgets never see exceptions. They never see IPC details. They call, await,
/// handle the <see cref="ApiResult"/>.
/// </summary>
public sealed class WidgetApi : IWidgetApi
{
    private readonly ProfileService _profileService;
    private readonly PipeClient _pipe;

    public WidgetApi(ProfileService profileService, PipeClient pipe)
    {
        _profileService = profileService;
        _pipe = pipe;
    }

    // ── Profile operations ──────────────────────────────────────────────

    public async Task<ApiResult> ApplyProfileAsync(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return ApiResult.Fail("Profile ID is required", ApiErrorCode.InvalidInput);
        }

        try
        {
            Logger.Info($"[WidgetApi] Applying profile: {profileId}");
            await _profileService.ApplyProfileAsync(profileId).ConfigureAwait(false);
            return ApiResult.Ok();
        }
        catch (ObjectDisposedException)
        {
            Logger.Warn($"[WidgetApi] ApplyProfile failed: service disposed");
            return ApiResult.Fail("Profile service is not available", ApiErrorCode.Disconnected);
        }
        catch (Exception ex)
        {
            Logger.Error($"[WidgetApi] ApplyProfile failed: {ex.Message}");
            return ApiResult.FromException(ex);
        }
    }

    public async Task<ApiResult> SaveProfileAsync(Profile profile)
    {
        if (profile == null)
        {
            return ApiResult.Fail("Profile is required", ApiErrorCode.InvalidInput);
        }

        try
        {
            Logger.Info($"[WidgetApi] Saving profile: {profile.Id}");
            await _profileService.SaveProfileAsync(profile).ConfigureAwait(false);
            return ApiResult.Ok();
        }
        catch (ObjectDisposedException)
        {
            Logger.Warn($"[WidgetApi] SaveProfile failed: service disposed");
            return ApiResult.Fail("Profile service is not available", ApiErrorCode.Disconnected);
        }
        catch (Exception ex)
        {
            Logger.Error($"[WidgetApi] SaveProfile failed: {ex.Message}");
            return ApiResult.FromException(ex);
        }
    }

    public async Task<ApiResult> DeleteProfileAsync(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return ApiResult.Fail("Profile ID is required", ApiErrorCode.InvalidInput);
        }

        try
        {
            Logger.Info($"[WidgetApi] Deleting profile: {profileId}");
            await _profileService.DeleteProfileAsync(profileId).ConfigureAwait(false);
            return ApiResult.Ok();
        }
        catch (ObjectDisposedException)
        {
            Logger.Warn($"[WidgetApi] DeleteProfile failed: service disposed");
            return ApiResult.Fail("Profile service is not available", ApiErrorCode.Disconnected);
        }
        catch (Exception ex)
        {
            Logger.Error($"[WidgetApi] DeleteProfile failed: {ex.Message}");
            return ApiResult.FromException(ex);
        }
    }

    // ── Fan curve operations ────────────────────────────────────────────

    public async Task<ApiResult> SaveFanCurveAsync(FanCurve curve)
    {
        if (curve == null)
        {
            return ApiResult.Fail("Fan curve is required", ApiErrorCode.InvalidInput);
        }

        try
        {
            Logger.Info($"[WidgetApi] Saving fan curve: {curve.Id}");
            await _profileService.SaveFanCurveAsync(curve).ConfigureAwait(false);
            return ApiResult.Ok();
        }
        catch (ObjectDisposedException)
        {
            Logger.Warn($"[WidgetApi] SaveFanCurve failed: service disposed");
            return ApiResult.Fail("Profile service is not available", ApiErrorCode.Disconnected);
        }
        catch (Exception ex)
        {
            Logger.Error($"[WidgetApi] SaveFanCurve failed: {ex.Message}");
            return ApiResult.FromException(ex);
        }
    }

    public async Task<ApiResult> DeleteFanCurveAsync(string curveId)
    {
        if (string.IsNullOrWhiteSpace(curveId))
        {
            return ApiResult.Fail("Fan curve ID is required", ApiErrorCode.InvalidInput);
        }

        try
        {
            Logger.Info($"[WidgetApi] Deleting fan curve: {curveId}");
            await _profileService.DeleteFanCurveAsync(curveId).ConfigureAwait(false);
            return ApiResult.Ok();
        }
        catch (ObjectDisposedException)
        {
            Logger.Warn($"[WidgetApi] DeleteFanCurve failed: service disposed");
            return ApiResult.Fail("Profile service is not available", ApiErrorCode.Disconnected);
        }
        catch (Exception ex)
        {
            Logger.Error($"[WidgetApi] DeleteFanCurve failed: {ex.Message}");
            return ApiResult.FromException(ex);
        }
    }

    // ── Hardware operations ─────────────────────────────────────────────

    public async Task<ApiResult> SetChargeLimitAsync(int percent)
    {
        if (percent < 0 || percent > 100)
        {
            return ApiResult.Fail($"Charge limit must be 0-100, got {percent}", ApiErrorCode.InvalidInput);
        }

        try
        {
            Logger.Info($"[WidgetApi] Setting charge limit: {percent}%");
            var payload = new JsonObject { ["percent"] = percent };
            await _pipe.SendCommandAsync("set_charge_limit", payload).ConfigureAwait(false);
            return ApiResult.Ok();
        }
        catch (Exception ex)
        {
            Logger.Error($"[WidgetApi] SetChargeLimit failed: {ex.Message}");
            return ApiResult.FromException(ex);
        }
    }
}
