using NetRumble.Core.Objects;
using System.Linq;
using Microsoft.Xna.Framework;
using NetRumble.Core;
using NetRumble.Core.Net;
using NetRumble.Core.Tuning;
using NetRumble.Game.Gameplay;
using NetRumble.Game.UI.Elements;
using NetRumble.Platform;
using NetRumble.Platform.Diagnostics;

namespace NetRumble.Game.UI.Screens;

/// <summary>
/// Ports <c>scripts/ui/screens/gameplay_screen.gd</c> (itself <c>Game/Screens/GameplayScreen</c>):
/// hosts one <see cref="MatchSession"/>, draws the HUD over it and routes the pause and
/// chat/voice actions.
/// </summary>
/// <remarks>
/// <para>
/// The GDScript spent most of its bulk wiring together the world, starfield, particle
/// manager and camera as separate scene-tree children, each guarded with
/// <c>ResourceLoader.exists()</c> so the screen degraded gracefully in isolation. All of
/// that now lives in <see cref="MatchSession"/>, which is a single object this screen
/// owns for its lifetime - there is no scene tree to assemble or a missing-scene case to
/// guard against, so this screen is left with exactly what the GDScript's HUD actually
/// drew plus the state-machine plumbing <c>MatchDirector</c>'s signals fed it.
/// </para>
/// <para>
/// <b>Chat is not a screen.</b> <see cref="ChatEntry"/> is drawn and updated inline by
/// this screen rather than pushed onto <see cref="ScreenManager"/>, mirroring the
/// GDScript adding <c>nr_chat_entry.tscn</c> as a child instead of a new scene. That
/// means this screen - not the manager - is responsible for the one hazard the fidelity
/// pass called out explicitly: while the chat box is open, ship movement must stop
/// reading the keyboard, or WASD and the arrow keys steer the ship and type into chat at
/// the same time. <see cref="Update"/> does this by passing <c>acceptInput: false</c> to
/// <see cref="MatchSession.Update"/> whenever <see cref="ChatEntry.IsOpen"/> is true -
/// exactly the flag the session already uses to coast the ship while a popup screen owns
/// input, reused here for a modal that is not a screen at all.
/// </para>
/// </remarks>
public sealed class GameplayScreen : Screen
{
    private readonly IMatchNetwork _network;

    private readonly RosterOverlay _roster = new();
    private readonly SttOverlay _stt = new();
    private readonly ChatEntry _chatEntry = new();

    private MatchSession? _match;
    private MatchDirector? _director;
    private MatchAchievementWatcher? _achievements;
    private UiContext _context = null!;

    private int _countdownSeconds;
    private bool _matchFinished;
    private Task? _leaveTask;

    /// <summary>
    /// Set when this screen passes its live session to the lobby for another match, so
    /// <see cref="Exit"/> leaves the network alone instead of disposing it (XR-003).
    /// </summary>
    private bool _networkHandedOn;

    /// <summary>
    /// How long the control reminder stays on screen once the match starts accepting
    /// input.
    /// </summary>
    /// <remarks>
    /// Long enough to read without hunting for it, short enough that it is gone before
    /// it competes with the HUD. It exists because firing is bound to the right stick,
    /// which bug-bash players could not discover: see
    /// <see cref="UiInput.MatchControlsHint"/>.
    /// </remarks>
    private const float ControlsHintSeconds = 10f;

    /// <summary>Seconds of control reminder left; zero or less once it has faded out.</summary>
    private float _controlsHint = ControlsHintSeconds;

    /// <summary>
    /// How long a reported loss of connectivity must persist before it ends an online
    /// match. See <see cref="OnConnectivityChanged"/> for why it is not immediate.
    /// </summary>
    private const float ConnectivityGraceSeconds = 8f;

    /// <summary>Seconds left in that grace period; negative when none is running.</summary>
    private float _offlineGrace = -1f;

    /// <summary>Whether this match is a live online session - lets
    /// <see cref="Game.InviteRouter"/> ask "are we already mid-match?" the same way
    /// <see cref="LobbyScreen.IsOnline"/> does for the lobby.</summary>
    public bool IsOnline => !_network.IsOffline;

    /// <summary>
    /// XR-047: the players in this match who have a gamercard behind their name, for the
    /// pause menu's profile route. The in-match roster overlay shows those names but
    /// takes no input - it is drawn over a live simulation - so the route belongs on the
    /// pause menu, which is where the player already goes to do anything that is not
    /// flying.
    /// </summary>
    public IReadOnlyList<ProfileList.ProfileEntry> ProfileEntries
        => ProfileList.Collect(
            _network.Players.Values.Select(player => (player.DisplayName, player.XboxUserId)));

    /// <summary>
    /// The running session, for observation only. Null before <see cref="Enter"/>.
    /// </summary>
    /// <remarks>
    /// Exists for <see cref="Autopilot"/>, which has to read the world to tell a match
    /// that is replicating from one that merely has not crashed. Deliberately internal
    /// and deliberately not a way to drive anything: an autopilot that wrote to the
    /// world would stop being a test of the game.
    /// </remarks>
    internal MatchSession? Match => _match;

    public GameplayScreen(IMatchNetwork network)
    {
        _network = network ?? throw new ArgumentNullException(nameof(network));

        // gameplay_screen.gd's _init() sets allow_back = false: there is no "back" out of
        // a live match, only Resume/Leave from the pause menu this screen opens.
        AllowBack = false;
    }

    public override void Enter(UiContext context)
    {
        _context = context;

        CrashLog.Mark("gameplay: enter, building match session");

        _match = new MatchSession(context.Assets, context.Theme, context.Audio, context.Random, context.Profile, _network);
        _director = _match.Director;
        _achievements = new MatchAchievementWatcher(context.Achievements, _match, _network);

        _director.CountdownChanged += OnCountdownChanged;
        _director.MatchCompleted += OnMatchCompleted;
        _director.MatchCanceled += OnMatchCanceled;

        // A match built while the title is already constrained, or while the player has no
        // controller, never saw that notification - it fired before this director existed -
        // so it adopts the current state rather than waiting for the next edge (XR-001,
        // XR-115).
        _director.SetExternallyPaused(context.Game.Lifecycle?.IsExternallyPaused == true);

        context.Platform.Party.NetworkDestroyed += OnServerDisconnected;
        context.Platform.Party.ChatTextReceived += OnChatTextReceived;

        // XR-074. Only an *online* match is at stake: a practice match is entirely local,
        // and ending one because the console dropped off the network would be nonsense.
        if (_network is PartyMatchNetwork)
        {
            context.Platform.Runtime.ConnectivityChanged += OnConnectivityChanged;
        }

        // XR-067: a peer who disconnects mid-match - rather than finishing or being
        // left cleanly - must still be reported before they vanish from the roster.
        // The lobby already reports continuously up to this point; this covers the
        // match itself, where the roster is otherwise static because late joins are
        // refused.
        if (IsOnline)
        {
            _network.RosterChanged += OnRosterChangedDuringMatch;
        }

        _chatEntry.Submitted += OnChatSubmitted;
        _chatEntry.Cancelled += () => _chatEntry.Close();

        // MatchDirector.HandlePlayersLoading only advances once every player's InGame
        // flag is set; PartyMatchNetwork.SendLoaded is what flips the local one and
        // broadcasts it. OfflineMatchNetwork sets it in its constructor instead, since a
        // solo session has no host to report to, so there is nothing to call there.
        //
        // IMatchNetwork itself has no ReportLocalPlayerLoaded/SendLoaded member - the
        // pattern match below reaches past the interface to the one implementation that
        // needs the call. This should move onto the interface so a match can be started
        // without knowing which concrete network it was handed.
        if (_network is PartyMatchNetwork online)
        {
            online.SendLoaded();
        }

        CrashLog.Mark("gameplay: enter complete");
    }

    public override void Exit()
    {
        if (_director is not null)
        {
            _director.CountdownChanged -= OnCountdownChanged;
            _director.MatchCompleted -= OnMatchCompleted;
            _director.MatchCanceled -= OnMatchCanceled;
        }

        _context.Platform.Party.NetworkDestroyed -= OnServerDisconnected;
        _context.Platform.Party.ChatTextReceived -= OnChatTextReceived;
        _context.Platform.Runtime.ConnectivityChanged -= OnConnectivityChanged;
        _network.RosterChanged -= OnRosterChangedDuringMatch;

        _achievements?.Dispose();
        _achievements = null;

        _match?.Dispose();
        _match = null;
        _director = null;

        if (!_networkHandedOn && _network is IDisposable disposableNetwork)
        {
            disposableNetwork.Dispose();
        }

        // The OS pointer is a process-wide setting, so it has to be handed back whether
        // the match ended, was cancelled or the player quit out of it. Anything less can
        // leave the menus with no cursor at all.
        _context.Game.IsMouseVisible = true;
    }

    /// <summary>
    /// Freezes the match the way pausing the Godot scene tree did. This is belt and
    /// braces rather than load-bearing: <see cref="ScreenManager"/> only calls
    /// <see cref="Update"/> on the active (topmost, non-popup-covered) screen, so once
    /// <see cref="GameMenuScreen"/> is pushed this screen's <see cref="Update"/> - and
    /// therefore <see cref="MatchSession.Update"/> - simply stops being called at all.
    /// Setting the flag anyway means the freeze is stated explicitly rather than being an
    /// accident of the screen stack, and keeps the session correct if anything is ever
    /// drawn or ticked for a covered screen in the future.
    /// </summary>
    public override void OnCovered()
    {
        if (_match is not null)
        {
            _match.IsSimulating = false;
        }

        // Popups draw above the world, and the reticle is deliberately world-space, so a
        // covered match hands the OS pointer back rather than leaving the player to steer
        // the pause menu with a crosshair that no longer updates.
        _context.Game.IsMouseVisible = true;
    }

    /// <summary>Thaws the match. See <see cref="OnCovered"/>.</summary>
    public override void OnRevealed()
    {
        if (_match is not null)
        {
            _match.IsSimulating = true;
        }
    }

    public override void Update(UiContext context)
    {
        TickConnectivityGrace(context.Delta);

        if (_match is null || _director is null)
        {
            return;
        }

        CrashLog.MarkOnce("gameplay-update", "gameplay: first update");
        // The chat box is not a screen, so acceptInput is this screen's own decision
        // rather than something ScreenManager already arranged by covering it with a
        // popup - see the class remarks for why this matters.
        var acceptInput = !_chatEntry.IsOpen;
        _match.Update(
            context.Delta,
            context.Time,
            acceptInput,
            context.Input.Keyboard,
            context.Input.GamePad,
            context.Input.Mouse,
            context.Input.IsMouseActiveDevice);

        // The OS arrow would otherwise sit on top of the reticle, which is the thing that
        // replaced it. Restored by the same call the moment the reticle stops drawing, and
        // unconditionally on teardown - see Dispose.
        context.Game.IsMouseVisible = !_match.Reticle.IsVisible;

        // After the session has ticked, so the watcher samples this frame's state rather
        // than the previous one's.
        _achievements?.Update();

        if (_controlsHint > 0f)
        {
            _controlsHint -= context.Delta;
        }

        _stt.Update(context);
        _chatEntry.Update(context);
    }

    public override bool HandleAction(UiContext context, MenuAction action)
    {
        if (_chatEntry.IsOpen)
        {
            // Back and the pause button both close the chat box rather than opening the
            // game menu underneath it - nr_chat_entry.gd's own ui_back_action handler
            // took priority over the screen's for the same reason. Every other action is
            // swallowed too: none of them mean anything while typing, and letting Accept
            // or a direction fall through to the (nonexistent) HUD widgets would be a
            // silent no-op at best.
            if (action is MenuAction.Back or MenuAction.GameMenu)
            {
                _chatEntry.Close();
            }

            return true;
        }

        if (action == MenuAction.GameMenu)
        {
            context.Screens.Push(new GameMenuScreen());
            return true;
        }

        if (action == MenuAction.VoiceChat)
        {
            OpenChatEntry(context);
            return true;
        }

        return base.HandleAction(context, action);
    }

    /// <summary>
    /// Ports <c>_open_chat</c>'s single-instance guard and its privilege check. Without
    /// the guard, pressing the chat action again while the box is open would be a no-op
    /// anyway (<see cref="ChatEntry.Open"/> just clears the text), but the privilege
    /// check matters: an account without the communications privilege gets told why
    /// instead of being handed an entry box whose send always fails (XR-045).
    /// </summary>
    private void OpenChatEntry(UiContext context)
    {
        if (_chatEntry.IsOpen)
        {
            return;
        }

        if (!context.Platform.Party.ChatAllowed)
        {
            // IPartyService has no per-account restriction-reason string the way
            // NetManager.chat_restriction_reason() did; this is the one generic message
            // the interface can actually support today.
            _stt.AddLine(string.Empty, "Chat is unavailable for this account.");
            return;
        }

        _chatEntry.Open(context);
    }

    private void OnChatSubmitted(string text)
    {
        // ChatTextEntryUIElement's OK handler leaves the dialog open when the send is
        // refused, so a typed message survives a failed attempt for a retry; an empty
        // message always "succeeds" by simply closing, matching text.is_empty() short-
        // circuiting the send in the GDScript.
        if (text.Length == 0)
        {
            _chatEntry.Close();
            return;
        }

        // XR-018: outgoing chat is the title's primary UGC surface and must be run
        // through platform string verification before it ever reaches another player.
        // Fire-and-forget from the caller's point of view - the entry box stays open
        // until the verification (and then the send) resolves, exactly as a failed
        // send already left it open for a retry.
        _ = VerifyAndSendAsync(text);
    }

    private async Task VerifyAndSendAsync(string text)
    {
        PlatformResult<string> verified;

        try
        {
            verified = await _context.Platform.Moderation.VerifyTextAsync(text);
        }
        catch (Exception)
        {
            // Best-effort per the interface's own contract elsewhere in this port: a
            // provider that throws must not take the match down, but it must also not
            // let unverified text through, so this counts as a refusal.
            _stt.AddLine(string.Empty, "Your message could not be sent.");
            _chatEntry.Retry(_context);
            return;
        }

        if (!verified.Succeeded || verified.Value is not { Length: > 0 } sanitized)
        {
            // XR-022: the provider's own text names the moderation service and its error
            // codes, which is log material rather than something to put in front of a
            // player, so only the refusal itself is surfaced.
            if (verified.Message is { Length: > 0 } detail)
            {
                CrashLog.MarkOnce("chat-verify-refused", $"chat: verification refused a message - {detail}");
            }

            _stt.AddLine(string.Empty, "Your message could not be sent.");
            _chatEntry.Retry(_context);
            return;
        }

        var result = _context.Platform.Party.SendChatText(sanitized);

        if (result.Succeeded)
        {
            _chatEntry.Close();
            return;
        }

        if (!string.IsNullOrEmpty(result.Message))
        {
            CrashLog.MarkOnce("chat-send-failed", $"chat: send refused - {result.Message}");
        }

        _stt.AddLine(string.Empty, "Your message could not be sent.");

        // The box stays open for a retry, which on a platform that owns text entry
        // means offering its keyboard again with the refused message still in it -
        // otherwise the retry is a box the player cannot type into.
        _chatEntry.Retry(_context);
    }

    private void OnChatTextReceived(int senderPeerId, string message, ChatTextKind kind)
    {
        var name = _network.Players.TryGetValue(senderPeerId, out var state) ? state.DisplayName : "Player";
        _stt.AddLine(name, message, kind);
    }

    /// <summary>
    /// Reports the current roster as encountered (XR-067) whenever it changes mid-match -
    /// most importantly when a peer disconnects, which is exactly the "early leaver" case
    /// the requirement exists for. <see cref="IActivityService.ReportRecentPlayersAsync"/>
    /// de-duplicates, so reporting an unchanged roster again is a cheap no-op.
    /// </summary>
    private void OnRosterChangedDuringMatch()
    {
        var xuids = _network.Players.Values
            .Where(p => p.PeerId != _network.LocalPeerId && !string.IsNullOrEmpty(p.XboxUserId))
            .Select(p => p.XboxUserId)
            .ToArray();

        if (xuids.Length == 0)
        {
            return;
        }

        _context.Activity.ReportRecentPlayers(xuids);

        // XR-015: covers the defence-in-depth case of a restriction changing (e.g. a
        // platform mute list update) while the match is underway. Late joins are
        // refused, so this never has to evaluate a peer the lobby has not already seen.
        _ = EvaluatePrivacyDuringMatchAsync(xuids);
    }

    private async Task EvaluatePrivacyDuringMatchAsync(IReadOnlyList<string> xuids)
    {
        PlatformResult<IReadOnlyDictionary<string, PrivacyVerdict>> result;

        try
        {
            result = await _context.Platform.Privacy.EvaluateAsync(xuids);
        }
        catch (Exception)
        {
            return;
        }

        if (!result.Succeeded || result.Value is not { } verdicts)
        {
            return;
        }

        var party = _context.Platform.Party;

        foreach (var player in _network.Players.Values)
        {
            if (player.PeerId == _network.LocalPeerId || string.IsNullOrEmpty(player.XboxUserId))
            {
                continue;
            }

            if (verdicts.TryGetValue(player.XboxUserId, out var verdict))
            {
                party.SetPeerRestrictions(player.PeerId, verdict.AllowVoice, verdict.AllowText);
            }
        }
    }

    private void OnCountdownChanged(int secondsRemaining) => _countdownSeconds = secondsRemaining;

    private void OnMatchCompleted(MatchResult result)
    {
        if (_matchFinished)
        {
            return;
        }

        _matchFinished = true;

        // Before the dialog, and before Exit() disposes the watcher: this is the only
        // point at which the final standings, the survival flag the watcher has been
        // keeping, and a live platform provider all exist at once.
        _achievements?.OnMatchCompleted(result);
        RecordMatchHistory(result);

        // XR-064/XR-067/XR-003: a completed match keeps its session - the party is what
        // the players re-form the next match out of, and tearing it down here is what
        // made everyone re-invite each other between rounds. The activity is still
        // cleared for the duration of the standings dialog (nobody is joinable while the
        // lobby does not exist yet) and re-published by the lobby when it re-enters.
        //
        // Every other exit from this screen still goes through LeavePartyAsync: leaving,
        // a cancellation and a disconnect all end the session as well as the match.
        if (IsOnline)
        {
            _context.Activity.Clear();
            _ = _context.Activity.FlushAsync();
        }

        var message = FormatStandings(result);

        // XR-047: the standings name every player in the match, so they owe a route to
        // each of their gamercards. The row is omitted outright when nobody in the list
        // has one - a practice match against bots has no profiles to show.
        var profiles = ProfileList.Collect(
            result.Standings.Select(standing => (
                standing.DisplayName,
                _network.Players.TryGetValue(standing.PeerId, out var player)
                    ? player.XboxUserId
                    : string.Empty)));

        _context.Screens.ShowDialog(
            "Match Complete",
            message,
            DialogSeverity.Default,
            showCancel: false,
            onDismissed: dismissed => ReturnToLobby(),
            extraLabel: profiles.Count > 0 ? "View Gamercards" : string.Empty,
            onExtra: profiles.Count > 0
                ? () => _context.Screens.Push(new ProfileList("Players", profiles))
                : null);
    }

    /// <summary>
    /// Appends the finished match to the local history the Extras screen reads, and
    /// commits whatever achievement progress it produced.
    /// </summary>
    /// <remarks>
    /// Fire-and-forget: the results dialog is already up, and neither write is something
    /// a player should be made to wait for. The stats flush is here rather than only at
    /// shutdown so a match's progress survives the process being killed rather than
    /// closed - which, on a devkit, is how it usually ends.
    /// </remarks>
    private void RecordMatchHistory(MatchResult result)
    {
        var context = _context;
        _ = Task.Run(async () =>
        {
            await context.MatchHistory.RecordAsync(result, _network.LocalPeerId);
            await context.Achievements.FlushAsync();
        });
    }

    private void OnMatchCanceled()
    {
        if (_matchFinished)
        {
            return;
        }

        _matchFinished = true;

        _context.Screens.ShowDialog(
            "Match Canceled",
            "A player failed to finish loading.",
            DialogSeverity.Warning,
            showCancel: false,
            onDismissed: dismissed => _ = LeaveAndReturnToMainMenuAsync());
    }

    /// <summary>
    /// Tears down the platform session before another screen can attempt to host or join.
    /// </summary>
    private async Task LeavePartyAsync()
    {
        await _context.Platform.Party.LeaveAsync();

        if (IsOnline)
        {
            // XR-064 / XR-067: belt and braces alongside LobbyScreen's own teardown -
            // an activity published (or re-published) during the match must not
            // outlive it, and any recent-player reports still batched must still land
            // even though the match never reached OnMatchCompleted.
            _context.Activity.Clear();
            await _context.Activity.FlushAsync();
        }
    }

    private async Task LeaveAndReturnToMainMenuAsync()
    {
        _leaveTask ??= LeavePartyAsync();
        await _leaveTask;
        ReturnToMainMenu();
    }

    private void OnServerDisconnected(PlatformResult result)
    {
        if (_matchFinished)
        {
            return;
        }

        _matchFinished = true;

        // XR-067: whoever was in the roster before the connection dropped must still be
        // flushed to Recently Played With - an early-leaver-by-disconnect is exactly the
        // case the requirement calls out.
        //
        // XR-064: the activity is cleared here too. It used to be skipped on the
        // reasoning that the party was already gone and a provider would expire an
        // activity behind a dead connection by itself. Nothing guarantees that, and the
        // requirement is explicit that a player who is no longer joinable must not be
        // advertised as joinable - a dropped connection is the clearest case of that
        // there is. The coordinator retries, so a delete issued while the network is
        // still down is not simply lost.
        _context.Activity.Clear();
        _ = _context.Activity.FlushAsync();

        // The reason is used when the caller supplied one - the connectivity path knows
        // whether the console is off the network entirely or merely cannot reach the
        // internet, and those need different things done about them. Party's own losses
        // arrive without useful text and keep the generic sentence.
        var message = result.Message is { Length: > 0 }
            ? result.Message
            : "You were disconnected from the match.";

        _context.Screens.ShowDialog(
            "Disconnected",
            message,
            DialogSeverity.Error,
            showCancel: false,
            onDismissed: _ => ReturnToMainMenu());
    }

    /// <summary>
    /// The console's connectivity changed under an online match (XR-074).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the proactive route out of a dead match.
    /// <c>IPartyService.NetworkDestroyed</c> remains the backstop, and either may win:
    /// whichever arrives first ends the session and the other is swallowed by the
    /// <see cref="_matchFinished"/> guard.
    /// </para>
    /// <para>
    /// <b>The loss has to persist.</b> The hint is device-wide and can flap - a brief
    /// interface change, a transient radio drop - over intervals a Party network rides
    /// out without losing a packet the player would notice. Ending a match the instant it
    /// goes low would be a regression against the reactive handling rather than an
    /// improvement on it.
    /// </para>
    /// </remarks>
    private void OnConnectivityChanged(bool online)
        => _offlineGrace = online ? -1f : ConnectivityGraceSeconds;

    private void TickConnectivityGrace(float delta)
    {
        if (_offlineGrace < 0f || _matchFinished)
        {
            return;
        }

        _offlineGrace -= delta;

        if (_offlineGrace > 0f)
        {
            return;
        }

        _offlineGrace = -1f;

        // Re-checked live rather than trusted. Cancelling the countdown catches a
        // reported recovery; this catches the case where the grace period simply
        // outlasted the problem without a notification arriving.
        var runtime = _context.Platform.Runtime;

        if (runtime.IsOnline)
        {
            return;
        }

        OnServerDisconnected(PlatformResult.Fail(PlatformStatus.Failed, runtime.OfflineReason));
    }

    /// <summary>
    /// Returns to the lobby on the session the match was played on, so the party
    /// survives to play another (XR-003).
    /// </summary>
    /// <remarks>
    /// <c>gameplay_screen.gd</c> returned to <c>ScreenManager.LOBBY</c> on an ordinary
    /// match completion and to <c>MAIN_MENU</c> only for a cancellation or a disconnect,
    /// and this now does the same. An offline practice match goes back to its own lobby
    /// too - the roster is reset either way, which is what stops a second match starting
    /// with the first one's scores and everyone already readied.
    /// </remarks>
    private void ReturnToLobby()
    {
        // Exit() disposes the network it was given, which is right for every other way
        // out of a match and exactly wrong for this one: the lobby about to be pushed is
        // built on this same session.
        _networkHandedOn = true;
        _context.Screens.ReplaceAll(new LobbyScreen(_network));
    }

    /// <summary>
    /// Where a match that did <i>not</i> finish sends the player: a cancellation or a
    /// disconnect has ended the session as well as the match, so there is no lobby left
    /// to return to.
    /// </summary>
    private void ReturnToMainMenu() => _context.Screens.ReplaceAll(new MainMenuScreen());

    private static string FormatStandings(MatchResult result)
    {
        var lines = result.Standings
            .Select(standing => $"{standing.Placement}. {standing.DisplayName} \u2014 {standing.Score}")
            .ToList();

        var message = lines.Count > 0 ? string.Join('\n', lines) : "The match has ended.";

        if (result.Reason == MatchEndReason.LastPlayerStanding)
        {
            message = $"Everyone else left the match.\n\n{message}";
        }
        else if (result.Reason == MatchEndReason.HostLeft)
        {
            message = $"The host left the match.\n\n{message}";
        }

        return message;
    }

    public override void DrawWorld(UiContext context, float renderScale, int viewportWidth, int viewportHeight)
    {
        // The world draw and the HUD draw are marked separately because they fail
        // differently: this one is the first time the match's own sprites reach the
        // graphics device, the other is the first time the match's text does.
        CrashLog.MarkOnce("gameplay-draw-world", "gameplay: first world draw");
        _match?.Draw(context.Batch, context.Pixel, renderScale, viewportWidth, viewportHeight);
        CrashLog.MarkOnce("gameplay-draw-world-done", "gameplay: first world draw complete");
    }

    public override void Draw(UiContext context)
    {
        if (_match is null || _director is null)
        {
            return;
        }

        CrashLog.MarkOnce("gameplay-draw-hud", "gameplay: first hud draw");

        DrawTopBar(context);
        DrawBottomBox(context);
        DrawCentreBanner(context);
        DrawControlsHint(context);

        _roster.Draw(context, new Point(40, 40), _network.PlayersByScore());
        _stt.Draw(context);
        _chatEntry.Draw(context);

        CrashLog.MarkOnce("gameplay-draw-hud-done", "gameplay: first hud draw complete");
    }

    /// <summary>
    /// A transient control reminder across the bottom of the screen at the start of a
    /// match.
    /// </summary>
    /// <remarks>
    /// Fires on the right stick, which is neither guessable nor stated anywhere else in
    /// the match; a first-time player could sit in a live game unable to shoot. Drawn
    /// through <see cref="ButtonPrompt"/> so the pad variant shows button icons rather
    /// than letters, and faded out over its last second so it leaves without a cut.
    /// </remarks>
    private void DrawControlsHint(UiContext context)
    {
        if (_controlsHint <= 0f)
        {
            return;
        }

        var alpha = Math.Clamp(_controlsHint, 0f, 1f);
        var color = UiTheme.Text * alpha;

        ButtonPrompt.DrawCentre(
            context.Batch,
            context.Assets,
            context.Theme.Small,
            context.Input.MatchControlsHint,
            new Vector2(context.Screen.Center.X, context.Screen.Bottom - 40),
            color);
    }

    private void DrawTopBar(UiContext context)
    {
        var score = _network.LocalPlayer?.Score ?? 0;
        var scoreBounds = new Rectangle(1480, 40, 400, 48);
        UiTheme.TextRight(
            context.Batch,
            context.Theme.Subtitle,
            $"Score: {score}",
            new Vector2(scoreBounds.Right, scoreBounds.Center.Y),
            UiTheme.Text);

        var timerBounds = new Rectangle(context.Screen.Center.X - 160, 40, 320, 60);
        UiTheme.TextCentre(
            context.Batch,
            context.Theme.Title,
            FormatTime(_director!.TimeRemaining),
            new Vector2(timerBounds.Center.X, timerBounds.Center.Y),
            UiTheme.Accent);
    }

    private void DrawBottomBox(UiContext context)
    {
        var ship = _match!.World.LocalShip is { IsActive: true } activeShip ? activeShip : null;

        var boxLeft = 40;
        var boxBottom = context.Screen.Bottom - 40;

        var weaponBounds = new Rectangle(boxLeft, boxBottom - 130, 420, 32);
        UiTheme.TextLeft(
            context.Batch,
            context.Theme.Body,
            $"Weapon: {(ship is null ? "\u2014" : WeaponLabel(ship))}",
            new Vector2(weaponBounds.X, weaponBounds.Center.Y),
            UiTheme.Text);

        DrawActiveBuffs(context, ship, boxLeft, boxBottom - 166);

        var healthBounds = new Rectangle(boxLeft, boxBottom - 90, 400, 26);
        DrawBar(
            context,
            healthBounds,
            ship?.Health ?? 0f,
            TuningLibrary.Ship.HealthMax,
            UiTheme.HealthFill);

        var shieldBounds = new Rectangle(boxLeft, boxBottom - 56, 400, 20);
        DrawBar(
            context,
            shieldBounds,
            ship?.Shield ?? 0f,

            // The overshield buff raises the ceiling, so the bar has to be drawn against
            // the ship's current maximum. Drawn against the constant it would simply peg
            // full and hide the entire benefit of the pickup.
            ship?.ShieldMaximum ?? TuningLibrary.Ship.ShieldMax,
            UiTheme.ShieldFill);
    }

    /// <summary>
    /// The weapon name, with its remaining ammunition when the weapon is not the
    /// unlimited default.
    /// </summary>
    /// <remarks>
    /// Ammunition is the balance lever for the whole arsenal - the exotics are a few
    /// seconds of burst before the ship drops back to the laser - so a player who cannot
    /// see the count cannot plan around the fallback that is about to happen.
    /// </remarks>
    private static string WeaponLabel(Ship ship)
    {
        var name = WeaponName(ship.PrimaryWeapon);
        return ship.WeaponAmmo < 0 ? name : $"{name}  x{ship.WeaponAmmo}";
    }

    /// <summary>
    /// Lists the buffs currently running, longest remaining first, with their countdowns.
    /// </summary>
    /// <remarks>
    /// Ordered by remaining time rather than by type so the list does not reshuffle as
    /// buffs come and go, and the one about to lapse is always at the end.
    /// </remarks>
    private static void DrawActiveBuffs(UiContext context, Ship? ship, int left, int top)
    {
        if (ship is null || ship.Buffs.Count == 0)
        {
            return;
        }

        var x = left;

        foreach (var (buff, remaining) in ship.Buffs.OrderByDescending(b => b.Value))
        {
            var text = $"{BuffName(buff)} {MathF.Ceiling(remaining):0}s";

            UiTheme.TextLeft(
                context.Batch,
                context.Theme.Small,
                text,
                new Vector2(x, top),
                UiTheme.Accent);

            x += (int)context.Theme.Small.MeasureString(text).X + 18;
        }
    }

    private static string BuffName(BuffType buff) => buff switch
    {
        BuffType.RapidFire => "RAPID",
        BuffType.Afterburner => "BURN",
        BuffType.Cloak => "CLOAK",
        BuffType.DoubleDamage => "DMG",
        BuffType.Overshield => "SHIELD",
        BuffType.Regeneration => "REGEN",
        BuffType.QuickCharge => "CHARGE",
        BuffType.MultiShot => "MULTI",
        BuffType.Ricochet => "RICO",
        BuffType.Vampiric => "VAMP",
        _ => buff.ToString().ToUpperInvariant(),
    };

    /// <summary>
    /// No interpolation here because there was none to port: the GDScript assigned
    /// <c>ProgressBar.value</c> straight from the ship's health and shield every frame,
    /// with no tween or lerp smoothing the jump on a hit.
    /// </summary>
    private static void DrawBar(UiContext context, Rectangle bounds, float value, float max, Color fill)
    {
        context.Theme.Box(context.Batch, bounds, UiTheme.ProgressBackground);

        var fraction = max > 0f ? Math.Clamp(value / max, 0f, 1f) : 0f;

        if (fraction <= 0f)
        {
            return;
        }

        var filled = bounds with { Width = (int)MathF.Round(bounds.Width * fraction) };
        context.Theme.Fill(context.Batch, filled, fill);
    }

    private void DrawCentreBanner(UiContext context)
    {
        var state = _director!.MatchState;

        // The centre banner is shown only up to the start of the match, then hidden for
        // its whole run and after it ends - _on_match_state_changed's own condition.
        if (state.HasMatchState(MatchState.Running) || state.HasMatchState(MatchState.MatchComplete))
        {
            return;
        }

        var centre = new Vector2(context.Screen.Center.X, context.Screen.Center.Y);

        if (state.HasMatchState(MatchState.Starting))
        {
            if (_countdownSeconds > 0)
            {
                // The GDScript bumped this label's font size to 180 for the countdown
                // numeral; SpriteFont has no runtime size, so the biggest baked font is
                // drawn scaled up instead to get back into the same visual neighbourhood.
                context.Batch.DrawString(
                    context.Theme.Title,
                    _countdownSeconds.ToString(),
                    centre,
                    UiTheme.Text,
                    0f,
                    context.Theme.Title.MeasureString(_countdownSeconds.ToString()) / 2f,
                    3.2f,
                    Microsoft.Xna.Framework.Graphics.SpriteEffects.None,
                    0f);
            }

            return;
        }

        // PLAYERS_JOINING / LOADING: the "waiting for players (n/total)" indicator.
        var total = _network.Players.Count;
        var loaded = _network.Players.Values.Count(player => player.InGame);

        if (loaded < total)
        {
            UiTheme.TextCentre(
                context.Batch,
                context.Theme.Subtitle,
                $"Waiting for players ({loaded}/{total})",
                centre,
                UiTheme.Text);
        }
    }

    private static string FormatTime(float seconds)
    {
        var total = (int)seconds;
        return $"{total / 60}:{total % 60:D2}";
    }

    /// <summary>
    /// The weapon's name, taken from its table row.
    /// </summary>
    /// <remarks>
    /// This was a four-case switch defaulting to "Laser", which with twenty weapons would
    /// have quietly labelled sixteen of them wrongly rather than failing. The table
    /// already carries a name for every row, so there is nothing for a switch here to add.
    /// </remarks>
    private static string WeaponName(WeaponType weapon) => WeaponLibrary.Get(weapon).DisplayName;
}
