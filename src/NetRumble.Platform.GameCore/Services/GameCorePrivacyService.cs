using GDK.Net;
using GDK.Net.XboxLive;
using NetRumble.Platform.Diagnostics;
using NetRumble.Platform.GameCore.Interop;

namespace NetRumble.Platform.GameCore.Services;

/// <summary>
/// GDK communication privacy: platform mute/avoid lists plus per-player permission
/// checks, over GDK.Net's <see cref="PrivacyService"/>.
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>scripts/services/privacy_service.gd</c>. Three sources feed a
/// verdict, and the list overlay rule is preserved exactly: the mute and avoid lists
/// win over whatever the permission check says, because a player who muted or avoided
/// someone asked for that directly and it is not something the title may soften. Avoid
/// blocks both voice and text; mute blocks only voice, because "a platform mute is
/// about audio" (the Godot source's own words).
/// </para>
/// <para>
/// <b>Deliberate simplification versus the Godot source: no batch permission call.</b>
/// GDK.Net exposes <see cref="PrivacyService.BatchCheckPermissionAsync"/> which handles
/// the complex variable-length marshalling internally, but this class issues one
/// <see cref="PrivacyService.CheckPermissionAsync"/> per (xuid, permission) pair
/// instead, matching the previous approach. The per-player call count is small enough
/// for a lobby roster, and it is safer to preserve the existing flow than to rearchitect
/// the evaluation loop mid-migration.
/// </para>
/// <para>
/// <b>Fails closed (XR-015).</b> Every permission check that cannot reach the service
/// resolves to denied, and so does an unevaluated or unparseable xuid. The Godot source
/// this was ported from failed open on the reasoning that the communications privilege
/// had already gated whether chat happens at all; the pre-certification audit rejected
/// that, because XR-015's carve-out for skipping per-peer checks applies only to
/// "scenarios that involve significant player counts", which a four-player session is
/// not. Offline and LAN sessions reach their own providers and are unaffected.
/// </para>
/// <para>
/// <b>Unverifiable in this environment.</b> Needs a live Xbox Services connection and,
/// to see the list-overlay behaviour, an account with something on its own mute or
/// avoid list. See <c>docs/design-notes.md</c>.
/// </para>
/// </remarks>
internal sealed class GameCorePrivacyService : IPrivacyService
{
    private const string ListRestrictionMessage = "Xbox privacy settings limit chat with this player.";

    private readonly GameCoreRuntime _runtime;
    private readonly GameCoreIdentityService _identity;
    private readonly GameCoreXblContext _xbl;

    private readonly Dictionary<string, PrivacyVerdict> _verdicts = new();
    private HashSet<string> _muted = [];
    private HashSet<string> _avoided = [];
    private bool _listsLoaded;

    internal GameCorePrivacyService(GameCoreRuntime runtime, GameCoreIdentityService identity, GameCoreXblContext xbl)
    {
        _runtime = runtime;
        _identity = identity;
        _xbl = xbl;
    }

    public async Task<PlatformResult> RefreshListsAsync(CancellationToken cancellationToken = default)
    {
        _verdicts.Clear();
        _muted = [];
        _avoided = [];
        _listsLoaded = false;

        if (!_runtime.IsInitialized || !_identity.TryGetUser(out _)
            || !_xbl.TryGetContext(out var context))
        {
            return PlatformResult.Unavailable("Xbox privacy lists are not available in this build.");
        }

        try
        {
            var muteTask = _runtime.Dispatcher
                .Marshal(context.Privacy.GetMuteListAsync(cancellationToken));
            var avoidTask = _runtime.Dispatcher
                .Marshal(context.Privacy.GetAvoidListAsync(cancellationToken));

            var muteList = await muteTask.ConfigureAwait(false);
            var avoidList = await avoidTask.ConfigureAwait(false);

            _muted = new HashSet<string>(muteList.Select(x => x.ToString()));
            _avoided = new HashSet<string>(avoidList.Select(x => x.ToString()));

            _listsLoaded = true;
            return PlatformResult.Ok();
        }
        catch (GameRuntimeException ex)
        {
            // A failed refresh still leaves _listsLoaded false, so EvaluateAsync will
            // retry it - matching the Godot source's own "cheap to call again" note.
            return PlatformResult.Fail(PlatformStatus.Failed, "The privacy lists could not be read.", ex.Message);
        }
    }

    public async Task<PlatformResult<IReadOnlyDictionary<string, PrivacyVerdict>>> EvaluateAsync(
        IReadOnlyList<string> xboxUserIds,
        CancellationToken cancellationToken = default)
    {
        var verdicts = new Dictionary<string, PrivacyVerdict>();

        if (!_runtime.IsInitialized || !_identity.TryGetUser(out _)
            || !_xbl.TryGetContext(out var context))
        {
            // Fails closed (XR-015). This is the GDK provider: if it is live at all, the
            // title believes it is on the Xbox network, and a roster it cannot evaluate is
            // a roster whose communication settings it does not know. Offline and LAN play
            // reach their own providers, which allow all, so nothing that legitimately has
            // no privacy service is muted by this.
            foreach (var xuidString in xboxUserIds)
            {
                verdicts[xuidString] = PrivacyVerdict.DenyAll;
            }

            return PlatformResult<IReadOnlyDictionary<string, PrivacyVerdict>>.Ok(verdicts);
        }

        if (!_listsLoaded)
        {
            await RefreshListsAsync(cancellationToken).ConfigureAwait(false);
        }

        var pending = new List<string>();
        foreach (var xuidString in xboxUserIds)
        {
            var trimmed = xuidString.Trim();
            if (trimmed.Length == 0 || verdicts.ContainsKey(trimmed))
            {
                continue;
            }

            if (_verdicts.TryGetValue(trimmed, out var cached))
            {
                verdicts[trimmed] = cached;
            }
            else if (!pending.Contains(trimmed))
            {
                pending.Add(trimmed);
            }
        }

        foreach (var trimmed in pending)
        {
            if (!ulong.TryParse(trimmed, out var targetXuid))
            {
                // An id that is not an xuid cannot be checked against the Xbox network, so
                // it cannot be cleared for communication either.
                verdicts[trimmed] = PrivacyVerdict.DenyAll;
                continue;
            }

            var allowVoice = await CheckPermissionAsync(
                context, targetXuid, Permission.CommunicateUsingVoice, cancellationToken)
                .ConfigureAwait(false);
            var allowText = await CheckPermissionAsync(
                context, targetXuid, Permission.CommunicateUsingText, cancellationToken)
                .ConfigureAwait(false);

            var verdict = new PrivacyVerdict(
                AllowVoice: allowVoice,
                AllowText: allowText,
                IsMuted: !allowVoice || !allowText,
                IsAvoided: false);

            _verdicts[trimmed] = verdict;
            verdicts[trimmed] = verdict;
        }

        // The lists win over the permission answer for every xuid, cached or fresh,
        // exactly as in the Godot source's own final pass.
        foreach (var xuidString in verdicts.Keys.ToList())
        {
            verdicts[xuidString] = ApplyLists(xuidString, verdicts[xuidString]);
        }

        return PlatformResult<IReadOnlyDictionary<string, PrivacyVerdict>>.Ok(verdicts);
    }

    public PrivacyVerdict? GetCached(string xboxUserId)
    {
        var trimmed = xboxUserId.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        // An xuid with no stored verdict has not been evaluated, so it is denied rather
        // than assumed clear - the same rule EvaluateAsync applies.
        return ApplyLists(trimmed, _verdicts.GetValueOrDefault(trimmed, PrivacyVerdict.DenyAll));
    }

    public void ClearCache()
    {
        _verdicts.Clear();
        _muted = [];
        _avoided = [];
        _listsLoaded = false;
    }

    /// <summary>
    /// Overlays the platform lists on a permission verdict. Kept separate from the
    /// permission call itself, matching the Godot source's <c>_apply_lists</c>, so a
    /// cached verdict picks up a list refreshed after it was stored.
    /// </summary>
    private PrivacyVerdict ApplyLists(string xuid, PrivacyVerdict verdict)
    {
        if (_avoided.Contains(xuid))
        {
            return verdict with { AllowVoice = false, AllowText = false, IsAvoided = true };
        }

        if (_muted.Contains(xuid))
        {
            // Text is left alone - a platform mute is about audio, per the Godot source.
            return verdict with { AllowVoice = false, IsMuted = true };
        }

        return verdict;
    }

    /// <summary>
    /// Checks one permission against one target. Fails closed (XR-015, XR-045).
    /// </summary>
    /// <remarks>
    /// <para>
    /// GDK.Net's <see cref="PrivacyService.CheckPermissionAsync"/> is itself fail-closed:
    /// a check that could not complete returns
    /// <see cref="PrivacyPermissionCheckResult.WasChecked"/> <see langword="false"/> and
    /// <see cref="PrivacyPermissionCheckResult.IsAllowed"/> <see langword="false"/>. This
    /// class used to invert that, treating an uncompleted check as permission granted,
    /// which is what the Godot source did.
    /// </para>
    /// <para>
    /// The pre-certification audit ruled that out. XR-015's large-scale carve-out - where
    /// per-peer permission checks may be skipped in favour of a periodic privilege and
    /// block-list sweep - is scoped to "scenarios that involve significant player counts
    /// (such as global chat and very large-scale sessions/events)". A four-player session
    /// is not one, so the ordinary per-peer rule governs, and under it an unanswered check
    /// is not consent. Returning <see langword="false"/> costs an unnecessary mute in an
    /// outage; returning <see langword="true"/> costs communication the platform may have
    /// been trying to forbid.
    /// </para>
    /// </remarks>
    private async Task<bool> CheckPermissionAsync(
        XboxLiveContext context,
        ulong targetXuid,
        Permission permission,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _runtime.Dispatcher
                .Marshal(context.Privacy.CheckPermissionAsync(permission, targetXuid, cancellationToken))
                .ConfigureAwait(false);

            // Only a check that actually completed can grant anything.
            return result.WasChecked && result.IsAllowed;
        }
        catch (GameRuntimeException ex)
        {
            CrashLog.MarkOnce(
                "privacy:" + permission,
                $"privacy: {permission} check threw, denying: {ex.Message}");

            return false;
        }
    }
}
