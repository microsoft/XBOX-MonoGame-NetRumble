namespace NetRumble.Platform;

/// <summary>
/// Per-player communication privacy (XR-015): permissions plus the platform mute and
/// avoid lists. Ported from <c>scripts/services/privacy_service.gd</c>.
/// </summary>
/// <remarks>
/// The game turns these verdicts into Party mutes via
/// <see cref="IPartyService.SetPeerRestrictions"/>. Evaluation is asynchronous and
/// driven off roster changes, so providers must tolerate overlapping calls.
/// </remarks>
public interface IPrivacyService
{
    /// <summary>Refreshes the cached mute/avoid lists for the signed-in user.</summary>
    Task<PlatformResult> RefreshListsAsync(CancellationToken cancellationToken = default);

    /// <summary>Evaluates communication permissions for a set of players.</summary>
    Task<PlatformResult<IReadOnlyDictionary<string, PrivacyVerdict>>> EvaluateAsync(
        IReadOnlyList<string> xboxUserIds,
        CancellationToken cancellationToken = default);

    /// <summary>Last cached verdict for one player, without issuing a call.</summary>
    PrivacyVerdict? GetCached(string xboxUserId);

    void ClearCache();
}

/// <summary>What the local user is permitted to exchange with one other player.</summary>
public readonly record struct PrivacyVerdict(
    bool AllowVoice,
    bool AllowText,
    bool IsMuted,
    bool IsAvoided)
{
    /// <summary>Permissive default used when privacy cannot be evaluated offline.</summary>
    public static PrivacyVerdict AllowAll => new(true, true, false, false);

    /// <summary>Restrictive default used when a check fails on a live session.</summary>
    public static PrivacyVerdict DenyAll => new(false, false, true, false);
}
