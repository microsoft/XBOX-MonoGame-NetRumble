using NetRumble.Game.UI;
using NetRumble.Game.UI.Screens;
using NetRumble.Platform;
using NetRumble.Platform.Diagnostics;

namespace NetRumble.Game;

/// <summary>
/// Handles the platform process lifecycle - suspend, resume, constrain and unconstrain
/// (XR-001) - porting the <c>_notification()</c> block and its handlers in
/// <c>scripts/main.gd</c>.
/// </summary>
/// <remarks>
/// <para>
/// Its own object rather than more methods on <see cref="NetRumbleGame"/>, for the reason
/// the GDScript gives for keeping this logic together: the four events are one policy
/// with one piece of shared state (whether a match was taken away), and splitting them
/// across the game class would scatter that state through it.
/// </para>
/// <para>
/// <b>Two sources, deliberately.</b> On console the events arrive from
/// <see cref="IPlatformRuntime.LifecycleChanged"/>, which the GDK provider raises from
/// real PLM notifications. On a desktop dev box the GDK delivers no PLM at all, so
/// constrain is additionally driven from window activation through
/// <see cref="SetConstrained"/> - which is both the honest desktop analogue of the Guide
/// covering the title and the only way this behaviour can be exercised without a console.
/// De-duplication in <see cref="SetConstrained"/> means a console receiving both sources
/// acts once.
/// </para>
/// </remarks>
public sealed class LifecycleCoordinator
{
    private readonly UiContext _context;
    private readonly InviteRouter _invites;

    private bool _constrained;

    /// <summary>XR-115: the player currently has no controller.</summary>
    private bool _controllerMissing;
    private bool _matchDroppedBySuspend;

    /// <summary>
    /// Whether a live session existed as of the last frame (XR-001).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A snapshot, not a query, because the reader is not the game thread.</b> The
    /// suspend notification arrives on an OS thread while the game loop is still running,
    /// and the obvious implementation - walk the screen stack and ask whichever screen is
    /// on top whether it is online - reads a <see cref="List{T}"/> that the game thread
    /// may be pushing to or popping from at that instant. There is no lock on that stack
    /// anywhere in the title, and adding one would mean taking it on every push, pop and
    /// find on the hot path to serve a caller that arrives at most once per session.
    /// </para>
    /// <para>
    /// So the game thread publishes the one bit the suspend handler needs, once a frame,
    /// through <see cref="Tick"/>. The handler reads a <see langword="volatile"/>
    /// <see cref="bool"/> and touches nothing shared. Being a frame stale is immaterial:
    /// the question is whether there is a session worth abandoning, and a session that
    /// began or ended within the last 16ms of a suspend is abandoned correctly either way
    /// - <see cref="IPartyService.LeaveAsync"/> on no session is a no-op, and the activity
    /// delete below is idempotent.
    /// </para>
    /// </remarks>
    private volatile bool _sessionOnline;

    public LifecycleCoordinator(UiContext context, InviteRouter invites)
    {
        _context = context;
        _invites = invites;
        _context.Platform.Runtime.LifecycleChanged += OnLifecycleChanged;
        _context.Platform.Identity.AccountChanged += OnAccountChanged;
        _context.Platform.Runtime.ConnectivityChanged += OnConnectivityChanged;
    }

    /// <summary>
    /// Connectivity came or went. The only thing this level cares about is the
    /// restoration: achievement reports that failed while the console was off the network
    /// are still owed, and nothing else will re-attempt them until the next sign-in
    /// (XR-055). Losing connectivity is handled where it matters - by the match screens,
    /// which have a session to tear down.
    /// </summary>
    private void OnConnectivityChanged(bool online)
    {
        if (online)
        {
            _context.Achievements.RetryPending();
        }
    }

    /// <summary>
    /// True while the title is constrained. Read by a <see cref="MatchDirector"/> built
    /// while the title is <i>already</i> constrained, whose own constrain notification
    /// fired before it existed.
    /// </summary>
    public bool IsConstrained => _constrained;

    /// <summary>
    /// True while something outside the match is holding it (XR-001 constrain, XR-115
    /// controller loss). Read by a <see cref="MatchDirector"/> built while one of those
    /// is <i>already</i> true, whose own notification fired before it existed.
    /// </summary>
    public bool IsExternallyPaused => _constrained || _controllerMissing;

    /// <summary>
    /// Publishes the state the suspend handler is allowed to read. Called once a frame
    /// from <see cref="NetRumbleGame.Update"/>, after the screens have run.
    /// </summary>
    /// <remarks>
    /// This is the only place the screen stack is walked on behalf of the lifecycle path,
    /// and it runs on the thread that owns it. See <see cref="_sessionOnline"/>.
    /// </remarks>
    public void Tick()
        => _sessionOnline = _context.Screens.Find<LobbyScreen>()?.IsOnline == true
            || _context.Screens.Find<GameplayScreen>()?.IsOnline == true;

    private void OnLifecycleChanged(PlatformLifecycleEvent change)
    {
        switch (change)
        {
            case PlatformLifecycleEvent.Suspending:
                OnSuspending();
                break;
            case PlatformLifecycleEvent.Resumed:
                OnResumed();
                break;
            case PlatformLifecycleEvent.Constrained:
                SetConstrained(true);
                break;
            case PlatformLifecycleEvent.Unconstrained:
                SetConstrained(false);
                break;
            case PlatformLifecycleEvent.UserChanged:
                // Deliberately nothing. This enum member predates
                // IIdentityService.AccountChanged and is coarser than it: a handler given
                // only "the user changed" cannot tell a privilege re-evaluation from an
                // account going away, and those want opposite responses. OnAccountChanged
                // below is the one that acts. No provider raises this.
                break;
        }
    }

    /// <summary>
    /// The suspend handler. Everything it does, it does now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This runs inside the platform's suspend deadline and the process is frozen the
    /// instant it returns. So there is no <c>await</c> here, no continuation and no timer:
    /// work parked behind any of those does not run before the freeze, and if the platform
    /// chooses to terminate the title rather than resume it - which is the common outcome,
    /// not the rare one - it never runs at all. Straight-line code only, cheapest first.
    /// </para>
    /// <para>
    /// <b>Each step is independently guarded.</b> This runs on an OS thread with native
    /// GDK code directly above it, so an escaping exception has no managed handler to
    /// reach - <c>PlmInterop</c> catches it at the boundary, but by then the
    /// remaining steps have been skipped. Guarding them separately means a failed durable
    /// write still leaves the session abandoned and the audio stopped, which is the order
    /// of importance a suspend deadline demands.
    /// </para>
    /// </remarks>
    private void OnSuspending()
    {
        // First, because it is the only part that must survive a terminate-without-resume.
        Step("lifecycle: suspend commit failed", CommitDurableState);

        // Then drop the session. Holding an open Party network across a suspend leaves a
        // peer that never reconnects and an RPC storm against a dead network; the match is
        // not worth either.
        Step(
            "lifecycle: suspend session abandon failed",
            () => _matchDroppedBySuspend = AbandonForSuspend());

        Step("lifecycle: suspend audio stop failed", _context.Audio.StopAll);
    }

    private static void Step(string context, Action body)
    {
        try
        {
            body();
        }
        catch (Exception ex)
        {
            CrashLog.Fatal(context, ex);
        }
    }

    /// <summary>
    /// The durable writes shared by the suspend and user-removed paths, and nothing else.
    /// Ported from <c>Services._commit_durable_state()</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The local tier only. The cloud push both of these would otherwise fire is a remote
    /// round trip that cannot land inside either deadline, and waiting on it would burn
    /// the budget for the work that can. <c>NetRumbleGame.OnExiting</c> is where the cloud
    /// write gets its (bounded) chance, because a player-initiated quit is the one ending
    /// that is not a race.
    /// </para>
    /// <para>
    /// Match history is absent because it needs nothing: <see cref="MatchHistoryStore"/>
    /// writes through at the end of each match rather than holding entries in memory, so
    /// there is never anything uncommitted to lose. Godot has to write it here only
    /// because its history is an in-memory array.
    /// </para>
    /// </remarks>
    private void CommitDurableState()
    {
        var profile = _context.Profile;
        profile.SuppressCloudPush = true;
        profile.Save();
        profile.SuppressCloudPush = false;

        _context.Achievements.CommitLocal();
    }

    /// <summary>
    /// Leaves a live session, answering whether there was one. Fire-and-forget on purpose:
    /// the suspend deadline does not allow waiting for the leave to be acknowledged, and a
    /// session the title never gets to tear down cleanly is still better dropped locally
    /// than held across a freeze.
    /// </summary>
    private bool AbandonForSuspend()
    {
        if (!_sessionOnline)
        {
            return false;
        }

        _ = _context.Platform.Party.LeaveAsync();

        // XR-064 / XR-067: a suspend is a session teardown like any other - the shell
        // must not keep advertising a joinable activity for a title that is about to be
        // frozen, and whoever was in the roster still has to reach Recently Played With.
        //
        // Declared rather than issued. Nothing started here can be relied on to finish
        // before the freeze either way, but an intent survives it: the coordinator's
        // worker is frozen with the rest of the process and converges on resume, where a
        // bare fire-and-forget task would have had its single attempt cut off and lost.
        _context.Activity.Clear();
        _ = _context.Activity.FlushAsync();

        return true;
    }

    /// <summary>
    /// The resume handler. Unlike suspend this is not deadline-bound, so it may defer -
    /// and it has to, because telling the player what happened means a dialog.
    /// </summary>
    private void OnResumed()
    {
        // Restarted rather than merely unmuted: the streams were playing into a device the
        // platform took away, so they are started again from a known state.
        _context.Audio.PlayMusic();

        // XR-055: a report that failed in the seconds before the suspend - or against a
        // service that had already begun tearing down - is still owed. Nothing in the game
        // has changed across the freeze, but the service's willingness to listen has.
        _context.Achievements.RetryPending();

        // XR-048: everything the title knows about the account was answered before the
        // suspend, and the shell is exactly where a player changes their gamertag, their
        // privileges or their privacy settings while the title is frozen. A stale
        // privilege answer reads as a granted one, so these are dropped rather than
        // trusted, and the profile is re-read outright.
        RefreshAccountState();

        if (_matchDroppedBySuspend)
        {
            _matchDroppedBySuspend = false;
            RouteAfterSuspend();
        }

        // The constrain state is deliberately left alone. A resume arrives while the title
        // is still constrained - the Guide is what suspended it and is still up - so the
        // unconstrain notification is what ends the pause, not this.
    }

    /// <summary>Puts the player back somewhere that makes sense after a suspend took their
    /// match away.</summary>
    private void RouteAfterSuspend()
    {
        _context.Screens.ReplaceAll(new MainMenuScreen());

        // An invite accepted while the title was suspended is why the player came back. It
        // redeems off the screen change above, and it is a better answer to "what now" than
        // a dialog about a match they have already moved on from, so it gets the front end.
        if (_invites.HasPendingInvite)
        {
            return;
        }

        _context.Screens.ShowDialog(
            "Match Ended",
            "The match was left when the game was suspended.",
            DialogSeverity.Default);
    }

    /// <summary>
    /// The platform changed something about the signed-in account (XR-052, XR-115).
    /// Ported from <c>Services._on_user_changed()</c>.
    /// </summary>
    private void OnAccountChanged(PlatformAccountChange change)
    {
        switch (change)
        {
            case PlatformAccountChange.SigningOut:
                OnUserRemoved();
                break;

            case PlatformAccountChange.SignedInAgain:
            case PlatformAccountChange.PrivilegesChanged:
                ResetAccountState();

                // XR-048: clearing the caches only guarantees the next answer is fresh.
                // The gamertag is not cached behind a service the player will query
                // again - it is held on CurrentUser and read from there forever - so it
                // has to be pulled back explicitly.
                _ = _context.Platform.Identity.RefreshUserAsync();
                break;

            case PlatformAccountChange.SignedOut:
                // The commit already happened on SigningOut, while the account was still
                // the signed-in one. Doing it again here would write state that has since
                // been cleared over the copy that matters.
                break;
        }
    }

    /// <summary>
    /// The signed-in user is being removed. Ported from
    /// <c>Services.persist_user_state()</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Commits, clears, and does nothing else.</b> There is deliberately no navigation,
    /// no Party teardown and no sign-out deferral, for one reason that covers all three:
    /// the platform terminates a title whose user has signed out, so this is the last
    /// moment this code is guaranteed to run. A screen transition is scenery nobody will
    /// see; a Party leave is a round trip that will not be acknowledged; and a deferral
    /// buys time for slow teardown, which two local writes do not need.
    /// </para>
    /// <para>
    /// Synchronous throughout for the same reason - see <see cref="CommitDurableState"/>.
    /// </para>
    /// </remarks>
    private void OnUserRemoved()
    {
        CommitDurableState();
        ResetAccountState();

        // Now drop what belonged to the departing player, so that if this title does keep
        // running - the platform is entitled to let it - nothing is left holding their
        // identity or accumulating against their counters.
        _context.Achievements.Clear();
        _context.Profile.SetIdentity(string.Empty, string.Empty);

        // Releases the settings file as well as the identity (XR-052). Without this the
        // profile stays pointed at the departing player's file, and anything the title
        // writes while nobody is signed in lands in their settings under their account.
        _context.Profile.Rehome(null);

        // If the platform does terminate on sign-out (the console cert case), the process
        // ends before this line's effect is ever seen, and it costs nothing. If it does
        // not - PC, or a console that keeps a title running with no signed-in user - this
        // is what stops the player being left on a screen for an identity that just
        // vanished, with no way back in (XR-115).
        _context.Screens.ReplaceAll(new AcquireUserScreen());
    }

    /// <summary>
    /// Invalidates everything cached about the account. Ported from
    /// <c>Services._reset_account_state()</c>.
    /// </summary>
    /// <remarks>
    /// Every one of these was answered using credentials that have just been replaced or
    /// revoked, which makes them stale rather than absent - the more dangerous of the two,
    /// because a stale privilege answer reads as a granted one. The social graph is the
    /// clearest case: it is started for one account, and a group left in place carries on
    /// reporting the previous player's friends.
    /// </remarks>
    private void ResetAccountState()
    {
        _context.Platform.Privileges.ClearCache();
        _context.Platform.Privacy.ClearCache();
        _context.Platform.Social.Clear();

        // XR-046/XR-052: gamerpics are read through the previous account's relationships,
        // so the textures behind them are that account's answers and not this one's.
        _context.Pictures.Clear();
    }

    /// <summary>
    /// The same account, but answers about it that may have moved while the title was
    /// not running (XR-048).
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="ResetAccountState"/> this does not drop the social graph: the
    /// friends list belongs to the same account it did before the suspend, and dropping
    /// it would cost a full re-query on the next overlay for no correctness gain. The
    /// privilege and privacy answers are dropped because they are permission decisions,
    /// and a permission that has been revoked in the shell must not still read as
    /// granted here.
    /// </remarks>
    private void RefreshAccountState()
    {
        _context.Platform.Privileges.ClearCache();
        _context.Platform.Privacy.ClearCache();
        _ = _context.Platform.Identity.RefreshUserAsync();
    }

    /// <summary>
    /// Constrain and unconstrain. The match keeps its phase and its place in the roster; it
    /// simply stops advancing, so nothing here is broadcast to the other peers and nothing
    /// here is written to <see cref="PlayerProfile"/> - opening the Guide is not a settings
    /// change.
    /// </summary>
    public void SetConstrained(bool constrained)
    {
        if (_constrained == constrained)
        {
            return;
        }

        _constrained = constrained;
        _context.Audio.SetSystemMuted(constrained);
        ApplyExternalPause();
    }

    /// <summary>
    /// The player has lost, or regained, their controller (XR-115).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Feeds the same external-pause gate the constrain does, and for the same reason: a
    /// player with nothing to press must not be losing a match while they go and find a
    /// pad. <see cref="MatchDirector"/> decides whether the pause actually freezes
    /// anything, and it only does so offline - a networked match cannot stop because one
    /// player unplugged something, and the prompt is still up over it either way.
    /// </para>
    /// <para>
    /// Audio is deliberately <i>not</i> muted here, unlike the constrain: the title is
    /// still in the foreground and the player has not been taken away from it.
    /// </para>
    /// </remarks>
    public void SetControllerMissing(bool missing)
    {
        if (_controllerMissing == missing)
        {
            return;
        }

        _controllerMissing = missing;
        ApplyExternalPause();
    }

    /// <summary>
    /// Pushes the combined external-pause state at the live match, if there is one.
    /// </summary>
    /// <remarks>
    /// Reached through the screen stack for the same reason the shutdown path is: there
    /// is at most one director, it exists only while a match does, and this class should
    /// not have to know which screen is currently holding it.
    /// </remarks>
    private void ApplyExternalPause()
        => _context.Screens.Find<GameplayScreen>()?.Match?.Director.SetExternallyPaused(IsExternallyPaused);
}
