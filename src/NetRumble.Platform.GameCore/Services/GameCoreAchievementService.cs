using GDK.Net;
using GDK.Net.XboxLive;
using NetRumble.Platform.GameCore.Interop;

namespace NetRumble.Platform.GameCore.Services;

/// <summary>
/// GDK achievement unlock and progress, over GDK.Net's
/// <see cref="AchievementsService"/>.
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>scripts/services/achievement_service.gd</c>, itself a port of
/// <c>Platforms/gdk-x64/Managers/GDKAchievementsManager.cpp</c>. This title has ten
/// achievements, ids <c>"1"</c> through <c>"10"</c>, defined in the service
/// configuration and mapped to gameplay by
/// <c>NetRumble.Game.Profile.AchievementTracker</c>. Some are one-shot (unlocked with a
/// single <see cref="SetProgressAsync"/> call at <c>100</c>) and some are progressive
/// (kill counts, "fire every weapon", "play every mode"), so
/// <see cref="SetProgressAsync"/> passes the percentage straight through to the Xbox
/// Services API, which is itself fully progressive - the id/100 special-casing belongs
/// to the Godot source's C++ predecessor, not to the GDK.
/// </para>
/// <para>
/// <b>Idempotent by design.</b> GDK.Net's <see cref="AchievementsService.UpdateAsync"/>
/// treats a repeated or lower value as a no-op (the service returns
/// <c>HTTP_E_STATUS_NOT_MODIFIED</c>, which GDK.Net folds into a successful completion),
/// so a title may call this freely without tracking what it last sent.
/// </para>
/// <para>
/// A real unlock round trip has not been observed. It needs a signed-in Xbox user, a
/// correctly configured title and a live Xbox Services connection; see
/// <c>docs/known-gaps.md</c>.
/// </para>
/// </remarks>
internal sealed class GameCoreAchievementService : IAchievementService
{
    private readonly GameCoreRuntime _runtime;
    private readonly GameCoreIdentityService _identity;
    private readonly GameCoreXblContext _xbl;

    internal GameCoreAchievementService(GameCoreRuntime runtime, GameCoreIdentityService identity, GameCoreXblContext xbl)
    {
        _runtime = runtime;
        _identity = identity;
        _xbl = xbl;
    }

    public Task<PlatformResult> UnlockAsync(string achievementId, CancellationToken cancellationToken = default)
        => SetProgressAsync(achievementId, 100, cancellationToken);

    public async Task<PlatformResult> SetProgressAsync(
        string achievementId,
        int percentComplete,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(achievementId))
        {
            // Matches the Godot source's own early-out for an empty id: a no-op, not an
            // error, since there is nothing to report.
            return PlatformResult.Ok();
        }

        if (!_runtime.IsInitialized || !_identity.TryGetUser(out _))
        {
            return PlatformResult.Unavailable("No Xbox session is available to report achievement progress.");
        }

        if (!_xbl.TryGetContext(out var context))
        {
            return PlatformResult.Unavailable("Xbox Services is not available in this build.");
        }

        var xuid = context.XboxUserId;
        var clampedPercent = (uint)Math.Clamp(percentComplete, 0, 100);

        try
        {
            await _runtime.Dispatcher
                .Marshal(context.Achievements.UpdateAsync(xuid, achievementId, clampedPercent, cancellationToken))
                .ConfigureAwait(false);

            return PlatformResult.Ok();
        }
        catch (GameRuntimeException ex)
        {
            // Best-effort per the interface contract: logged, never surfaced as a hard
            // failure that would interrupt gameplay over a missed achievement report.
            return PlatformResult.Fail(
                PlatformStatus.Failed,
                "The achievement could not be updated.",
                ex.Message);
        }
        catch (OperationCanceledException)
        {
            return PlatformResult.Fail(PlatformStatus.Failed, "The achievement update was canceled.");
        }
    }
}
