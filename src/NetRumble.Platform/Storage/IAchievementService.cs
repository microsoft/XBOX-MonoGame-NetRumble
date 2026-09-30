namespace NetRumble.Platform;

/// <summary>
/// Achievement unlock and progress. Best-effort: failures are logged, never surfaced
/// as errors. Ported from <c>scripts/services/achievement_service.gd</c>.
/// </summary>
public interface IAchievementService
{
    /// <summary>Unlocks an achievement. Idempotent; safe to call when already unlocked.</summary>
    Task<PlatformResult> UnlockAsync(string achievementId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports progress toward a progressive achievement, 0 to 100. Providers that only
    /// support binary unlock should unlock at 100 and no-op otherwise.
    /// </summary>
    Task<PlatformResult> SetProgressAsync(
        string achievementId,
        int percentComplete,
        CancellationToken cancellationToken = default);
}
