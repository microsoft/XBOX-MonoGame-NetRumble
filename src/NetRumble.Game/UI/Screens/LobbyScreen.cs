using NetRumble.Platform.Networking;
using System.Globalization;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using NetRumble.Core;
using NetRumble.Core.Net;
using NetRumble.Core.Tuning;
using NetRumble.Game.UI.Elements;
using NetRumble.Game.UI.Widgets;
using NetRumble.Platform;
using NetRumble.Platform.Diagnostics;

namespace NetRumble.Game.UI.Screens;

/// <summary>How the lobby was entered, replacing the payload dictionary
/// <c>ScreenManager.replace_all(ScreenManager.LOBBY, {"option": ..., "code": ...})</c>
/// carried in the GDScript.</summary>
public enum LobbyIntent
{
    /// <summary>Create a new online session and advertise a join code.</summary>
    Host,

    /// <summary>Join an existing online session by join code.</summary>
    Join,

    /// <summary>Single-machine session with no transport at all.</summary>
    Practice,
}

/// <summary>
/// Ports <c>scripts/ui/screens/lobby_screen.gd</c> (+ <c>UI/Lobby/*</c>): ship and
/// colour pick, ready-up, the host's game-mode choice, the roster, and the way back
/// out to the main menu.
/// </summary>
/// <remarks>
/// <para>
/// <b>Structural divergence, forced by the constructor signature.</b> In the GDScript,
/// <c>NetManager</c> is a persistent autoload: whatever pushed the lobby (the main menu,
/// or <c>invite_router.gd</c>) had already awaited <c>host_match</c>/<c>join_match</c>
/// and the lobby only ever displayed an already-connected session. This port's
/// <see cref="LobbyScreen"/> takes only a <see cref="LobbyIntent"/> and there is no
/// other channel to hand it a live <see cref="IMatchNetwork"/>, so this screen stands
/// its own session up in <see cref="Enter"/> instead: sign-in, host-or-join, and (for
/// <see cref="LobbyIntent.Join"/>) collecting the code via <see cref="JoinCodeEntry"/>
/// all happen here, behind a short "Connecting…" state, before the roster is ever
/// shown. <see cref="LobbyIntent.Practice"/> needs none of this and stays synchronous,
/// exactly as <c>start_offline()</c> was.
/// </para>
/// <para>
/// <b>No footer button row.</b> Preserved deliberately from the original: ready-up is
/// the <see cref="MenuAction.ToggleReady"/> action reflected in your own roster ring,
/// leaving is Back, voice mute is <see cref="MenuAction.VoiceChat"/>, and the host's
/// match start happens automatically once everyone is ready. There is no "Ready"
/// button in <see cref="_list"/> to focus past.
/// </para>
/// <para>
/// <b>The roster never rebuilds.</b> Its eight <see cref="RosterRow"/> widgets are
/// created once in <see cref="Enter"/> and live inside <see cref="_list"/> for the rest
/// of the screen's life; a roster change only calls <see cref="RosterRow.SetState"/> and
/// friends on the existing objects; see <see cref="RefreshRoster"/>. This is stronger
/// than the usual "clear, rebuild, restore focus by index" pattern
/// <c>MenuScreen.Rebuild</c> documents - there is nothing to restore because nothing was
/// ever torn down - and it is not an optimisation invented for this port: the GDScript
/// says so of itself ("Refreshing only re-binds them, so nothing is created or freed
/// when someone joins, leaves or toggles ready"). <see cref="Rebuild"/> (the
/// clear-and-restore-focus form) exists for completeness and runs once, from
/// <see cref="Enter"/>.
/// </para>
/// </remarks>
public sealed class LobbyScreen : Screen
{
    // The match holds four players; the roster draws one slot each. Was 16 while Big Team
    // Battle existed, and 8 before that.
    private const int RosterSlotMax = 4;

    private readonly LobbyIntent _intent;
    private readonly string? _inviteConnectionString;
    private readonly MenuList _list = new();
    private readonly RosterRow[] _rosterRows = new RosterRow[RosterSlotMax];

    private UiContext? _context;
    private IMatchNetwork? _network;

    /// <summary>
    /// True when the session was handed to this screen already connected, by the
    /// between-matches constructor, rather than stood up in <see cref="Enter"/>.
    /// </summary>
    private readonly bool _adopted;

    private int _selectedStyleId;
    private int _selectedColorId;

    // The ship and colour rows are horizontal tab strips in the source, not vertical
    // spinner rows: the .tscn lays them out as fixed-position <c>Control</c> nodes with
    // four <c>ShipTabs</c> at the top of the ship panel and eight <c>ColorTabs</c> under
    // it. Focus is a zone plus an index within the current strip; the roster stays in
    // <see cref="_list"/> unchanged.
    private FocusZone _focusZone = FocusZone.ShipTabs;
    private int _shipTabFocus;
    private int _colorTabFocus;

    private bool _ready;
    private bool _transitioning;
    private bool _connectFailed;
    private string _connectingText = "Connecting\u2026";
    private string _countdownText = string.Empty;

    /// <summary>
    /// How long a host or join attempt may run before it is abandoned and reported.
    /// </summary>
    /// <remarks>
    /// <see cref="IPartyService"/> permits a backend to wait forever, and the GameCore
    /// one does: a join code whose lobby row outlived its host resolves happily and then
    /// blocks on a Party handshake with nobody to answer it, leaving the player on the
    /// connecting screen with no timeout and no error. Cancelling here rather than in
    /// one backend gives every provider the same guarantee - the LAN transport already
    /// enforces a shorter deadline of its own, which simply wins.
    /// </remarks>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often the communications privilege and the platform block lists are re-read
    /// while a session is live (XR-015).
    /// </summary>
    /// <remarks>
    /// The requirement offers "a regular cadence (such as hourly)" as its example, but
    /// that example is drawn from global-chat and very-large-scale scenarios where the
    /// per-peer call volume is the constraint. A four-player lobby issues at most three
    /// permission pairs per sweep, so it can re-check often enough that a revoked
    /// privilege or a fresh block takes effect within the same match rather than the same
    /// hour.
    /// </remarks>
    private const float CommunicationsRecheckSeconds = 120f;

    /// <summary>
    /// <see cref="UiContext.Time"/> at which the next communications sweep is due.
    /// </summary>
    private float _nextCommunicationsRecheck = float.MaxValue;

    public LobbyScreen(LobbyIntent intent) => _intent = intent;

    /// <summary>
    /// Enters with <see cref="LobbyIntent.Join"/> already resolved to a connection
    /// string carried by a platform invite, so <see cref="ConnectAsync"/> can skip
    /// <see cref="JoinCodeEntry"/> entirely - mirrors <c>invite_router.gd</c> calling
    /// <c>NetManager.join_match</c> directly with the invite's connection string rather
    /// than routing back through the join-code menu the player never saw.
    /// </summary>
    public LobbyScreen(LobbyIntent intent, string inviteConnectionString)
    {
        _intent = intent;
        _inviteConnectionString = inviteConnectionString;
    }

    /// <summary>
    /// Re-enters the lobby on a session that is already connected, after a match played
    /// on it has finished (XR-003).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the case the type remarks above say does not exist - and it did not, when
    /// every finished match tore the party down and returned to the main menu. Keeping
    /// the party alive between matches is the behaviour the original had
    /// (<c>gameplay_screen.gd</c> returned to <c>ScreenManager.LOBBY</c> on an ordinary
    /// completion), and it is what stops four players having to re-invite each other to
    /// play a second round.
    /// </para>
    /// <para>
    /// <see cref="Enter"/> skips the whole connect path for a screen built this way:
    /// there is nothing to sign in to, nothing to host or join, and no privilege to
    /// re-check that the session it has just been handed did not already pass.
    /// </para>
    /// </remarks>
    public LobbyScreen(IMatchNetwork network)
    {
        ArgumentNullException.ThrowIfNull(network);

        _network = network;
        _adopted = true;
        _intent = network.IsOffline
            ? LobbyIntent.Practice
            : network.IsHost ? LobbyIntent.Host : LobbyIntent.Join;
    }

    /// <summary>Whether this screen currently holds a live, non-offline session -
    /// lets <see cref="Game.InviteRouter"/> ask "are we already mid-match?" without a
    /// second <see cref="PartyMatchNetwork"/> field the way the pre-Phase-6 code did.</summary>
    public bool IsOnline => _network is { IsOffline: false };

    public override void Enter(UiContext context)
    {
        _context = context;

        // The roster is one of several focus zones, and the mouse can put focus on a row
        // without ever walking through the strips above it - so a pointer focus has to
        // move the zone too, or the row would light up while Accept still went to a tab.
        _list.PointerFocused += OnRosterPointerFocused;

        for (var i = 0; i < RosterSlotMax; i++)
        {
            var row = new RosterRow();
            row.InviteRequested += OnInviteRequested;
            row.ActionsRequested += OnActionsRequested;
            _rosterRows[i] = row;
        }

        if (_adopted)
        {
            AdoptSession(context);
            return;
        }

        if (_intent == LobbyIntent.Practice)
        {
            StartOffline(context);
            return;
        }

        _ = ConnectAsync(context);
    }

    /// <summary>
    /// Re-enters a session that is already connected, after a match on it finished
    /// (XR-003).
    /// </summary>
    private void AdoptSession(UiContext context)
    {
        // Everything ConnectAsync exists to establish is already true. What is not yet
        // true is that the roster is clear of the finished match, which is the host's
        // call to make and is published to every client from there.
        if (_network!.IsHost)
        {
            _network.ResetForNextMatch();
            _network.SetMatchState(MatchState.Waiting);
        }
        else
        {
            _network.ResetForNextMatch();
        }

        // Re-advertised because the activity was cleared when the match ended: the
        // session is joinable again, and a friend looking at this player's profile must
        // be offered Join for the lobby they are actually sitting in.
        if (!_network.IsOffline)
        {
            PublishActivity(context);
        }

        FinishConnecting(context);
    }

    public override void Exit()
    {
        _list.PointerFocused -= OnRosterPointerFocused;

        if (_network is not null)
        {
            _network.RosterChanged -= OnRosterChanged;
            _network.ConnectionLost -= OnConnectionLost;
            _network.MatchStateChanged -= OnMatchStateChanged;
            _network.CountdownChanged -= OnCountdownChanged;
        }
    }

    /// <summary>
    /// Refreshes the roster and its dependent status text whenever this screen becomes
    /// the top again - reproduces <c>_on_player_actions_closed</c>'s
    /// <c>_refresh_roster()</c>, generalised to every overlay this screen can push
    /// (player actions, the join-code entry) rather than being wired to just one.
    /// </summary>
    public override void OnRevealed()
    {
        if (_context is not { } context)
        {
            return;
        }

        if (_ready)
        {
            RefreshRoster(context);
            RefreshStatus(context);
        }
    }

    public override void Update(UiContext context)
    {
        if (!_ready || _transitioning)
        {
            return;
        }

        // XR-015 requires the communications privilege and the platform block lists to be
        // respected for the life of a session, not just at the moment it was created: a
        // privilege can be revoked, and a player can be blocked, while a match is running.
        // The requirement names an hourly cadence as an example for large-scale scenarios;
        // a four-player lobby can afford to be far more responsive than that.
        if (_network is { IsOffline: false } && context.Time >= _nextCommunicationsRecheck)
        {
            _nextCommunicationsRecheck = context.Time + CommunicationsRecheckSeconds;
            _ = RecheckCommunicationsAsync(context);
        }

        // Before the roster list, so a click on a tab strip is not first offered to a
        // roster row that happens to sit under the same cursor travel.
        UpdatePointer(context);

        _list.Bounds = RosterListBounds;
        _list.Update(context);
    }

    /// <summary>
    /// The periodic half of the XR-015 duty: re-reads the communications privilege and the
    /// mute/avoid lists, then re-evaluates every peer against them.
    /// </summary>
    private async Task RecheckCommunicationsAsync(UiContext context)
    {
        try
        {
            await ApplyCommunicationsPrivilegeAsync(context);
            await context.Platform.Privacy.RefreshListsAsync();
            await EvaluatePrivacyAsync(context);
        }
        catch (Exception ex)
        {
            // A failed re-check leaves the previous, more restrictive verdicts in place.
            // It must not take the lobby down with it.
            CrashLog.MarkOnce("comms-recheck", $"lobby: communications re-check failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Mouse support for the parts of this screen that are drawn rather than built from
    /// <see cref="Widget"/>s: the ship and colour tab strips and the offline opponent
    /// stepper. Each was a real <c>Control</c> in the .tscn and so was clickable there;
    /// hit-testing the same rectangles the draw pass uses restores that.
    /// </summary>
    private void UpdatePointer(UiContext context)
    {
        var input = context.Input;

        if (!input.PointerMoved && !input.PointerPressed)
        {
            return;
        }

        var pointer = input.Pointer;
        var clicked = input.PointerPressed;

        for (var index = 0; index < 4; index++)
        {
            var tab = new Rectangle(ShipTabX(index), ShipTabsRect.Y, ShipTabWidth, ShipTabHeight);

            if (!tab.Contains(pointer))
            {
                continue;
            }

            FocusShipTab(context, index);

            if (clicked)
            {
                input.ConsumePointer();
                context.Audio.Play("MenuSelect");
                OnShipStyleChanged(context, index);
            }

            return;
        }

        for (var index = 0; index < TuningLibrary.Palette.Size; index++)
        {
            var tab = new Rectangle(ColorTabX(index), ColorTabsRect.Y, ColorTabWidth, ColorTabHeight);

            if (!tab.Contains(pointer))
            {
                continue;
            }

            FocusColorTab(context, index);

            if (clicked && !ColorsTakenByOthers().Contains(index))
            {
                input.ConsumePointer();
                context.Audio.Play("MenuSelect");
                OnColorChanged(context, index);
            }

            return;
        }

        if (_network is not OfflineMatchNetwork)
        {
            return;
        }

        if (OpponentsMinusRect.Contains(pointer) || OpponentsPlusRect.Contains(pointer))
        {
            EnterZone(FocusZone.Opponents, context);

            if (clicked)
            {
                input.ConsumePointer();
                StepOpponents(context, OpponentsMinusRect.Contains(pointer) ? -1 : 1);
            }
        }
    }

    /// <summary>Moves focus to a ship tab from the pointer, entering the strip if needed.</summary>
    private void FocusShipTab(UiContext context, int index)
    {
        var moved = _focusZone != FocusZone.ShipTabs || _shipTabFocus != index;

        EnterZone(FocusZone.ShipTabs, context, silent: true);
        _shipTabFocus = index;

        if (moved)
        {
            context.Audio.Play("MenuScroll");
        }
    }

    private void FocusColorTab(UiContext context, int index)
    {
        var moved = _focusZone != FocusZone.ColorTabs || _colorTabFocus != index;

        EnterZone(FocusZone.ColorTabs, context, silent: true);
        _colorTabFocus = index;

        if (moved)
        {
            context.Audio.Play("MenuScroll");
        }
    }

    /// <summary>
    /// The roster list focused a row from the pointer; make the roster the active zone so
    /// the focus ring it just drew is the one Accept, and the strips' navigation, agree
    /// with.
    /// </summary>
    private void OnRosterPointerFocused(int index)
    {
        _ = index;

        if (_context is { } context)
        {
            EnterZone(FocusZone.Roster, context, silent: true);
        }
    }

    public override bool HandleAction(UiContext context, MenuAction action)
    {
        if (!_ready || _transitioning)
        {
            // While connecting, only Back (below) is meaningful; there is no list yet
            // and no ready state to toggle.
            return base.HandleAction(context, action);
        }

        if (action == MenuAction.ToggleReady)
        {
            ToggleReady(context);
            return true;
        }

        if (action == MenuAction.VoiceChat)
        {
            OnVoiceChatToggled(context);
            return true;
        }

        if (action is MenuAction.PagePrevious or MenuAction.PageNext)
        {
            HandleBumperFocus(context, action);
            return true;
        }

        if (HandleZoneAction(context, action))
        {
            return true;
        }

        return base.HandleAction(context, action);
    }

    /// <summary>
    /// Routes a menu action through the three focus zones: the ship-tab strip, the
    /// colour-tab strip and the roster list. Reproduces the .gd's neighbour-focus
    /// walk with the tab strips as horizontal rings and the roster as its own
    /// vertical list, connected at the top and bottom of the roster.
    /// </summary>
    private bool HandleZoneAction(UiContext context, MenuAction action)
    {
        switch (_focusZone)
        {
            case FocusZone.ShipTabs:
                return HandleShipTabAction(context, action);
            case FocusZone.ColorTabs:
                return HandleColorTabAction(context, action);
            case FocusZone.Opponents:
                return HandleOpponentsAction(context, action);
            case FocusZone.Roster:
                return HandleRosterAction(context, action);
            default:
                return false;
        }
    }

    private bool HandleShipTabAction(UiContext context, MenuAction action)
    {
        switch (action)
        {
            case MenuAction.Left:
                _shipTabFocus = (_shipTabFocus + 3) % 4;
                context.Audio.Play("MenuScroll");
                return true;
            case MenuAction.Right:
                _shipTabFocus = (_shipTabFocus + 1) % 4;
                context.Audio.Play("MenuScroll");
                return true;
            case MenuAction.Down:
                EnterZone(FocusZone.ColorTabs, context);
                return true;
            case MenuAction.Up:
                EnterZone(FocusZone.Roster, context);
                return true;
            case MenuAction.Accept:
                context.Audio.Play("MenuSelect");
                OnShipStyleChanged(context, _shipTabFocus);
                return true;
            default:
                return false;
        }
    }

    private bool HandleColorTabAction(UiContext context, MenuAction action)
    {
        var count = TuningLibrary.Palette.Size;

        switch (action)
        {
            case MenuAction.Left:
                _colorTabFocus = (_colorTabFocus + count - 1) % count;
                context.Audio.Play("MenuScroll");
                return true;
            case MenuAction.Right:
                _colorTabFocus = (_colorTabFocus + 1) % count;
                context.Audio.Play("MenuScroll");
                return true;
            case MenuAction.Up:
                EnterZone(FocusZone.ShipTabs, context);
                return true;
            case MenuAction.Down:
                EnterZone(ZoneBelowColorTabs, context);
                return true;
            case MenuAction.Accept:
                if (ColorsTakenByOthers().Contains(_colorTabFocus))
                {
                    // Refuses the pick exactly as `_on_color_selected`'s taken-guard did;
                    // no audio because the intent-to-refuse should not sound like success.
                    return true;
                }

                context.Audio.Play("MenuSelect");
                OnColorChanged(context, _colorTabFocus);
                return true;
            default:
                return false;
        }
    }

    private bool HandleRosterAction(UiContext context, MenuAction action)
    {
        // Roster wraps within itself in MenuList; the tab strips above are reached by
        // walking off the top of the list rather than by MenuList's own wrap-around.
        if (action == MenuAction.Up && _list.FocusIndex <= 0)
        {
            EnterZone(ZoneBelowColorTabs, context);
            return true;
        }

        return _list.HandleAction(context, action);
    }

    /// <summary>
    /// The opponent stepper only exists offline, so the zone between the colour strip
    /// and the roster collapses away in a networked lobby.
    /// </summary>
    private FocusZone ZoneBelowColorTabs
        => _network is OfflineMatchNetwork ? FocusZone.Opponents : FocusZone.Roster;

    /// <summary>
    /// LB enters customization, toggling between ship and colour when pressed again.
    /// RB always enters the roster, providing predictable one-button zone navigation.
    /// </summary>
    private void HandleBumperFocus(UiContext context, MenuAction action)
    {
        var next = action == MenuAction.PageNext
            ? FocusZone.Roster
            : _focusZone == FocusZone.ShipTabs
                ? FocusZone.ColorTabs
                : FocusZone.ShipTabs;

        EnterZone(next, context);
    }

    /// <summary>
    /// Left/Right step the bot count; the two glyph buttons in the .tscn collapse into
    /// one focusable zone here because a stepper with a single value reads the same way
    /// either side of the change.
    /// </summary>
    private bool HandleOpponentsAction(UiContext context, MenuAction action)
    {
        switch (action)
        {
            case MenuAction.Left:
                return StepOpponents(context, -1);
            case MenuAction.Right:
            case MenuAction.Accept:
                return StepOpponents(context, 1);
            case MenuAction.Up:
                EnterZone(FocusZone.ColorTabs, context);
                return true;
            case MenuAction.Down:
                EnterZone(FocusZone.Roster, context);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Transitions between the three focus zones, keeping the roster's own focus flag
    /// in sync so a row does not keep drawing the focus ring while the player is in
    /// one of the tab strips.
    /// </summary>
    /// <param name="silent">
    /// Suppresses the scroll cue for a zone change the pointer has already sounded - the
    /// roster list plays it as the row takes focus, and two cues for one hover reads as a
    /// stutter.
    /// </param>
    private void EnterZone(FocusZone zone, UiContext context, bool silent = false)
    {
        if (_focusZone == zone)
        {
            return;
        }

        if (_focusZone == FocusZone.Roster && _list.Focused is { } previous)
        {
            previous.HasFocus = false;
        }

        _focusZone = zone;

        if (!silent)
        {
            context.Audio.Play("MenuScroll");
        }

        if (zone != FocusZone.Roster)
        {
            return;
        }

        // Re-highlight the row that was focused before; MenuList's own SetFocus early-returns
        // when the index has not changed, so the HasFocus flag has to be set directly.
        if (_list.Focused is { } row)
        {
            row.HasFocus = true;
        }
        else
        {
            _list.FocusFirst();
        }
    }

    /// <summary>The three focus zones the lobby routes menu actions through.</summary>
    private enum FocusZone
    {
        ShipTabs,
        ColorTabs,
        Opponents,
        Roster,
    }

    /// <summary>
    /// Reproduces <c>on_back_pressed</c>'s confirm-then-leave. The dialog's callback
    /// runs after it has already popped itself (see <see cref="ScreenManager.ShowDialog"/>),
    /// which is exactly why a callback that ends in <see cref="ScreenManager.ReplaceAll"/>
    /// is safe here rather than being undone by the dialog's own teardown.
    /// </summary>
    public override void OnBackPressed(UiContext context)
    {
        if (_transitioning)
        {
            return;
        }

        context.Screens.ShowDialog(
            "Leave Match",
            "Leave the current match?",
            DialogSeverity.Warning,
            showCancel: true,
            onDismissed: confirmed =>
            {
                if (!confirmed)
                {
                    return;
                }

                _transitioning = true;
                _ = LeaveAndReturnToMenuAsync(context);
            });
    }

    public override void Draw(UiContext context)
    {
        DrawBackground(context);

        if (!_ready)
        {
            UiTheme.TextCentre(
                context.Batch,
                context.Theme.Subtitle,
                _connectingText,
                new Vector2(context.Screen.Center.X, context.Screen.Center.Y),
                _connectFailed ? UiTheme.TextDisabled : UiTheme.Text);
            return;
        }

        DrawStateLabel(context);
        DrawShipSection(context);
        DrawColorSection(context);
        DrawGameTypeSection(context);
        DrawRulesSection(context);
        DrawJoinCodeOrOpponentsSection(context);
        DrawRosterSection(context);
        DrawHint(context);
    }

    // --- Session bootstrap ---------------------------------------------------

    private void StartOffline(UiContext context)
    {
        var local = BuildLocalPlayerState(context);
        var offline = new OfflineMatchNetwork(local);
        _network = offline;

        // The roster has to know the saved opponent count before anything reads it.
        offline.SyncPracticeBots(context.Profile.PracticeOpponents);

        FinishConnecting(context);
    }

    /// <summary>
    /// Adds or removes one AI opponent. <paramref name="direction"/> is -1 or 1.
    /// Practice only: there is nothing to step in a session other people are in.
    /// </summary>
    private bool StepOpponents(UiContext context, int direction)
    {
        if (_network is not OfflineMatchNetwork offline)
        {
            return false;
        }

        var count = Math.Clamp(
            context.Profile.PracticeOpponents + direction, 0, NRConst.MaxPracticeBots);

        if (count == context.Profile.PracticeOpponents)
        {
            // The steppers clamp rather than wrap, so an end of the range is a no-op.
            // No audio: refusing should not sound like success.
            return true;
        }

        context.Profile.PracticeOpponents = count;
        context.Profile.MarkDirty();
        offline.SyncPracticeBots(count);
        context.Audio.Play("MenuSelect");
        RefreshRoster(context);
        return true;
    }

    /// <summary>
    /// Host or join an online session. Mirrors <c>NetManager.host_match</c> /
    /// <c>join_match</c>: resolve a signed-in user first (multiplayer has no
    /// guest path), then stand up the Party network.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every online entry point passes through here, which is why the gates live
    /// here.</b> <c>MainMenuScreen</c> runs its own connectivity and privilege checks
    /// before pushing this screen, but a shell invite pushes it directly and bypasses
    /// them entirely - the player would get a raw connection failure instead of the
    /// offline explanation (XR-074). The checks below are the ones that actually cover
    /// every route.
    /// </para>
    /// <para>
    /// <b>The communications privilege is awaited before the network comes up, not
    /// alongside it (XR-015).</b> It used to be dispatched fire-and-forget from
    /// <see cref="FinishConnecting"/>, after the chat control already existed, so the
    /// microphone was live for the duration of the round trip.
    /// </para>
    /// </remarks>
    private async Task ConnectAsync(UiContext context)
    {
        var runtime = context.Platform.Runtime;

        if (!runtime.IsOnline)
        {
            FailConnection(
                context,
                runtime.OfflineReason is { Length: > 0 } reason
                    ? reason
                    : "Online play needs a network connection.");
            return;
        }

        var identity = context.Platform.Identity;
        var user = identity.CurrentUser;

        if (user is null)
        {
            var signIn = await identity.SignInAsync(context.InteractiveSignIn);

            if (!signIn.Succeeded)
            {
                FailConnection(
                    context,
                    ClassifyConnectionFailure(signIn.Result, "sign-in", "Could not sign in."));
                return;
            }

            user = signIn.Value;
        }

        // XR-045: MainMenuScreen's own check ran before this screen was even pushed and
        // may be stale by the time the session is actually created - a privilege can be
        // revoked, or resolved, in between. useCache:false forces a live query at the
        // one point that matters: immediately before HostAsync/JoinAsync actually stands
        // up the session.
        var verdict = await context.Platform.Privileges.CheckAsync(GamePrivilege.Multiplayer, useCache: false);

        if (!verdict.Allowed)
        {
            FailConnection(context, verdict.Message);
            return;
        }

        // XR-015: resolved before anything can transmit. A denial is not a connection
        // failure - the player may still play, just without chat - so this sets the
        // policy and continues rather than returning.
        await ApplyCommunicationsPrivilegeAsync(context);

        if (_intent == LobbyIntent.Join)
        {
            if (_inviteConnectionString is { } connectionString)
            {
                _ = JoinByConnectionStringAsync(context, connectionString);
                return;
            }

            // The join code has no other source in this port - see the type remarks -
            // so it is collected here, before the network exists, exactly as the main
            // menu collected it before the GDScript's NetManager.join_match ever ran.
            context.Screens.Push(new JoinCodeEntry("Enter Join Code", code => _ = JoinAsync(context, code)));
            return;
        }

        await HostAsync(context);
    }

    private async Task HostAsync(UiContext context)
    {
        var party = context.Platform.Party;
        var mode = TuningLibrary.GameMode;

        using var timeout = new CancellationTokenSource(ConnectTimeout);
        PlatformResult<string> result;

        try
        {
            result = await party.HostAsync(
                mode.PlayerCount, mode.DisplayName, NRProtocol.VersionString(), timeout.Token);
        }
        catch (OperationCanceledException)
        {
            result = PlatformResult<string>.Canceled();
        }

        if (_transitioning)
        {
            return;
        }

        if (!result.Succeeded)
        {
            await AbandonConnectionAsync(context);
            FailConnection(
                context,
                TimedOutMessage(result, timeout, "The match could not be created in time.")
                    ?? ClassifyConnectionFailure(result.Result, "host", "Could not host the match."));
            return;
        }

        BindPartyNetwork(context);

        // The host owns the state machine; there is no PLAYERS_JOINING handshake to
        // wait for the way a client has, so this screen sets it directly, matching
        // NetManager.host_match's `_set_match_state(PLAYERS_JOINING)`.
        _network!.SetMatchState(MatchState.PlayersJoining);

        PublishActivity(context);
        FinishConnecting(context);
    }

    private async Task JoinAsync(UiContext context, string code)
    {
        _connectingText = "Joining\u2026";
        var party = context.Platform.Party;

        using var timeout = new CancellationTokenSource(ConnectTimeout);
        PlatformResult result;

        try
        {
            result = await party.JoinAsync(code, NRProtocol.VersionString(), timeout.Token);
        }
        catch (OperationCanceledException)
        {
            result = PlatformResult.Canceled();
        }

        if (_transitioning)
        {
            return;
        }

        if (!result.Succeeded)
        {
            await AbandonConnectionAsync(context);
            FailConnection(
                context,
                TimedOutMessage(result, timeout, StaleJoinCodeMessage)
                    ?? ClassifyConnectionFailure(result, "join", "Could not join the match."));
            return;
        }

        BindPartyNetwork(context);

        // #23: every member of the session publishes the activity, not just the host.
        // A friend looking at a joined player's Xbox profile must be offered Join for
        // the session that player is actually in - publishing only from the host left
        // everyone else advertising nothing, so the profile fell back to Play.
        PublishActivity(context);

        // A client waits for the host's own MatchState broadcast rather than setting
        // one itself - matching NetManager._join's `_set_match_state(LOADING)` and the
        // fact that only the host ever calls set_match_state again after this.
        FinishConnecting(context);
    }

    /// <summary>
    /// Turns a connection failure into wording a player can act on (XR-003, XR-022).
    /// </summary>
    /// <remarks>
    /// The provider's own <see cref="PlatformResult.Message"/> is written for a log: it
    /// names Party, PlayFab, entity ids and HRESULTs, none of which mean anything to a
    /// player and all of which are developer terminology the shell requirements
    /// disallow on a player-facing surface. The status is the part of the result that
    /// actually carries a decision, so it is what the wording is derived from, and the
    /// message is left to <see cref="CrashLog"/> where it is useful.
    /// </remarks>
    /// <param name="fallback">
    /// What to say for a failure the status does not explain - the verb differs between
    /// hosting and joining, and only the caller knows which it was doing.
    /// </param>
    private static string ClassifyConnectionFailure(
        PlatformResult result, string operation, string fallback)
    {
        if (result.Message is { Length: > 0 } detail)
        {
            CrashLog.MarkOnce($"connect-failed-{operation}", $"lobby: {operation} failed - {detail}");
        }

        return result.Status switch
        {
            PlatformStatus.NetworkFailure => "The connection was lost. Check your network and try again.",
            PlatformStatus.TimedOut => "The match did not respond in time.",
            PlatformStatus.NotSignedIn => "Online play needs you to be signed in to Xbox.",
            PlatformStatus.NoPrivilege => "This account is not allowed to play online multiplayer.",
            PlatformStatus.Unavailable => "Online play is unavailable right now.",
            _ => fallback,
        };
    }

    /// <summary>
    /// What a player is told when a join code resolved but nobody answered. The most
    /// common cause by far is a lobby whose host has left: the row can outlive the
    /// session that owned it, so the code still looks valid right up to the handshake.
    /// </summary>
    private const string StaleJoinCodeMessage =
        "That match is no longer accepting players. The host may have left.";

    /// <summary>
    /// Turns a cancellation this screen's own deadline caused into a player-facing
    /// timeout message, or null when the failure was something the provider explained.
    /// </summary>
    private static string? TimedOutMessage(
        PlatformResult result, CancellationTokenSource timeout, string message)
        => timeout.IsCancellationRequested
            && result.Status is PlatformStatus.Canceled or PlatformStatus.TimedOut
                ? message
                : null;

    private static string? TimedOutMessage(
        PlatformResult<string> result, CancellationTokenSource timeout, string message)
        => TimedOutMessage(result.Result, timeout, message);

    /// <summary>
    /// Unwinds whatever a failed host or join attempt left behind before the player is
    /// sent back to the menu.
    /// </summary>
    /// <remarks>
    /// An attempt that is abandoned part-way - a timeout above all - can leave lobby
    /// membership or a half-built Party network behind, and nothing else clears it:
    /// <see cref="LeaveAndReturnToMenuAsync"/> only acts when a bound
    /// <see cref="IMatchNetwork"/> exists, and a failed connection never produced one.
    /// Without this the next host attempt is refused with "A session is already
    /// running."
    /// </remarks>
    private static async Task AbandonConnectionAsync(UiContext context)
    {
        try
        {
            await context.Platform.Party.LeaveAsync();
        }
        catch (Exception)
        {
            // Best-effort teardown, exactly like every other leave path in this screen:
            // a provider that throws on the way out must not replace the failure the
            // player is about to be shown with a different one.
        }
    }

    /// <summary>Same handshake as <see cref="JoinAsync(UiContext,string)"/> but through
    /// the connection string an invite carries instead of a typed join code - the two
    /// differ only in which <see cref="IPartyService"/> entry point resolves the
    /// session, exactly as <c>NetManager.join_match</c> accepts either in the GDScript.</summary>
    private async Task JoinByConnectionStringAsync(UiContext context, string connectionString)
    {
        _connectingText = "Joining\u2026";
        var party = context.Platform.Party;

        using var timeout = new CancellationTokenSource(ConnectTimeout);
        PlatformResult result;

        try
        {
            result = await party.JoinByConnectionStringAsync(
                connectionString, NRProtocol.VersionString(), timeout.Token);
        }
        catch (OperationCanceledException)
        {
            result = PlatformResult.Canceled();
        }

        if (_transitioning)
        {
            return;
        }

        if (!result.Succeeded)
        {
            await AbandonConnectionAsync(context);
            FailConnection(
                context,
                TimedOutMessage(result, timeout, StaleJoinCodeMessage)
                    ?? ClassifyConnectionFailure(result, "join", "Could not join the match."));
            return;
        }

        BindPartyNetwork(context);

        // XR-064/#23: a player who joined by invite is in the same joinable session the
        // host advertised, so their own activity has to say so too - otherwise a friend
        // looking at *their* profile is offered Play rather than Join, and the session
        // is only reachable through the one player who created it.
        PublishActivity(context);
        FinishConnecting(context);
    }

    private void BindPartyNetwork(UiContext context)
    {
        var local = BuildLocalPlayerState(context);
        var party = new PartyMatchNetwork(context.Platform.Party, local);
        _network = party;
    }

    private PlayerState BuildLocalPlayerState(UiContext context)
    {
        var user = context.Platform.Identity.CurrentUser;

        return new PlayerState
        {
            DisplayName = user?.DisplayName ?? context.Profile.DisplayName,
            XboxUserId = user?.XboxUserId ?? string.Empty,
            ShipStyleId = context.Profile.ShipStyleId,
            ShipColorId = context.Profile.ShipColorId,
        };
    }

    private void PublishActivity(UiContext context)
    {
        if (_network is null || _network.IsOffline || !context.Platform.Capabilities.HasFlag(PlatformCapabilities.Invites))
        {
            return;
        }

        var party = context.Platform.Party;

        // XR-064: currentPlayers must track the live roster, not the value at host
        // time, or the shell keeps advertising a session as having one player long
        // after it has filled. Declared rather than issued: ActivityCoordinator
        // serialises this against every other activity mutation, so a set raised by a
        // roster change can no longer land after the delete that ended the session.
        context.Activity.Advertise(new ActivityCoordinator.ActivityState(
            party.ConnectionString,
            TuningLibrary.GameMode.PlayerCount,
            _network.Players.Count,
            ActivityCoordinator.GroupIdFor(party.ConnectionString)));
    }

    /// <summary>
    /// Reports every peer currently in the roster as encountered this session (XR-067).
    /// Called on every roster change, not just at match end, so a player who leaves
    /// before the match finishes is still reported: <see cref="IActivityService.ReportRecentPlayersAsync"/>
    /// de-duplicates, so calling this repeatedly is safe and cheap.
    /// </summary>
    private void ReportRecentPlayers(UiContext context)
    {
        if (_network is null || _network.IsOffline)
        {
            return;
        }

        var xuids = _network.Players.Values
            .Where(p => p.PeerId != _network.LocalPeerId && !string.IsNullOrEmpty(p.XboxUserId))
            .Select(p => p.XboxUserId)
            .ToArray();

        if (xuids.Length == 0)
        {
            return;
        }

        context.Activity.ReportRecentPlayers(xuids);
    }

    /// <summary>
    /// Checks the account's communications privilege and sets
    /// <see cref="IPartyService.ChatAllowed"/> from the verdict (XR-015, XR-045).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>useCache:false</c>, and awaited by <see cref="ConnectAsync"/> before the party
    /// network is stood up. Both matter: a cached grant from earlier in the session is
    /// exactly the stale answer XR-045's privilege-lifetime clause is about, and a
    /// verdict that lands after the chat control exists is a verdict that arrives after
    /// the microphone is already open.
    /// </para>
    /// <para>
    /// Also called on the cadence <see cref="Update"/> drives, because XR-015 requires
    /// the privilege and the block lists to be respected for the life of the session, not
    /// merely at the moment it was created.
    /// </para>
    /// </remarks>
    private static async Task ApplyCommunicationsPrivilegeAsync(UiContext context)
    {
        var verdict = await context.Platform.Privileges
            .CheckAsync(GamePrivilege.Communications, useCache: false);

        context.Platform.Party.ChatAllowed = verdict.Allowed;
    }

    /// <summary>
    /// Evaluates communication privacy for every peer currently in the roster and turns
    /// each verdict into a Party restriction (XR-015). Called on every roster change so
    /// a peer who joins after the initial evaluation is still covered, and so the
    /// "Restricted" indicator <see cref="RosterRow"/> shows is backed by a real verdict
    /// instead of the permissive default <see cref="IPrivacyService.GetCached"/> returns
    /// before anything has ever evaluated it.
    /// </summary>
    private async Task EvaluatePrivacyAsync(UiContext context)
    {
        if (_network is null || _network.IsOffline)
        {
            return;
        }

        var xuids = _network.Players.Values
            .Where(p => p.PeerId != _network.LocalPeerId && !string.IsNullOrEmpty(p.XboxUserId))
            .Select(p => p.XboxUserId)
            .Distinct()
            .ToArray();

        if (xuids.Length == 0)
        {
            return;
        }

        PlatformResult<IReadOnlyDictionary<string, PrivacyVerdict>> result;

        try
        {
            result = await context.Platform.Privacy.EvaluateAsync(xuids);
        }
        catch (Exception)
        {
            // Best-effort, like every other privacy/privilege call in this port: a
            // provider that throws must not take the lobby down over it.
            return;
        }

        if (!result.Succeeded || result.Value is not { } verdicts || _network is null)
        {
            return;
        }

        var party = context.Platform.Party;

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

        // The restrictions just applied change what the roster's mute/restricted
        // indicators should show.
        RefreshRoster(context);
    }

    private void FailConnection(UiContext context, string reason)
    {
        _connectFailed = true;
        ReturnToMenuWithDialog(context, "Connection Failed", reason);
    }

    private void FinishConnecting(UiContext context)
    {
        var local = _network!.LocalPlayer;
        _selectedStyleId = local?.ShipStyleId ?? context.Profile.ShipStyleId;
        _selectedColorId = local?.ShipColorId ?? context.Profile.ShipColorId;

        _network.MatchStateChanged += OnMatchStateChanged;
        _network.CountdownChanged += OnCountdownChanged;
        _network.RosterChanged += OnRosterChanged;
        _network.ConnectionLost += OnConnectionLost;

        _ready = true;
        Rebuild(context);
        RefreshRoster(context);
        RefreshStatus(context);

        // XR-015: the mute/avoid lists are refreshed once per session rather than per
        // roster change - they belong to the signed-in account, not to any one peer -
        // and EvaluatePrivacyAsync (driven off every roster change below) reads the
        // refreshed lists for whoever is actually in the roster.
        //
        // The communications privilege is deliberately absent from this block. It is
        // resolved and awaited by ConnectAsync before the network exists; dispatching it
        // here, beside the list refresh and unawaited, was what left the microphone live
        // until the round trip landed.
        if (!_network.IsOffline)
        {
            _ = context.Platform.Privacy.RefreshListsAsync();
            _nextCommunicationsRecheck = context.Time + CommunicationsRecheckSeconds;
        }

        // PeerJoined and the host's identity request can arrive before JoinAsync
        // returns. Start the roster handshake after both network and UI are listening.
        if (_network is PartyMatchNetwork party)
        {
            party.SendIdentity();
        }
    }

    // --- List construction ----------------------------------------------------

    /// <summary>
    /// Sets up the roster list once. See the type remarks for why the rows themselves
    /// are only re-bound, never rebuilt, when the roster changes.
    /// </summary>
    private void Rebuild(UiContext context)
    {
        _list.Clear();
        _list.Bounds = RosterListBounds;
        _list.BoxedRows = true;

        // 61 px rows at the source's 64 px _ROSTER_ROW_PITCH.
        _list.RowSeparation = 3;
        BuildRows(context);
        _list.FocusFirst();

        // Focus starts on the selected ship, matching `_ship_tab_buttons[_selected_style_id]
        // .call_deferred("grab_focus")`. The list still needs a valid focus row for later
        // transitions back into the roster.
        _focusZone = FocusZone.ShipTabs;
        _shipTabFocus = Math.Clamp(_selectedStyleId, 0, 3);
        _colorTabFocus = Math.Clamp(_selectedColorId, 0, TuningLibrary.Palette.Size - 1);

        // MenuList focused a row; the roster is not the active zone yet, so drop that
        // paint state until the player walks into the roster.
        if (_list.Focused is { } row)
        {
            row.HasFocus = false;
        }
    }

    private void BuildRows(UiContext context)
    {
        _ = context;

        foreach (var row in _rosterRows)
        {
            _list.AddRow(row);
        }
    }

    // --- Ship / colour selection ------------------------------------------------

    private void OnShipStyleChanged(UiContext context, int styleId)
    {
        _selectedStyleId = styleId;
        SetLocalAppearance(context, _selectedColorId, _selectedStyleId);
    }

    private void OnColorChanged(UiContext context, int colorId)
    {
        if (ColorsTakenByOthers().Contains(colorId))
        {
            // Refuses the pick exactly as `_on_color_selected`'s
            // `if _colors_taken_by_others().has(color_id): return` did.
            return;
        }

        _selectedColorId = colorId;
        SetLocalAppearance(context, _selectedColorId, _selectedStyleId);
    }

    private HashSet<int> ColorsTakenByOthers()
    {
        var taken = new HashSet<int>();
        var localId = _network!.LocalPeerId;

        foreach (var player in _network.Players.Values)
        {
            if (player.PeerId != localId)
            {
                taken.Add(player.ShipColorId);
            }
        }

        return taken;
    }

    private void SetLocalAppearance(UiContext context, int colorId, int styleId)
    {
        if (_network!.LocalPlayer is null)
        {
            return;
        }

        context.Profile.ShipColorId = colorId;
        context.Profile.ShipStyleId = styleId;
        context.Profile.MarkDirty();

        // Sets the local roster row and, depending on whether this peer is the host
        // or a client, either broadcasts or sends MessageType.SubmitAppearance - see
        // IMatchNetwork.PublishLocalAppearance.
        _network.PublishLocalAppearance(colorId, styleId);
        RefreshRoster(context);
    }

    // --- Roster ------------------------------------------------------------

    private void OnInviteRequested()
    {
        if (_context is not { } context || _network is null)
        {
            return;
        }

        // Sending an invite needs a session someone can join, so a practice match's
        // empty slots stay the original's inert placeholder - matches `_can_invite()`.
        if (_network.IsOffline || !context.Platform.Capabilities.HasFlag(PlatformCapabilities.Invites))
        {
            return;
        }

        _ = ShowInviteUiAsync(context);
    }

    /// <summary>
    /// Opens the platform invite composer and, unlike the fire-and-forget call this
    /// replaced, tells the player when it did not open (#22).
    /// </summary>
    /// <remarks>
    /// The invite the shell sends carries the published joinable activity, so the most
    /// likely refusal is that this session has not been advertised yet - a state the
    /// player can do something about (wait a moment, or check they are online) but only
    /// if they are told about it. Silently discarding the result made a failed invite
    /// indistinguishable from one that was sent and ignored.
    /// </remarks>
    private async Task ShowInviteUiAsync(UiContext context)
    {
        var result = await context.Platform.Activity.ShowInviteUiAsync();
        if (result.Succeeded || result.Status == PlatformStatus.Canceled)
        {
            return;
        }

        context.Screens.ShowDialog(
            "Invite",
            result.Message ?? "The invite screen could not be opened.",
            DialogSeverity.Warning);
    }

    private void OnActionsRequested(int peerId)
    {
        if (_context is not { } context || _network is null)
        {
            return;
        }

        // A second activation while the overlay is already open must not stack a
        // second one - mirrors `if _player_actions != null: return`.
        if (context.Screens.Find<PlayerActions>() is not null)
        {
            return;
        }

        if (!_network.Players.TryGetValue(peerId, out var state))
        {
            return;
        }

        context.Screens.Push(new PlayerActions(peerId, state.DisplayName, _network));
    }

    private void OnRosterChanged()
    {
        if (_context is { } context && _ready)
        {
            RefreshRoster(context);
            RefreshStatus(context);

            // XR-064 / XR-067: republish the activity's live player count and report
            // the current roster as encountered, on every change - not just at host
            // time or match end - so a peer who leaves before the match starts still
            // shows up in Recently Played With and the advertised session never lags
            // the real roster.
            //
            // #23: every member republishes, not only the host. The activity is what a
            // friend's profile view reads to decide between Join and Play, and it is
            // per-player - a client that never publishes is a client nobody can join
            // through.
            PublishActivity(context);

            ReportRecentPlayers(context);

            // XR-015: a peer who just joined has never had their mute/avoid verdict
            // evaluated, so this must run on every roster change, not once.
            _ = EvaluatePrivacyAsync(context);

            // XR-046/XR-048: a peer's DisplayName up to this point is whatever string
            // it sent over the wire in SubmitIdentity - see PartyMatchNetwork - which
            // nothing verifies against the account actually signed in there. Resolving
            // it against the platform's own profile service, keyed on the same xuid
            // the wire carried, catches a stale cached name or a display name from a
            // different account than the one that connected.
            ResolveRemoteDisplayNames(context);

            TryAutoStart(context);
        }
    }

    private readonly HashSet<string> _resolvingDisplayNames = [];

    /// <summary>
    /// Best-effort, one lookup per xuid per visit: <see cref="_resolvingDisplayNames"/>
    /// stops a player who has not answered yet from being queried again on every
    /// subsequent roster tick, which would turn "one profile lookup per new peer" into
    /// "N lookups per peer per roster change".
    /// </summary>
    /// <remarks>
    /// XR-048: the set is pruned to the current roster on every pass, so an xuid that
    /// leaves and comes back is resolved again rather than being written off as already
    /// answered. That is the case where a name has most likely moved - the player went
    /// out to the shell, which is where a gamertag gets changed - and it also bounds the
    /// set to the roster instead of letting it grow for the lifetime of the lobby.
    /// </remarks>
    private void ResolveRemoteDisplayNames(UiContext context)
    {
        if (_network is null || _network.IsOffline)
        {
            return;
        }

        _resolvingDisplayNames.RemoveWhere(
            xuid => !_network.Players.Values.Any(player => player.XboxUserId == xuid));

        foreach (var player in _network.Players.Values)
        {
            if (player.PeerId == _network.LocalPeerId || string.IsNullOrEmpty(player.XboxUserId))
            {
                continue;
            }

            if (!_resolvingDisplayNames.Add(player.XboxUserId))
            {
                continue;
            }

            _ = ResolveOneDisplayNameAsync(context, player);
        }
    }

    private async Task ResolveOneDisplayNameAsync(UiContext context, PlayerState player)
    {
        var result = await context.Platform.Social.ResolveDisplayNameAsync(player.XboxUserId);

        if (_transitioning || !result.Succeeded)
        {
            return;
        }

        player.DisplayName = result.Value!;

        if (_context is { } current && _ready)
        {
            RefreshRoster(current);
        }
    }

    /// <summary>
    /// Practice is a single-machine session nobody can join, so the roster collapses
    /// to the local player instead of advertising open slots that will never fill.
    /// </summary>
    private int RosterCapacity()
        => _network is OfflineMatchNetwork offline
            ? Math.Clamp(1 + offline.BotCount, 1, RosterSlotMax)
            : Math.Clamp(TuningLibrary.GameMode.PlayerCount, 1, RosterSlotMax);

    private void RefreshRoster(UiContext context)
    {
        var network = _network!;
        var players = network.SortedPlayers();
        var capacity = RosterCapacity();
        var invitable = !network.IsOffline && context.Platform.Capabilities.HasFlag(PlatformCapabilities.Invites);
        var party = context.Platform.Party;

        for (var i = 0; i < _rosterRows.Length; i++)
        {
            var row = _rosterRows[i];
            row.Visible = i < capacity;
            row.SetInvitable(invitable);

            var state = i < players.Count ? players[i] : null;

            if (state is null)
            {
                row.SetActionsContext(false, false);
                row.SetChatIndicator(ChatIndicator.None);
            }
            else
            {
                var actionable = CanMutePeer(context, state) || CanReportPeer(context, state);
                row.SetActionsContext(actionable, party.IsPeerMuted(state.PeerId));
                row.SetChatIndicator(
                    network.IsOffline || !party.HasNetwork ? ChatIndicator.None : party.GetChatIndicator(state.PeerId));
            }

            row.SetState(state);
        }
    }

    /// <summary>
    /// True when the local player may mute or unmute this peer. Duplicated in
    /// <see cref="PlayerActions"/> rather than shared, because there is no
    /// <c>NetManager</c>-equivalent facade both can call into - each element resolves
    /// the verdict itself from the same two shared inputs, <see cref="IMatchNetwork"/>
    /// and <see cref="IPlatformProvider"/>.
    /// </summary>
    private bool CanMutePeer(UiContext context, PlayerState player)
    {
        var party = context.Platform.Party;

        if (_network!.IsOffline || !party.HasNetwork || !party.ChatAllowed || player.PeerId == _network.LocalPeerId)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(player.XboxUserId)
            && context.Platform.Privacy.GetCached(player.XboxUserId) is { AllowVoice: false })
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// True when this peer can be reported, gating whether the row opens the actions
    /// overlay for that reason alone. Mirrors <c>NetManager.can_report_player</c>.
    /// </summary>
    private bool CanReportPeer(UiContext context, PlayerState player)
        => !_network!.IsOffline
            && player.PeerId != _network.LocalPeerId
            && context.Platform.Capabilities.HasFlag(PlatformCapabilities.Moderation)
            && !string.IsNullOrEmpty(player.XboxUserId);

    // --- Ready / start -------------------------------------------------------

    private void ToggleReady(UiContext context)
    {
        var local = _network!.LocalPlayer;

        if (local is null)
        {
            return;
        }

        SetLocalReady(context, !local.IsReady);
        TryAutoStart(context);
    }

    private void SetLocalReady(UiContext context, bool ready)
    {
        if (_network!.LocalPlayer is null)
        {
            return;
        }

        // Sets the local roster row and, depending on whether this peer is the host or
        // a client, either broadcasts or sends MessageType.SubmitReady - see
        // IMatchNetwork.PublishLocalReady.
        _network.PublishLocalReady(ready);
        RefreshRoster(context);
        RefreshStatus(context);
    }

    /// <summary>
    /// The original has no Start Match button: the host starts as soon as everyone is
    /// ready. An online lobby of one is guarded so readying up alone does not
    /// immediately launch the match; a practice session is meant to be solo and starts
    /// the moment the local player is ready.
    /// </summary>
    private void TryAutoStart(UiContext context)
    {
        if (_transitioning || !_network!.IsHost)
        {
            return;
        }

        if (!_network.IsOffline && _network.Players.Count < 2)
        {
            return;
        }

        if (!EveryoneReady())
        {
            return;
        }

        _network.SetMatchState(MatchState.Starting);
        GoToGameplay(context);    }

    private bool EveryoneReady()
    {
        if (_network!.Players.Count == 0)
        {
            return false;
        }

        foreach (var player in _network.Players.Values)
        {
            if (!player.IsReady)
            {
                return false;
            }
        }

        return true;
    }

    private void GoToGameplay(UiContext context)
    {
        if (_transitioning)
        {
            return;
        }

        _transitioning = true;

        CrashLog.Mark(
            $"lobby: leaving for gameplay, host={_network!.IsHost} offline={_network.IsOffline} "
            + $"players={_network.Players.Count}");

        // XR-064: the session stops being joinable the moment the match starts - late
        // joiners are refused - so the activity that advertised it must go with it,
        // rather than keep inviting players into a match that will turn them away.
        if (!_network.IsOffline)
        {
            context.Activity.Clear();
        }

        context.Screens.ReplaceAll(new GameplayScreen(_network!));
    }

    // --- Voice chat ----------------------------------------------------------

    private void OnVoiceChatToggled(UiContext context)
    {
        var party = context.Platform.Party;

        if (_network!.IsOffline || !party.ChatAllowed)
        {
            return;
        }

        party.IsSelfMuted = !party.IsSelfMuted;
        RefreshStatus(context);
    }

    // --- Status / hint ---------------------------------------------------------

    private void RefreshStatus(UiContext context)
    {
        // Recomputed on demand in Draw rather than cached, matching how cheap the
        // GDScript's _refresh_status/_refresh_hint text assembly was.
    }

    private string StatusText(UiContext context)
    {
        if (_countdownText.Length > 0)
        {
            return _countdownText;
        }

        var local = _network!.LocalPlayer;

        // XR-064: a session the shell was never told about is not joinable by invite,
        // and the player has no other way to find that out - the lobby otherwise looks
        // entirely healthy while nobody can get in. Ranked above the ready prompts
        // because it changes what the player should do about the lobby.
        if (!_network.IsOffline && context.Activity.FailureMessage is { Length: > 0 } activityFailure)
        {
            return activityFailure;
        }

        if (local is not null && !local.IsReady)
        {
            return context.Input.ReadyPrompt;
        }

        if (_network.IsOffline)
        {
            return "Starting practice match\u2026";
        }

        if (_network.Players.Count < 2)
        {
            return "Waiting for players\u2026";
        }

        return !EveryoneReady() ? "Waiting for all players to ready up\u2026" : "Starting match\u2026";
    }

    private string HintText(UiContext context)
    {
        var party = context.Platform.Party;

        return context.Input.LobbyHint(!_network!.IsOffline && party.ChatAllowed, party.IsSelfMuted);
    }

    // --- Net failure handlers --------------------------------------------------

    private void OnMatchStateChanged(MatchState state)
    {
        if (state.HasMatchState(MatchState.Starting) || state.HasMatchState(MatchState.Running))
        {
            if (_context is { } context)
            {
                GoToGameplay(context);
            }
        }
    }

    private void OnCountdownChanged(int secondsRemaining)
    {
        _countdownText = secondsRemaining > 0 ? $"Match starting in {secondsRemaining}\u2026" : string.Empty;
    }

    private void OnConnectionLost(PlatformResult result)
    {
        if (_context is { } context)
        {
            // XR-003: a JoinRefused (match already started) carries its own explanation
            // through this same event; falling back to the generic wording only when
            // the provider did not supply one, matching every other best-effort message
            // surface in this screen.
            ReturnToMenuWithDialog(
                context,
                "Disconnected",
                string.IsNullOrEmpty(result.Message) ? "The connection to the host was lost." : result.Message);
        }
    }

    private void ReturnToMenuWithDialog(UiContext context, string title, string message)
    {
        if (_transitioning)
        {
            return;
        }

        _transitioning = true;
        context.Screens.ShowDialog(
            title,
            message,
            DialogSeverity.Error,
            showCancel: false,
            onDismissed: dismissed => _ = LeaveAndReturnToMenuAsync(context));
    }

    private async Task LeaveAndReturnToMenuAsync(UiContext context)
    {
        if (_network is not null && !_network.IsOffline)
        {
            await context.Platform.Party.LeaveAsync();

            // XR-064: an activity nobody deletes keeps advertising a session that no
            // longer exists, so a shell "Join game" lands a player in a lobby that is
            // gone. Serialised and retried by the coordinator (L4).
            context.Activity.Clear();
        }

        if (_network is IDisposable disposableNetwork)
        {
            disposableNetwork.Dispose();
        }

        context.Screens.ReplaceAll(new MainMenuScreen());
    }

    // --- Layout / drawing --------------------------------------------------
    //
    // Every geometry constant below is lifted directly from
    // scenes/ui/screens/lobby_screen.tscn against the shared 1920x1080 design canvas
    // (NetRumbleGame.DesignWidth/DesignHeight), which context.Screen already reports
    // as. Any Rectangle here is (offset_left, offset_top, offset_right-offset_left,
    // offset_bottom-offset_top) copied from the .tscn without conversion.

    // State label - the top strip that carries the connection / countdown / prompt text.
    private static readonly Rectangle StateLabelRect = new(360, 60, 1200, 40);

    // Ship panel: title, background slab, 4 tabs across the top, and the rotated preview.
    private static readonly Rectangle ShipTitleRect = new(156, 140, 360, 54);
    private static readonly Rectangle ShipBackgroundRect = new(142, 194, 620, 552);
    private static readonly Rectangle ShipTabsRect = new(142, 194, 620, 100);
    private static readonly Rectangle ShipTextureRect = new(356, 424, 192, 192);
    private const float ShipRotation = 0.6f;

    // Colour panel.
    private static readonly Rectangle ColorTitleRect = new(156, 756, 360, 54);
    private static readonly Rectangle ColorTabsRect = new(142, 821, 600, 100);

    // Middle column: game type / rules / (join code or opponents).
    private static readonly Rectangle GameTypeTitleRect = new(826, 194, 360, 61);
    private static readonly Rectangle GameTypePanelRect = new(826, 255, 360, 61);
    private static readonly Rectangle GameTypeValueRect = new(846, 267, 340, 49);
    private static readonly Rectangle RulesTitleRect = new(826, 377, 360, 61);
    private static readonly Rectangle RulesPanelRect = new(826, 438, 360, 130);
    private static readonly Rectangle RulesLabelRect = new(846, 450, 320, 118);
    private static readonly Rectangle JoinOpponentsTitleRect = new(826, 580, 360, 61);
    private static readonly Rectangle JoinOpponentsPanelRect = new(826, 641, 360, 61);
    private static readonly Rectangle JoinCodeLabelRect = new(846, 653, 340, 49);
    private static readonly Rectangle OpponentsValueRect = new(846, 653, 200, 49);
    private static readonly Rectangle OpponentsMinusRect = new(1056, 647, 50, 49);
    private static readonly Rectangle OpponentsPlusRect = new(1116, 647, 50, 49);

    // Roster column.
    private static readonly Rectangle RosterTitleRect = new(1293, 140, 360, 54);
    private static readonly Rectangle RosterListBounds = new(1279, 194, 520, 512);

    // Hint strip along the bottom.
    private static readonly Rectangle HintLabelRect = new(360, 985, 1200, 40);

    // The 4 ship tabs sit at 151.25x100 with a 156.25 pitch inside the 620-wide strip -
    // the fractional values from the .gd, rounded to the nearest integer per tab for
    // sprite-batch destination rects. Rounding is deliberate; the source is a Control
    // layout that lets Godot subpixel it, the batch cannot.
    private const int ShipTabWidth = 151;
    private const int ShipTabHeight = 100;

    // The 8 colour tabs sit at 75x100 across the 600-wide strip.
    private const int ColorTabWidth = 75;
    private const int ColorTabHeight = 100;
    private const int ColorAccentWidth = 76;
    private const int ColorAccentHeight = 16;

    /// <summary>x offset of ship tab <paramref name="index"/> inside <see cref="ShipTabsRect"/>.</summary>
    private static int ShipTabX(int index) => ShipTabsRect.X + (int)MathF.Round(index * 156.25f);

    private static int ColorTabX(int index) => ColorTabsRect.X + (index * ColorTabWidth);

    private void DrawBackground(UiContext context)
        => context.Theme.Fill(context.Batch, context.Screen, UiTheme.ScreenBackground);

    private void DrawStateLabel(UiContext context)
    {
        var text = StatusText(context);

        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        ButtonPrompt.DrawCentre(
            context.Batch,
            context.Assets,
            context.Theme.Body,
            text,
            new Vector2(StateLabelRect.Center.X, StateLabelRect.Center.Y),
            UiTheme.Accent);
    }

    /// <summary>
    /// The ship-panel column: title, background slab, four ship tabs across the top, and
    /// the rotated ship preview built from the ship base tinted with the player's colour
    /// and the ship overlay drawn white on top.
    /// </summary>
    private void DrawShipSection(UiContext context)
    {
        DrawPanelHeader(context, ShipTitleRect, "SPACESHIP", TextAlign.Left, verticalBottom: true);
        DrawTexturePanel(context, ShipBackgroundRect, "Lobby_BackgroundSpaceship");

        DrawShipTabs(context);
        DrawShipPreview(context);
    }

    private void DrawShipTabs(UiContext context)
    {
        var square = context.Assets.Texture("Shape_Square");
        var silhouetteAll = new Texture2D[4];

        for (var i = 0; i < 4; i++)
        {
            silhouetteAll[i] = context.Assets.ShipTexture(i, "Silhouette");
        }

        for (var index = 0; index < 4; index++)
        {
            var tabRect = new Rectangle(ShipTabX(index), ShipTabsRect.Y, ShipTabWidth, ShipTabHeight);
            var alpha = 0.15f;

            if (index == _selectedStyleId)
            {
                alpha = 0.65f;
            }
            else if (_focusZone == FocusZone.ShipTabs && index == _shipTabFocus)
            {
                alpha = 0.45f;
            }

            var tint = Color.FromNonPremultiplied(189, 199, 199, (int)MathF.Round(alpha * 255f));
            context.Batch.Draw(square, tabRect, tint);

            var silhouetteCentre = new Vector2(tabRect.X + 76, tabRect.Y + 50);
            DrawRotated(context.Batch, silhouetteAll[index], silhouetteCentre, 64f, ShipRotation, UiTheme.Neutral);

            if (_focusZone == FocusZone.ShipTabs && index == _shipTabFocus)
            {
                context.Theme.Stroke(context.Batch, tabRect, UiTheme.Accent);
            }
        }
    }

    /// <summary>
    /// The rotated ship preview. Two stacked layers, matching <c>_refresh_ship</c>: the
    /// base carries the player's colour while the overlay decals stay white so they
    /// render on top of any hull colour.
    /// </summary>
    private void DrawShipPreview(UiContext context)
    {
        var centre = new Vector2(ShipTextureRect.Center.X, ShipTextureRect.Center.Y);
        var colour = new Color(TuningLibrary.Palette.ColorAt(_selectedColorId).ToRgba());

        DrawRotated(
            context.Batch,
            context.Assets.ShipTexture(_selectedStyleId, "Base"),
            centre,
            ShipTextureRect.Width,
            ShipRotation,
            colour);

        DrawRotated(
            context.Batch,
            context.Assets.ShipTexture(_selectedStyleId, "Overlay"),
            centre,
            ShipTextureRect.Width,
            ShipRotation,
            Color.White);
    }

    private void DrawColorSection(UiContext context)
    {
        DrawPanelHeader(context, ColorTitleRect, "COLOR", TextAlign.Left, verticalBottom: true);
        DrawColorTabs(context);
    }

    /// <summary>
    /// Colour tabs. The selected tab expands into the full panel, showing the ship
    /// silhouette above a 76x16 accent strip pinned to the bottom; unselected tabs
    /// collapse to the accent strip alone at the top of the tab. A taken colour is
    /// darkened to say it cannot be picked without shouting about it.
    /// </summary>
    private void DrawColorTabs(UiContext context)
    {
        var taken = ColorsTakenByOthers();
        var panelTex = context.Assets.Texture("Lobby_BackgroundColor");
        var accentTex = context.Assets.Texture("Lobby_BackgroundColorSlot");
        var silhouetteTex = context.Assets.ShipTexture(_selectedStyleId, "Silhouette");
        var count = TuningLibrary.Palette.Size;

        for (var colorId = 0; colorId < count; colorId++)
        {
            var tabRect = new Rectangle(ColorTabX(colorId), ColorTabsRect.Y, ColorTabWidth, ColorTabHeight);
            var selected = colorId == _selectedColorId;
            var focused = _focusZone == FocusZone.ColorTabs && colorId == _colorTabFocus;
            var baseColour = new Color(TuningLibrary.Palette.ColorAt(colorId).ToRgba());
            var accentColour = focused && !selected ? BrightenBy(baseColour, 0.3f) : baseColour;

            if (taken.Contains(colorId))
            {
                accentColour = DarkenBy(accentColour, 0.6f);
            }

            if (selected)
            {
                context.Batch.Draw(panelTex, tabRect, UiTheme.PanelTexture);
                var silhouetteCentre = new Vector2(tabRect.X + 37, tabRect.Y + 37);
                DrawRotated(context.Batch, silhouetteTex, silhouetteCentre, 64f, ShipRotation, accentColour);
            }

            var accentY = selected ? tabRect.Y + ColorTabHeight - ColorAccentHeight : tabRect.Y;
            var accentRect = new Rectangle(tabRect.X, accentY, ColorAccentWidth, ColorAccentHeight);
            context.Batch.Draw(accentTex, accentRect, accentColour);

            if (focused)
            {
                context.Theme.Stroke(context.Batch, tabRect, UiTheme.Accent);
            }
        }
    }

    /// <summary>
    /// The game-type row: a header, a texture-backed slab, and the mode's display name.
    /// There is only one mode, so this is now a fixed caption - the panel is kept because
    /// it is part of the lobby's authored layout, and a player still wants telling what
    /// they are about to play.
    /// </summary>
    private void DrawGameTypeSection(UiContext context)
    {
        DrawPanelHeader(context, GameTypeTitleRect, "GAME TYPE", TextAlign.Left, verticalCentre: true);
        DrawTexturePanel(context, GameTypePanelRect, "Lobby_BackgroundGameType");
        UiTheme.TextLeft(
            context.Batch,
            context.Theme.Body,
            TuningLibrary.GameMode.DisplayName,
            new Vector2(GameTypeValueRect.X, GameTypeValueRect.Center.Y),
            UiTheme.Text);
    }

    /// <summary>
    /// Reproduces <c>_refresh_rules</c>: "Score to win: X\nTime limit: Y min\nPlayers: Z".
    /// The authored box is only 118 px high, so these three detail lines use the small
    /// font and are vertically centred instead of overflowing below the panel.
    /// </summary>
    private void DrawRulesSection(UiContext context)
    {
        DrawPanelHeader(context, RulesTitleRect, "RULES", TextAlign.Left, verticalCentre: true);
        DrawTexturePanel(context, RulesPanelRect, "Lobby_BackgroundRules");

        var mode = TuningLibrary.GameMode;
        var minutes = (int)(mode.TimeLimit / 60f);
        string[] lines =
        [
            $"Score to win: {mode.TargetScore}",
            $"Time limit: {minutes} min",
            $"Players: {RosterCapacity()}",
        ];

        var font = context.Theme.Small;
        var lineHeight = font.LineSpacing;
        var contentHeight = lineHeight * lines.Length;
        var y = RulesLabelRect.Y + Math.Max(0, (RulesLabelRect.Height - contentHeight) / 2);

        foreach (var line in lines)
        {
            context.Batch.DrawString(font, line, new Vector2(RulesLabelRect.X, y), UiTheme.Text);
            y += lineHeight;
        }
    }

    /// <summary>
    /// The bottom slot in the middle column is either the join code (networked match) or
    /// the opponents picker (practice) - never both, since a join code is meaningless in
    /// a session nobody can join.
    /// </summary>
    private void DrawJoinCodeOrOpponentsSection(UiContext context)
    {
        if (_network is null)
        {
            return;
        }

        if (_network is OfflineMatchNetwork offline)
        {
            DrawOpponentsPanel(context, offline);
            return;
        }

        DrawJoinCodePanel(context);
    }

    private void DrawJoinCodePanel(UiContext context)
    {
        var code = context.Platform.Party.JoinCode;

        if (string.IsNullOrEmpty(code))
        {
            return;
        }

        DrawPanelHeader(context, JoinOpponentsTitleRect, "JOIN CODE", TextAlign.Left, verticalCentre: true);
        // There is no dedicated join-code panel texture in AssetRegistry, and the .gd
        // reuses the game-type panel for the same slot: `_join_code_panel.texture =
        // Assets.texture("Lobby_BackgroundGameType")`.
        DrawTexturePanel(context, JoinOpponentsPanelRect, "Lobby_BackgroundGameType");
        UiTheme.TextLeft(
            context.Batch,
            context.Theme.Body,
            code,
            new Vector2(JoinCodeLabelRect.X, JoinCodeLabelRect.Center.Y),
            UiTheme.Text);
    }

    private void DrawOpponentsPanel(UiContext context, OfflineMatchNetwork offline)
    {
        DrawPanelHeader(context, JoinOpponentsTitleRect, "OPPONENTS", TextAlign.Left, verticalCentre: true);
        DrawTexturePanel(context, JoinOpponentsPanelRect, "Lobby_BackgroundGameType");

        var count = offline.BotCount;

        UiTheme.TextLeft(
            context.Batch,
            context.Theme.Body,
            count == 0 ? "None" : count.ToString(CultureInfo.InvariantCulture),
            new Vector2(OpponentsValueRect.X, OpponentsValueRect.Center.Y),
            UiTheme.Text);

        // The steppers clamp rather than wrap, so the ends of the range draw disabled to
        // show the limit instead of looking live and silently doing nothing.
        var focused = _focusZone == FocusZone.Opponents;
        DrawStepper(context, OpponentsMinusRect, "-", count > 0, focused);
        DrawStepper(context, OpponentsPlusRect, "+", count < NRConst.MaxPracticeBots, focused);
    }

    private void DrawStepper(
        UiContext context,
        Rectangle rect,
        string glyph,
        bool enabled,
        bool focused = false)
    {
        context.Theme.Box(
            context.Batch,
            rect,
            enabled ? UiTheme.ButtonNormal : UiTheme.ButtonDisabled,
            focused && enabled ? UiTheme.Accent : UiTheme.OverlayBorder);

        UiTheme.TextCentre(
            context.Batch,
            context.Theme.Button,
            glyph,
            new Vector2(rect.Center.X, rect.Center.Y),
            enabled ? UiTheme.Text : UiTheme.TextDisabled);
    }

    private void DrawRosterSection(UiContext context)
    {
        // No column-wide backing texture: LobbyBackground_Roster is a single 520x61 row,
        // and each RosterRow draws its own copy. Stretching one over the whole column
        // smears its bevel into vertical streaks.
        DrawPanelHeader(context, RosterTitleRect, "ROSTER", TextAlign.Left, verticalBottom: true);
        _list.Draw(context);
    }

    private void DrawHint(UiContext context)
    {
        ButtonPrompt.DrawCentre(
            context.Batch,
            context.Assets,
            context.Theme.Body,
            HintText(context),
            new Vector2(HintLabelRect.Center.X, HintLabelRect.Center.Y),
            UiTheme.Neutral);
    }

    // --- Small drawing helpers ------------------------------------------------

    /// <summary>
    /// Draws a section header the way the .tscn's <c>LobbyPanelHeader</c> label does:
    /// panel-header font, white, aligned inside <paramref name="rect"/>. The Godot
    /// label was either bottom-aligned (top-row titles) or centre-aligned (mid-column
    /// titles) depending on the section.
    /// </summary>
    private static void DrawPanelHeader(
        UiContext context,
        Rectangle rect,
        string text,
        TextAlign align,
        bool verticalCentre = false,
        bool verticalBottom = false)
    {
        var font = context.Theme.PanelHeader;
        var size = font.MeasureString(text);

        float y;

        if (verticalBottom)
        {
            y = rect.Bottom - size.Y;
        }
        else if (verticalCentre)
        {
            y = rect.Center.Y - (size.Y / 2f);
        }
        else
        {
            y = rect.Y;
        }

        float x = align switch
        {
            TextAlign.Centre => rect.Center.X - (size.X / 2f),
            TextAlign.Right => rect.Right - size.X,
            _ => rect.X,
        };

        context.Batch.DrawString(font, text, new Vector2(x, y), UiTheme.Text);
    }

    /// <summary>
    /// Paints one of the section-background textures at
    /// <see cref="UiTheme.PanelTexture"/>, matching the .tscn's
    /// <c>modulate = Color(0.74, 0.78, 0.78, 0.66)</c> across every panel.
    /// </summary>
    private static void DrawTexturePanel(UiContext context, Rectangle bounds, string textureKey)
    {
        var texture = context.Assets.Texture(textureKey);
        context.Batch.Draw(texture, bounds, UiTheme.PanelTexture);
    }

    /// <summary>
    /// Draws <paramref name="texture"/> centred on <paramref name="centre"/>, rotated by
    /// <paramref name="rotation"/> radians, scaled so its longer side fits inside a
    /// <paramref name="sizePx"/>-wide square. Used for the rotated ship preview and the
    /// rotated silhouettes on the ship and colour tabs.
    /// </summary>
    private static void DrawRotated(
        SpriteBatch batch, Texture2D texture, Vector2 centre, float sizePx, float rotation, Color tint)
    {
        var scale = MathF.Min(sizePx / texture.Width, sizePx / texture.Height);
        var origin = new Vector2(texture.Width / 2f, texture.Height / 2f);
        batch.Draw(texture, centre, null, tint, rotation, origin, scale, SpriteEffects.None, 0f);
    }

    private static Color BrightenBy(Color color, float delta) => new(
        MathF.Min(color.R / 255f + delta, 1f),
        MathF.Min(color.G / 255f + delta, 1f),
        MathF.Min(color.B / 255f + delta, 1f),
        color.A / 255f);

    private static Color DarkenBy(Color color, float amount)
    {
        var factor = 1f - amount;
        return new Color(
            (color.R / 255f) * factor,
            (color.G / 255f) * factor,
            (color.B / 255f) * factor,
            color.A / 255f);
    }
}
