namespace NetRumble.Platform;

/// <summary>
/// Account privilege checks (XR-045). Ported from
/// <c>scripts/services/privilege_service.gd</c>.
/// </summary>
public interface IPrivilegeService
{
    /// <summary>Checks a privilege, optionally from cache.</summary>
    Task<PrivilegeVerdict> CheckAsync(
        GamePrivilege privilege,
        bool useCache = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks a privilege and, when it is denied, shows the platform's upsell UI.
    /// </summary>
    Task<PrivilegeVerdict> EnsureAsync(
        GamePrivilege privilege,
        CancellationToken cancellationToken = default);

    /// <summary>Last cached verdict without issuing a call.</summary>
    PrivilegeVerdict? GetCached(GamePrivilege privilege);

    /// <summary>Drops the cache. Called when the active user changes.</summary>
    void ClearCache();
}

/// <summary>Privileges this game checks.</summary>
/// <remarks>
/// <para>
/// These are exactly the two privileges the GDK exposes as account-level checks that
/// this game gates on, and they map one-to-one onto <c>XUserPrivilege</c> values. A
/// third member, <c>ViewProfiles</c>, used to sit here and was removed in Phase 20: no
/// <c>XUserPrivilege</c> corresponds to it, so every implementation hard-coded an
/// "allowed" answer, and no caller ever asked for it.
/// </para>
/// <para>
/// It could not be redirected to <see cref="IPrivacyService"/> either, which is the
/// obvious-looking alternative. The two APIs answer different questions:
/// <see cref="IPrivilegeService.CheckAsync"/> is account-level and takes no target,
/// while <see cref="IPrivacyService.EvaluateAsync"/> is per-target and returns a
/// <see cref="PrivacyVerdict"/> that carries voice, text, mute and avoid flags but
/// nothing about profile viewing. An account-level question cannot be answered by a
/// per-target API. If per-target profile visibility is ever needed, it belongs as a new
/// field on <see cref="PrivacyVerdict"/>, not as a member here.
/// </para>
/// </remarks>
public enum GamePrivilege
{
    /// <summary>Playing an online multiplayer session.</summary>
    Multiplayer,

    /// <summary>Voice and text communication.</summary>
    Communications,
}

/// <summary>Result of a privilege check, with a player-facing reason when denied.</summary>
public readonly record struct PrivilegeVerdict(
    bool Allowed,
    PrivilegeDenialReason Reason,
    string Message)
{
    public static PrivilegeVerdict Allow()
        => new(true, PrivilegeDenialReason.None, string.Empty);

    public static PrivilegeVerdict Deny(PrivilegeDenialReason reason, string message)
        => new(false, reason, message);
}

public enum PrivilegeDenialReason
{
    None = 0,
    /// <summary>Denied and the platform offers a way to fix it (a subscription upsell).</summary>
    PurchaseRequired,
    /// <summary>Denied by parental controls or account restriction; not resolvable in-game.</summary>
    Restricted,
    /// <summary>Could not be determined; treat as denied but say so differently.</summary>
    Unknown,
}
