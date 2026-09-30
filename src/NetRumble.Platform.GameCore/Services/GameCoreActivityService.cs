using System.Collections.Concurrent;
using System.Linq;
using GDK.Net;
using GDK.Net.Activation;
using GDK.Net.XboxLive;
using NetRumble.Platform.Diagnostics;

namespace NetRumble.Platform.GameCore.Services;

/// <summary>
/// Protocol activation, Xbox invite acceptance, and the platform invite composer.
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>scripts/services/activity_service.gd</c>'s activation half (XR-064 /
/// XR-124) and <c>scripts/autoload/invite_router.gd</c>'s trigger. The GDScript split
/// those two concerns across two files because the decision of what an accepted invite
/// <i>means</i> needs <c>ScreenManager</c> and <c>NetManager</c>, neither of which a
/// platform service should know about - this class keeps exactly the same split: it
/// only ever raises <see cref="InviteAccepted"/> with a bare connection string, and
/// leaves what happens next to <c>NetRumble.Game.InviteRouter</c>.
/// </para>
/// <para>
/// <b>The activation callback binds straight to the thunks DLL.</b> GDK.Net used to bind
/// <c>XGameActivationRegisterForEvent</c> to its own <c>xgameruntime.extras.dll</c> shim,
/// because the GDK's redistributable thunks DLL did not export it. GDK edition 260404
/// exports it directly, the shim is retired, and subscribing no longer throws
/// <see cref="PlatformNotSupportedException"/> for a missing shim. Registration is still
/// guarded below, because a title with no <c>&lt;MultiplayerProtocol&gt;</c> registration
/// or a machine with no Gaming Runtime at all still fails here: not fatal, the game runs,
/// it just cannot be launched by invite. See <c>docs/xbox-console-build.md</c>.
/// </para>
/// <para>
/// <b>Multiplayer Activity is the whole of the online half.</b>
/// <see cref="SetActivityAsync"/>, <see cref="GetJoinableActivitiesAsync"/>,
/// <see cref="ReportRecentPlayersAsync"/> and <see cref="FlushRecentPlayersAsync"/> go
/// through <c>XboxLiveContext.MultiplayerActivity</c>, and
/// <see cref="SetPresenceAsync"/> through <c>.Presence</c>. All of them need a signed-in
/// user and a live Xbox Services context, so all of them degrade to
/// <see cref="PlatformResult.Unavailable(string)"/> rather than throwing when there is
/// none - exactly like <c>NetRumble.Platform.Offline</c>'s stub, so the game plays
/// offline on a machine with no GDK.
/// </para>
/// <para>
/// <b>None of it has been exercised against a live service.</b> This checkout has a
/// working GDK but no devkit, no signed-in account and no real title configuration, so
/// every call here compiles and is shaped correctly but has only ever returned
/// <c>Unavailable</c> in practice. The SCID that <see cref="SetPresenceAsync"/> depends
/// on is derived, not read from configuration - see <c>docs/design-notes.md</c>.
/// </para>
/// <para>
/// <b><see cref="ShowInviteUiAsync"/> is real</b> - it is the one member of this
/// interface that needs nothing beyond what is already wired: the GDK's
/// <c>XGameUiShowMultiplayerActivityGameInviteAsync</c> composer only takes the
/// requesting user, matching activity_service.gd's own choice of the Multiplayer
/// Activity invite route over the session-based <c>XGameUiShowSendGameInviteAsync</c>
/// (see its remarks on why MPSD does not apply to this title). The invite it sends
/// carries whatever connection string <see cref="SetActivityAsync"/> last published, so
/// the two are only useful together.
/// </para>
/// </remarks>
internal sealed class GameCoreActivityService : IActivityService
{
    /// <summary>
    /// XSAPI's per-call ceiling for <c>XblMultiplayerActivityGetActivityAsync</c>.
    /// </summary>
    private const int MaxActivityQueryBatch = 30;

    private readonly GameCoreRuntime _runtime;
    private readonly GameCoreIdentityService _identity;
    private readonly GameCoreXblContext _xbl;

    private readonly ConcurrentQueue<string> _pendingInvites = new();
    private readonly HashSet<ulong> _reportedPlayers = new();
    private GameActivationManager? _activation;
    private bool _registered;
    private bool _activityPublished;
    private string? _groupId;

    internal GameCoreActivityService(
        GameCoreRuntime runtime,
        GameCoreIdentityService identity,
        GameCoreXblContext xbl)
    {
        _runtime = runtime;
        _identity = identity;
        _xbl = xbl;
    }

    public event Action<string>? InviteAccepted;

    /// <summary>
    /// Drains activation callbacks queued since the last call. Must run from
    /// <see cref="GameCoreRuntime.Pump"/>: GDK.Net raises
    /// <see cref="GameActivationManager.Activated"/> on the Gaming Runtime's own thread,
    /// so the queue below is what keeps this provider's "every event surfaces on the
    /// pump thread" contract for activations - the same job
    /// <see cref="PumpDispatcher"/> does for completions.
    /// </summary>
    internal void Pump()
    {
        if (!_registered)
        {
            TryRegister();
        }

        while (_pendingInvites.TryDequeue(out var connectionString))
        {
            InviteAccepted?.Invoke(connectionString);
        }
    }

    /// <summary>
    /// Subscribes to activation events once the runtime exists. Retried every
    /// <see cref="Pump"/> until the subscription actually succeeds - a cold-launch
    /// protocol activation can arrive before <c>IPlatformRuntime.InitializeAsync</c> has
    /// completed, so this cannot simply run once from a constructor.
    /// </summary>
    /// <remarks>
    /// The success flag is set after the subscription, not before it (#22). Setting it
    /// first meant that one early failure - a runtime that existed but was not yet ready
    /// to register, most often - permanently disabled invite delivery for the rest of
    /// the process, with no symptom beyond invites that never arrived. Retrying costs a
    /// field read per frame while the registration is outstanding and nothing at all
    /// once it lands.
    /// </remarks>
    private void TryRegister()
    {
        if (_runtime.Runtime is not { } runtime)
        {
            return;
        }

        try
        {
            runtime.Activation.Activated += OnActivated;
            _activation = runtime.Activation;
            _registered = true;
            CrashLog.Mark("activity: registered for activation events");
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException
                                      or EntryPointNotFoundException
                                      or DllNotFoundException
                                      or GameRuntimeException
                                      or ObjectDisposedException)
        {
            // Not fatal: a title with no MicrosoftGame.config <MultiplayerProtocol>
            // registration, or a build with no Gaming Runtime at all, simply never sees
            // an activation. Degrading here means the game still runs; it just cannot be
            // launched by invite. Logged once per distinct failure so an invite that
            // never arrives has something to explain it.
            CrashLog.MarkOnce(
                "activity-register:" + ex.GetType().Name,
                $"activity: activation registration failed ({ex.GetType().Name}): {ex.Message}");
        }
    }

    /// <summary>
    /// Handles one activation. Runs on the Gaming Runtime's thread, so it does no more
    /// than decode the URI and enqueue or hand back - everything the game reacts to
    /// happens on the pump, in <see cref="Pump"/>.
    /// </summary>
    private void OnActivated(object? sender, GameActivationEventArgs e)
    {
        var uri = e.Uri ?? string.Empty;

        switch (e.Kind)
        {
            case GameActivationType.Protocol:
            case GameActivationType.AcceptedGameInvite:
                // Both carry the same query-string shape - protocol_activated and
                // invite_accepted route through one parser in the GDScript for the same
                // reason (see _connection_string_from_uri's own remarks).
                var connectionString = ConnectionStringFromUri(uri);
                if (connectionString.Length > 0)
                {
                    CrashLog.Mark($"activity: activation {e.Kind} accepted");
                    _pendingInvites.Enqueue(connectionString);
                }
                else
                {
                    // #22: an activation that carries nothing this parser recognises used
                    // to vanish without trace, which is precisely the "accepting the
                    // invite does nothing" symptom. Record the shape (never the payload)
                    // so the next unrecognised URI form is diagnosable from a log.
                    CrashLog.MarkOnce(
                        "activity-unparsed",
                        $"activity: activation {e.Kind} carried no recognisable connection string (length {uri.Length})");
                }

                break;

            case GameActivationType.PendingGameInvite:
                // Mirrors _on_pending_invite_received: accepting re-raises this same
                // event as AcceptedGameInvite, so this hands off rather than parsing the
                // pending URI directly. Fire-and-forget: a failure here just means the
                // invite sits unaccepted, which is what happens if the player ignores an
                // Xbox shell invite anyway - and throwing back into GDK.Net's dispatch
                // would take the process down.
                if (uri.Length > 0)
                {
                    try
                    {
                        (sender as GameActivationManager ?? _activation)?.AcceptPendingInvite(uri);
                    }
                    catch (Exception ex) when (ex is GameRuntimeException
                                                  or PlatformNotSupportedException
                                                  or ObjectDisposedException
                                                  or ArgumentException)
                    {
                        CrashLog.MarkOnce(
                            "activity-accept-pending",
                            $"activity: accepting pending invite failed ({ex.GetType().Name}): {ex.Message}");

                        // #22: if the shell will not re-raise the invite for us, try the
                        // URI we were handed directly rather than losing the invite.
                        var pending = ConnectionStringFromUri(uri);
                        if (pending.Length > 0)
                        {
                            _pendingInvites.Enqueue(pending);
                        }
                    }
                }

                break;

            case GameActivationType.File:
            default:
                // This title has no file association; nothing to route.
                break;
        }
    }

    /// <summary>
    /// Keys the connection string might be carried under in the query string. Mirrors
    /// <c>activity_service.gd</c>'s <c>_CONNECTION_STRING_KEYS</c> exactly, including the
    /// case-insensitive match - the addon's own documentation there does not commit to
    /// one spelling.
    /// </summary>
    private static readonly string[] ConnectionStringKeys =
    [
        "connectionString",
        "connection_string",
        "connectionstring",
        "connection",
        "cs",
    ];

    /// <summary>
    /// Extracts a connection string from a protocol/invite URI. The GDK's activation API
    /// hands over the raw URI only - unlike the Godot addon, which additionally offers a
    /// pre-parsed invite dictionary - so every activation here goes through this one
    /// parser rather than <c>_connection_string_from_invite</c>'s dictionary-then-fallback
    /// path. Internal (not private) so PlatformSpike can exercise it directly without
    /// needing a live activation callback to drive it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately permissive (#22). The original parser accepted exactly one shape -
    /// a <c>?</c> query carrying one of three key spellings - and silently returned
    /// empty for everything else, which is indistinguishable from "no invite arrived"
    /// and was the reason accepted invites appeared to do nothing. What the shell
    /// actually hands back depends on how the activity's connection string was
    /// round-tripped, so this now also reads a <c>#</c> fragment, accepts a payload
    /// carried as the URI's path, and treats a bare string that is not a URI at all as
    /// the connection string itself.
    /// </para>
    /// <para>
    /// Being permissive is safe here: a value that is not a real connection string fails
    /// at <c>JoinByConnectionStringAsync</c> with a visible error, which is strictly
    /// more useful than dropping it without trace.
    /// </para>
    /// </remarks>
    internal static string ConnectionStringFromUri(string uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return string.Empty;
        }

        uri = uri.Trim();

        // Query and fragment are parsed with the same rules; some shells round-trip the
        // payload through the fragment to keep it out of logs.
        var separator = uri.IndexOfAny(['?', '#']);
        if (separator >= 0)
        {
            var payload = uri[(separator + 1)..].Replace('#', '&');
            foreach (var pair in payload.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var split = pair.Split('=', 2);
                if (split.Length != 2)
                {
                    continue;
                }

                var key = split[0].Trim();
                if (ConnectionStringKeys.Any(candidate => string.Equals(key, candidate, StringComparison.OrdinalIgnoreCase)))
                {
                    return Unescape(split[1].Trim());
                }
            }
        }

        // No recognised key. Fall back to a payload carried as the path -
        // "netrumble://join/<connection string>" and friends.
        var schemeEnd = uri.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            var rest = separator >= 0 ? uri[(schemeEnd + 3)..separator] : uri[(schemeEnd + 3)..];
            var segments = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);

            // The first segment is the authority, which is a host name rather than a
            // payload; anything after it is ours.
            return segments.Length > 1 ? Unescape(segments[^1]) : string.Empty;
        }

        // Not a URI at all: some paths hand the connection string straight through.
        return separator < 0 && !uri.Contains(':') ? Unescape(uri) : string.Empty;

        static string Unescape(string value)
        {
            try
            {
                return Uri.UnescapeDataString(value);
            }
            catch (FormatException)
            {
                // A malformed percent-escape must not crash the activation path;
                // the raw (still usable, if unusual) value is better than nothing.
                return value;
            }
        }
    }

    public async Task<PlatformResult> SetActivityAsync(
        string connectionString,
        int maxPlayers,
        int currentPlayers,
        string groupId = "",
        CancellationToken cancellationToken = default)
    {
        // XSAPI rejects an empty connection string, and an activity without one advertises
        // a join that cannot happen - the exact certification failure DeleteActivityAsync
        // exists to prevent. Refuse at the door rather than publish a broken one.
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return PlatformResult.Fail(
                PlatformStatus.Failed,
                "A joinable activity needs a connection string.",
                "SetActivityAsync was called with an empty connection string.");
        }

        if (!_xbl.TryGetContext(out var context))
        {
            return PlatformResult.Unavailable(
                "The Xbox network is not available, or no user is signed in.");
        }

        // GroupId must not be empty, and it has to stay the same for as long as one
        // session is advertised - it is what lets the shell treat several players'
        // activities as the same game. The caller rarely has anything meaningful to put
        // here, so one is minted per published activity and held until the activity is
        // deleted.
        _groupId = string.IsNullOrWhiteSpace(groupId)
            ? _groupId ?? Guid.NewGuid().ToString("N")
            : groupId;

        try
        {
            var info = new MultiplayerActivityInfo(
                context.XboxUserId,
                connectionString,
                MultiplayerActivityJoinRestriction.Public,
                ToPlayerCount(maxPlayers),
                ToPlayerCount(currentPlayers),
                _groupId,
                MultiplayerActivityPlatform.Unknown);

            await _runtime.Dispatcher
                .Marshal(context.MultiplayerActivity.SetActivityAsync(
                    info,
                    // #23: the title ships on Xbox and on PC GDK against one shared
                    // PlayFab lobby backend, so a session really is joinable across
                    // platforms. Left false, the shell suppresses the Join affordance
                    // for a friend on the other platform and offers only Play, which is
                    // exactly the reported symptom.
                    allowCrossPlatformJoin: true,
                    cancellationToken))
                .ConfigureAwait(false);

            _activityPublished = true;
            CrashLog.Mark($"activity: published ({currentPlayers}/{maxPlayers})");
            return PlatformResult.Ok();
        }
        catch (GameRuntimeException ex)
        {
            CrashLog.MarkOnce("activity-publish", $"activity: publish failed: {ex.Message}");
            return PlatformResult.Fail(
                PlatformStatus.Failed,
                "The joinable activity could not be published.",
                ex.Message);
        }
        catch (OperationCanceledException)
        {
            return PlatformResult.Canceled("Publishing the joinable activity was cancelled.");
        }
    }

    public async Task<PlatformResult> DeleteActivityAsync(CancellationToken cancellationToken = default)
    {
        _groupId = null;

        // Nothing was ever published, so there is nothing to clear and no failure for the
        // caller to react to. This also keeps the offline-equivalent behaviour on a
        // machine with no GDK, where the session simply never advertised itself.
        if (!_activityPublished || !_xbl.TryGetContext(out var context))
        {
            _activityPublished = false;
            return PlatformResult.Ok();
        }

        try
        {
            await _runtime.Dispatcher
                .Marshal(context.MultiplayerActivity.DeleteActivityAsync(cancellationToken))
                .ConfigureAwait(false);

            _activityPublished = false;
            return PlatformResult.Ok();
        }
        catch (GameRuntimeException ex)
        {
            // Deliberately left marked as published. A delete that failed means the
            // service may still be advertising a session that has ended, and the next
            // attempt should try again rather than assume it is already gone.
            return PlatformResult.Fail(
                PlatformStatus.Failed,
                "The joinable activity could not be cleared.",
                ex.Message);
        }
        catch (OperationCanceledException)
        {
            return PlatformResult.Canceled("Clearing the joinable activity was cancelled.");
        }
    }

    public async Task<PlatformResult> ShowInviteUiAsync(CancellationToken cancellationToken = default)
    {
        if (!_runtime.IsInitialized || _runtime.Runtime is null || !_identity.TryGetUser(out var user))
        {
            return PlatformResult.Unavailable("The Microsoft GDK is not available or no user is signed in.");
        }

        // #22: the invite the shell sends carries the *published activity's* connection
        // string - there is no payload argument here to carry one instead. With no
        // activity published, the shell still shows its composer and still claims to
        // send, but what arrives on the other console has nothing to join, so accepting
        // it does nothing at all. Refusing here turns an invite that silently goes
        // nowhere into a visible message on the sender's own screen.
        if (!_activityPublished)
        {
            CrashLog.Mark("activity: invite UI refused, no activity published");
            return PlatformResult.Fail(
                PlatformStatus.Failed,
                "This match is not joinable yet, so it cannot be invited to.",
                "ShowInviteUiAsync was called with no published multiplayer activity.");
        }

        try
        {
            await _runtime.Dispatcher
                .Marshal(_runtime.Runtime.GameUi.ShowMultiplayerActivityGameInviteAsync(user, cancellationToken))
                .ConfigureAwait(false);

            return PlatformResult.Ok();
        }
        catch (GameRuntimeException ex)
        {
            return PlatformResult.Fail(PlatformStatus.Failed, "Could not show the invite screen.", ex.Message);
        }
        catch (OperationCanceledException)
        {
            return PlatformResult.Canceled("The invite screen was cancelled.");
        }
    }

    public async Task<PlatformResult<IReadOnlyDictionary<string, string>>> GetJoinableActivitiesAsync(
        IReadOnlyList<string> xboxUserIds,
        CancellationToken cancellationToken = default)
    {
        var found = new Dictionary<string, string>(xboxUserIds.Count);

        if (xboxUserIds.Count == 0)
        {
            return PlatformResult<IReadOnlyDictionary<string, string>>.Ok(found);
        }

        if (!_xbl.TryGetContext(out var context))
        {
            return PlatformResult<IReadOnlyDictionary<string, string>>.Unavailable(
                "The Xbox network is not available, or no user is signed in.");
        }

        // The interface speaks in strings because most of the port does; XSAPI wants
        // 64-bit ids. An id that does not parse is skipped rather than failing the whole
        // query - one malformed entry in a friends list should not blank the join
        // prompts for everyone else in it.
        var requested = new List<(string Text, ulong Id)>(xboxUserIds.Count);
        foreach (var text in xboxUserIds)
        {
            if (ulong.TryParse(text, out var id))
            {
                requested.Add((text, id));
            }
        }

        if (requested.Count == 0)
        {
            return PlatformResult<IReadOnlyDictionary<string, string>>.Ok(found);
        }

        try
        {
            // XblMultiplayerActivityGetActivityAsync takes at most 30 users per call, so
            // a larger roster is queried in batches rather than silently truncated.
            for (var offset = 0; offset < requested.Count; offset += MaxActivityQueryBatch)
            {
                var batch = requested.GetRange(
                    offset,
                    Math.Min(MaxActivityQueryBatch, requested.Count - offset));

                var activities = await _runtime.Dispatcher
                    .Marshal(context.MultiplayerActivity.GetActivitiesAsync(
                        batch.Select(entry => entry.Id),
                        cancellationToken))
                    .ConfigureAwait(false);

                foreach (var activity in activities)
                {
                    // A null connection string is XSAPI reporting that privacy or the
                    // join restriction hides the join data - the player is in a session
                    // but this user may not join it, which is not the same as having no
                    // activity, and must not be offered as joinable.
                    if (string.IsNullOrEmpty(activity.ConnectionString))
                    {
                        continue;
                    }

                    var text = batch.FirstOrDefault(entry => entry.Id == activity.XboxUserId).Text;
                    if (!string.IsNullOrEmpty(text))
                    {
                        found[text] = activity.ConnectionString;
                    }
                }
            }

            return PlatformResult<IReadOnlyDictionary<string, string>>.Ok(found);
        }
        catch (GameRuntimeException ex)
        {
            return PlatformResult<IReadOnlyDictionary<string, string>>.Fail(
                PlatformStatus.Failed,
                "Joinable activities could not be read.",
                ex.Message);
        }
        catch (OperationCanceledException)
        {
            return PlatformResult<IReadOnlyDictionary<string, string>>.Canceled(
                "Reading joinable activities was cancelled.");
        }
    }

    public Task<PlatformResult> ReportRecentPlayersAsync(
        IReadOnlyList<string> xboxUserIds,
        CancellationToken cancellationToken = default)
    {
        if (xboxUserIds.Count == 0)
        {
            return Task.FromResult(PlatformResult.Ok());
        }

        if (!_xbl.TryGetContext(out var context))
        {
            return Task.FromResult(PlatformResult.Unavailable(
                "The Xbox network is not available, or no user is signed in."));
        }

        // The interface promises the provider de-duplicates, because the game calls this
        // every time the roster changes and the same opponents are in most of those
        // calls. Recent players is privacy-sensitive data; reporting the same encounter
        // repeatedly is noise XSAPI should not be asked to carry.
        var updates = new List<MultiplayerActivityRecentPlayerUpdate>(xboxUserIds.Count);
        foreach (var text in xboxUserIds)
        {
            if (ulong.TryParse(text, out var id)
                && id != context.XboxUserId
                && _reportedPlayers.Add(id))
            {
                // Opponent rather than Teammate: this is a free-for-all deathmatch, and
                // the guidance is to report the least specific type that fits.
                updates.Add(new MultiplayerActivityRecentPlayerUpdate(
                    id,
                    MultiplayerActivityEncounterType.Opponent));
            }
        }

        if (updates.Count == 0)
        {
            return Task.FromResult(PlatformResult.Ok());
        }

        try
        {
            // Synchronous by design: this only appends to XSAPI's local batch. The upload
            // happens on XSAPI's own schedule, or when FlushRecentPlayersAsync is called.
            context.MultiplayerActivity.UpdateRecentPlayers(updates);
            return Task.FromResult(PlatformResult.Ok());
        }
        catch (GameRuntimeException ex)
        {
            // Nothing was queued, so the de-duplication set would otherwise suppress a
            // later retry of the same players.
            foreach (var update in updates)
            {
                _reportedPlayers.Remove(update.XboxUserId);
            }

            return Task.FromResult(PlatformResult.Fail(
                PlatformStatus.Failed,
                "Recent players could not be reported.",
                ex.Message));
        }
    }

    public async Task<PlatformResult> FlushRecentPlayersAsync(CancellationToken cancellationToken = default)
    {
        if (!_xbl.TryGetContext(out var context))
        {
            // Nothing was ever queued without a context, so there is nothing to flush.
            return PlatformResult.Ok();
        }

        try
        {
            await _runtime.Dispatcher
                .Marshal(context.MultiplayerActivity.FlushRecentPlayersAsync(cancellationToken))
                .ConfigureAwait(false);

            return PlatformResult.Ok();
        }
        catch (GameRuntimeException ex)
        {
            return PlatformResult.Fail(
                PlatformStatus.Failed,
                "Recent players could not be uploaded.",
                ex.Message);
        }
        catch (OperationCanceledException)
        {
            return PlatformResult.Canceled("Uploading recent players was cancelled.");
        }
    }

    /// <summary>
    /// Sets rich presence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><paramref name="status"/> is a presence string id, not display text.</b> Xbox
    /// rich presence strings live in the title's service configuration and are referenced
    /// by id; XSAPI has no way to show free text a title invents at runtime. The
    /// interface's parameter is a bare string because the offline provider has no such
    /// constraint, so this provider treats it as the configured id and an empty value as
    /// "active in title, no specific string".
    /// </para>
    /// <para>
    /// That means an id this title's service configuration does not define will be
    /// rejected by the service rather than caught here. There is no configuration to
    /// validate against in this checkout - see <c>docs/design-notes.md</c> on the derived
    /// SCID, which this depends on and which has never been confirmed against a live
    /// service.
    /// </para>
    /// </remarks>
    public async Task<PlatformResult> SetPresenceAsync(
        string status,
        CancellationToken cancellationToken = default)
    {
        if (!_xbl.TryGetContext(out var context) || _runtime.Runtime is not { } runtime)
        {
            return PlatformResult.Unavailable("The Xbox network is not available, or no user is signed in.");
        }

        try
        {
            var scid = runtime.XboxLive.Scid;
            var ids = string.IsNullOrWhiteSpace(status) || string.IsNullOrEmpty(scid)
                ? null
                : new PresenceRichPresenceIds(scid, status);

            await _runtime.Dispatcher
                .Marshal(context.Presence.SetPresenceAsync(
                    isUserActiveInTitle: true,
                    ids,
                    cancellationToken))
                .ConfigureAwait(false);

            return PlatformResult.Ok();
        }
        catch (GameRuntimeException ex)
        {
            return PlatformResult.Fail(PlatformStatus.Failed, "Presence could not be set.", ex.Message);
        }
        catch (OperationCanceledException)
        {
            return PlatformResult.Canceled("Setting presence was cancelled.");
        }
    }

    /// <summary>
    /// Clamps a player count into the unsigned range XSAPI takes. Zero tells it to ignore
    /// the field, which is also the right answer for a negative count.
    /// </summary>
    private static uint ToPlayerCount(int value) => value <= 0 ? 0u : (uint)value;

    /// <summary>Unsubscribes from activation events. Safe to call more than once.</summary>
    internal void Shutdown()
    {
        if (_activation is null)
        {
            return;
        }

        try
        {
            _activation.Activated -= OnActivated;
        }
        catch (ObjectDisposedException)
        {
            // The runtime was torn down first; the registration went with it.
        }

        _activation = null;
    }
}
