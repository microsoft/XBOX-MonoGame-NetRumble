using GDK.Net;
using GDK.Net.Users;
using NetRumble.Platform.Diagnostics;

namespace NetRumble.Platform.GameCore.Services;

/// <summary>
/// GDK-backed privilege checks, gating multiplayer and communications.
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>scripts/services/privilege_service.gd</c>. Only two privileges are
/// ever checked by this title - <c>MULTIPLAYER</c> (254) and <c>COMMUNICATIONS</c> (252)
/// - and the numbers are load-bearing: GDK.Net's <see cref="UserPrivilege"/> enum carries
/// the same raw values the GDK headers publish, so the mapping is explicit but
/// value-preserving.
/// </para>
/// <para>
/// <b>There is no third privilege.</b> <c>scripts/services/privilege_service.gd</c>
/// only ever calls <c>check()</c>/<c>ensure()</c> with <c>MULTIPLAYER</c> or
/// <c>COMMUNICATIONS</c>, and <c>XUser.h</c> in GDK 260403 publishes no
/// <c>XUserPrivilege</c> enumerator for anything else this title would gate on. An
/// earlier <c>GamePrivilege.ViewProfiles</c> member was special-cased here to always
/// allow; it was removed in Phase 20 because it had no GDK counterpart, no caller and
/// no way to ever return a denial. See <see cref="GamePrivilege"/> for why it could not
/// be redirected to <c>XblPermission::ViewTargetProfile</c> via
/// <c>GameCorePrivacyService</c> instead.
/// </para>
/// <para>
/// <b>Fails closed (XR-045).</b> A privilege check that cannot be answered - no GDK, no
/// signed-in user, a service outage - returns a
/// <see cref="PrivilegeDenialReason.Unknown"/> denial, not
/// <see cref="PrivilegeVerdict.Allow"/>. The Godot source this was ported from failed
/// open, and so did this class until the pre-certification audit: XR-045 states that "if
/// a user does not have the privilege, the user must not be allowed to use the associated
/// activity", and a class that grants the privilege whenever it cannot tell does not
/// honour that. The requirement's guidance for an unreachable service points the same
/// way - "the title should block access to the requested action". The cost of the old
/// behaviour was that a child account whose query failed transiently was handed full
/// multiplayer and voice chat.
/// </para>
/// <para>
/// The connectivity evaluator is the place where failing open is still defensible, and it
/// is unchanged; this is not it.
/// </para>
/// <para>
/// <b>Cached verdicts expire.</b> XR-045 bounds privilege lifetime: "Privileges are
/// granted for the duration of the session/action or the time before the XBOX network
/// token is refreshed, whichever is shorter." The cache therefore carries
/// <see cref="CacheLifetime"/> per entry rather than living until an explicit
/// <see cref="ClearCache"/>, so a privilege granted once cannot outlive the window the
/// requirement allows. Denials are cached too, and expire on the same clock, so a
/// restriction lifted by a parent is picked up without a restart.
/// </para>
/// </remarks>
internal sealed class GameCorePrivilegeService : IPrivilegeService
{
    /// <summary>
    /// How long a verdict may be reused. See the class remarks: XR-045 caps this at the
    /// XBOX network token refresh interval, which the flat API does not expose, so this
    /// is a conservative fixed window well inside it.
    /// </summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    /// <summary>What each privilege lets the player do, for denial messages. Matches
    /// <c>_PRIVILEGE_VERBS</c> in the Godot source.</summary>
    private static readonly Dictionary<GamePrivilege, string> Verbs = new()
    {
        [GamePrivilege.Multiplayer] = "play online",
        [GamePrivilege.Communications] = "use voice and text chat",
    };

    private readonly GameCoreRuntime _runtime;
    private readonly GameCoreIdentityService _identity;
    private readonly Dictionary<GamePrivilege, CacheEntry> _cache = new();

    internal GameCorePrivilegeService(GameCoreRuntime runtime, GameCoreIdentityService identity)
    {
        _runtime = runtime;
        _identity = identity;
    }

    public Task<PrivilegeVerdict> CheckAsync(
        GamePrivilege privilege,
        bool useCache = true,
        CancellationToken cancellationToken = default)
    {
        if (useCache && TryGetFresh(privilege, out var cached))
        {
            return Task.FromResult(cached);
        }

        if (!_runtime.IsInitialized)
        {
            // Not cached: the runtime coming up later must be allowed to change the answer.
            return Task.FromResult(Indeterminate(
                privilege,
                "The Xbox services are not available yet."));
        }

        if (!_identity.TryGetUser(out var user))
        {
            return Task.FromResult(Indeterminate(
                privilege,
                "Sign in to Xbox to " + Verb(privilege) + "."));
        }

        var gdkPrivilege = ToUserPrivilege(privilege);

        bool hasPrivilege;
        UserPrivilegeDenyReason denyReason;
        try
        {
            hasPrivilege = user.CheckPrivilege(gdkPrivilege, out denyReason);
        }
        catch (GameRuntimeException ex)
        {
            // Fails closed. A query that threw has not established the privilege, and
            // XR-045 does not accept "we could not tell" as a grant. Not cached, so the
            // next attempt re-queries rather than inheriting the outage.
            CrashLog.MarkOnce(
                "privilege:" + privilege,
                $"privilege: {privilege} query failed, denying: {ex.Message}");

            return Task.FromResult(Indeterminate(
                privilege,
                "Xbox could not confirm this account's permissions right now."));
        }

        var verdict = hasPrivilege
            ? PrivilegeVerdict.Allow()
            : PrivilegeVerdict.Deny(ToDenialReason(denyReason), Describe(privilege, denyReason));

        _cache[privilege] = new CacheEntry(verdict, DateTime.UtcNow + CacheLifetime);
        return Task.FromResult(verdict);
    }

    public async Task<PrivilegeVerdict> EnsureAsync(
        GamePrivilege privilege,
        CancellationToken cancellationToken = default)
    {
        var verdict = await CheckAsync(privilege, useCache: true, cancellationToken).ConfigureAwait(false);
        if (verdict.Allowed || verdict.Reason == PrivilegeDenialReason.Restricted)
        {
            // Restricted (parental control / suspension) is not offered the resolution
            // flow: the Godot source only offers it when the raw deny reason is not
            // "banned", and this class folds "banned" into Restricted below - see
            // ToDenialReason. There is nothing the system UI can fix for either.
            return verdict;
        }

        if (!_runtime.IsInitialized || _runtime.Runtime is null || !_identity.TryGetUser(out var user))
        {
            return verdict;
        }

        var gdkPrivilege = ToUserPrivilege(privilege);

        try
        {
            await _runtime.Dispatcher
                .Marshal(user.ResolvePrivilegeWithUiAsync(gdkPrivilege, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
        catch (GameRuntimeException)
        {
            // Dismissing the UI, or the resolution failing, is a normal outcome here too
            // - the original denial stands and is returned below, unchanged.
            return verdict;
        }
        catch (OperationCanceledException)
        {
            return verdict;
        }

        return await CheckAsync(privilege, useCache: false, cancellationToken).ConfigureAwait(false);
    }

    public PrivilegeVerdict? GetCached(GamePrivilege privilege)
        => TryGetFresh(privilege, out var verdict) ? verdict : null;

    public void ClearCache() => _cache.Clear();

    /// <summary>
    /// A cached verdict, if one is present and still inside <see cref="CacheLifetime"/>.
    /// An expired entry is dropped rather than returned, so the caller re-queries.
    /// </summary>
    private bool TryGetFresh(GamePrivilege privilege, out PrivilegeVerdict verdict)
    {
        if (_cache.TryGetValue(privilege, out var entry))
        {
            if (entry.ExpiresUtc > DateTime.UtcNow)
            {
                verdict = entry.Verdict;
                return true;
            }

            _cache.Remove(privilege);
        }

        verdict = default;
        return false;
    }

    /// <summary>
    /// The verdict for "this could not be established". Denied, because XR-045 does not
    /// treat an unanswerable check as a grant, and reported as
    /// <see cref="PrivilegeDenialReason.Unknown"/> so callers can word it as a temporary
    /// problem rather than as a restriction on the account.
    /// </summary>
    private static PrivilegeVerdict Indeterminate(GamePrivilege privilege, string message)
        => PrivilegeVerdict.Deny(
            PrivilegeDenialReason.Unknown,
            $"{message} This account cannot {Verb(privilege)} until it can be confirmed.");

    private static string Verb(GamePrivilege privilege)
        => Verbs.GetValueOrDefault(privilege, "use this feature");

    private readonly record struct CacheEntry(PrivilegeVerdict Verdict, DateTime ExpiresUtc);

    /// <summary>Maps every <see cref="GamePrivilege"/> to its GDK.Net value. Both
    /// members are checkable, so the fallback arm is unreachable today and exists only
    /// to fail loudly if a member is added without a mapping.</summary>
    private static UserPrivilege ToUserPrivilege(GamePrivilege privilege) => privilege switch
    {
        GamePrivilege.Multiplayer => UserPrivilege.Multiplayer,
        GamePrivilege.Communications => UserPrivilege.Communications,
        _ => throw new ArgumentOutOfRangeException(nameof(privilege), privilege, "Not a GDK-checkable privilege."),
    };

    /// <summary>
    /// <c>Banned</c> folds into <see cref="PrivilegeDenialReason.Restricted"/>: neither is
    /// resolvable through the system UI, and <see cref="PrivilegeDenialReason"/> has no
    /// separate value for it. The player-facing text in <see cref="Describe"/> still
    /// distinguishes the two.
    /// </summary>
    private static PrivilegeDenialReason ToDenialReason(UserPrivilegeDenyReason reason) => reason switch
    {
        UserPrivilegeDenyReason.PurchaseRequired => PrivilegeDenialReason.PurchaseRequired,
        UserPrivilegeDenyReason.Restricted => PrivilegeDenialReason.Restricted,
        UserPrivilegeDenyReason.Banned => PrivilegeDenialReason.Restricted,
        _ => PrivilegeDenialReason.Unknown,
    };

    /// <summary>Player-facing denial text. Wording matches <c>describe()</c> in the
    /// Godot source exactly, including the family-group phrasing for restrictions.</summary>
    private static string Describe(GamePrivilege privilege, UserPrivilegeDenyReason reason)
    {
        var verb = Verb(privilege);

        return reason switch
        {
            UserPrivilegeDenyReason.PurchaseRequired =>
                $"This account needs an active Xbox subscription to {verb}.",
            UserPrivilegeDenyReason.Restricted =>
                $"This account is not allowed to {verb}. An adult on its family group can change that " +
                "in the Xbox privacy and online safety settings.",
            UserPrivilegeDenyReason.Banned =>
                $"This account is suspended from Xbox services and cannot {verb}.",
            _ => $"This account cannot {verb} right now.",
        };
    }
}
