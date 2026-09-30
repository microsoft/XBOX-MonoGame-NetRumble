using Microsoft.Xna.Framework;
using NetRumble.Core;
using NetRumble.Core.Net;
using NetRumble.Game.UI.Widgets;
using NetRumble.Platform;

namespace NetRumble.Game.UI.Elements;

/// <summary>
/// Ports <c>scripts/ui/elements/nr_player_actions.gd</c>: the actions the local player
/// can take against another lobby member - mute (XR-015), report (XR-018) and the
/// system profile card.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="Screen"/> for the same reason as <see cref="JoinCodeEntry"/>: it needs
/// its own slot in the input stack (popup over the lobby, explicit Back handling) that
/// <see cref="ScreenManager"/> already provides to anything pushed onto it.
/// </para>
/// <para>
/// <b>Divergence: reasons are an enum, not a service-provided list.</b> The GDScript
/// built its report-reason rows from <c>ModerationService.REPORT_REASONS</c>, a data
/// table the service owned so the reasons PlayFab actually accepts could not drift from
/// the ones offered. This port's <see cref="IModerationService.ReportPlayerAsync"/>
/// instead takes a closed <see cref="PlayerReportType"/> enum, so the reason rows are
/// built directly from its members with hand-written labels. There is nothing left to
/// drift, so the indirection would have bought nothing.
/// </para>
/// <para>
/// <b>Re-entrancy:</b> the lobby guards against opening two of these at once by
/// checking <c>ScreenManager.Find&lt;PlayerActions&gt;()</c> before pushing a second -
/// see <c>LobbyScreen.OnRosterRowActionsRequested</c> - which reproduces the GDScript's
/// <c>if _player_actions != null: return</c> guard without this class needing to know
/// about it itself.
/// </para>
/// </remarks>
public sealed class PlayerActions : Screen
{
    private readonly int _peerId;
    private readonly string _playerName;
    private readonly IMatchNetwork _network;
    private readonly MenuList _actions = new();

    private bool _reporting;
    private bool _closed;
    private string _statusText = string.Empty;
    private Task? _pendingCall;

    public PlayerActions(int peerId, string playerName, IMatchNetwork network)
    {
        _peerId = peerId;
        _playerName = string.IsNullOrEmpty(playerName) ? "Player" : playerName;
        _network = network;

        IsPopup = true;
        AllowBack = false;
    }

    public override void Enter(UiContext context) => BuildActions(context);

    public override void Update(UiContext context)
    {
        _actions.Bounds = ActionArea(context);
        _actions.Update(context);
    }

    public override bool HandleAction(UiContext context, MenuAction action)
    {
        if (action == MenuAction.Back)
        {
            OnCancel(context);
            return true;
        }

        return _actions.HandleAction(context, action);
    }

    public override void Draw(UiContext context)
    {
        var panel = PanelBounds(context);
        context.Theme.Fill(context.Batch, context.Screen, Color.FromNonPremultiplied(0, 0, 0, 140));
        context.Theme.Box(context.Batch, panel, UiTheme.DialogPanel, UiTheme.DialogBorder);

        var titleBar = new Rectangle(panel.X, panel.Y, panel.Width, 80);
        context.Theme.Box(context.Batch, titleBar, UiTheme.DialogTitle, UiTheme.Accent);
        UiTheme.TextCentre(
            context.Batch,
            context.Theme.Subtitle,
            _reporting ? $"Report {_playerName}" : _playerName,
            new Vector2(titleBar.Center.X, titleBar.Center.Y),
            UiTheme.Text);

        if (_statusText.Length > 0)
        {
            UiTheme.TextCentre(
                context.Batch,
                context.Theme.Body,
                _statusText,
                new Vector2(panel.Center.X, titleBar.Bottom + 48),
                UiTheme.TextDisabled);
        }

        _actions.Draw(context);
    }

    private void BuildActions(UiContext context)
    {
        _reporting = false;
        _statusText = string.Empty;
        _actions.Clear();

        var party = context.Platform.Party;

        if (CanMute(context))
        {
            var muted = party.IsPeerMuted(_peerId);
            _actions.AddButton(muted ? "Unmute Player" : "Mute Player", () => OnMute(context));
        }

        if (CanReport(context))
        {
            _actions.AddButton("Report Player", BuildReportReasons);
        }

        // XR-047: viewing a gamercard is not a moderation action and must not need
        // PlatformCapabilities.Moderation - a title with reporting turned off (or a
        // provider that has not wired it up) still owes every other player a way to
        // open someone's profile from a name-bearing surface. This used to be nested
        // inside CanReport, which meant a title with no moderation capability had no
        // way to view a gamercard at all.
        if (CanViewProfile(context))
        {
            _actions.AddButton("View Profile", () => OnProfile(context));
        }

        _actions.AddButton("Cancel", () => OnCancel(context));

        if (_actions.Rows.Count == 1)
        {
            _statusText = "There is nothing to do for this player.";
        }

        _actions.FocusFirst();
    }

    /// <summary>
    /// Reason picker, replacing the second scene the GDScript built when
    /// <c>_reporting</c> went true. Backing out returns to <see cref="BuildActions"/>
    /// rather than closing outright, so a mis-picked "Report" is one Cancel away from
    /// being undone - exactly as <c>_on_cancel</c>'s <c>if _reporting</c> branch did.
    /// </summary>
    private void BuildReportReasons()
    {
        _reporting = true;
        _statusText = string.Empty;
        _actions.Clear();

        AddReportReason("Communications", PlayerReportType.Communications);
        AddReportReason("Cheating", PlayerReportType.Cheating);
        AddReportReason("Unsporting Behaviour", PlayerReportType.UnsportingBehavior);
        AddReportReason("Something Else", PlayerReportType.Other);
        _actions.AddButton("Cancel", () => { _reporting = false; BuildActionsFromCancel(); });

        _actions.FocusFirst();
    }

    private void BuildActionsFromCancel()
    {
        // Rebuilding needs a UiContext, which this local callback was not handed one
        // of; every caller of BuildActions above always has one in scope by construction
        // (Enter, OnMute's continuation), so this indirection exists solely to give the
        // Cancel row inside BuildReportReasons somewhere legal to call back into without
        // capturing a context that might already belong to a torn-down frame.
        if (Manager.Context is { } context)
        {
            BuildActions(context);
        }
    }

    private void AddReportReason(string label, PlayerReportType type)
        => _actions.AddButton(label, () => _ = SubmitReportAsync(type));

    private bool CanMute(UiContext context)
    {
        // Mirrors NetManager.can_mute_peer: no network, offline practice, chat globally
        // off, or the local player's own row all refuse the action outright.
        var party = context.Platform.Party;

        if (_network.IsOffline || !party.HasNetwork || !party.ChatAllowed || _peerId == _network.LocalPeerId)
        {
            return false;
        }

        // A voice restriction the platform itself imposed is not the player's to lift
        // (XR-015); the row stays informative (see RosterRow's "Restricted" text) but
        // this action list omits the toggle rather than offering one that refuses.
        if (_network.Players.TryGetValue(_peerId, out var player)
            && !string.IsNullOrEmpty(player.XboxUserId)
            && context.Platform.Privacy.GetCached(player.XboxUserId) is { AllowVoice: false })
        {
            return false;
        }

        return true;
    }

    private bool CanReport(UiContext context)
    {
        if (_network.IsOffline || _peerId == _network.LocalPeerId)
        {
            return false;
        }

        if (!context.Platform.Capabilities.HasFlag(PlatformCapabilities.Moderation))
        {
            return false;
        }

        return _network.Players.TryGetValue(_peerId, out var player)
            && !string.IsNullOrEmpty(player.XboxUserId);
    }

    /// <summary>
    /// XR-047: gated on <see cref="PlatformCapabilities.Social"/> - the capability the
    /// profile lookup itself belongs to - rather than <see cref="PlatformCapabilities.Moderation"/>,
    /// which only <see cref="CanReport"/> should ever consult.
    /// </summary>
    private bool CanViewProfile(UiContext context)
    {
        if (_network.IsOffline || _peerId == _network.LocalPeerId)
        {
            return false;
        }

        if (!context.Platform.Capabilities.HasFlag(PlatformCapabilities.Social))
        {
            return false;
        }

        return _network.Players.TryGetValue(_peerId, out var player)
            && !string.IsNullOrEmpty(player.XboxUserId);
    }

    private void OnMute(UiContext context)
    {
        // Divergence: IPartyService.SetPeerMuted is synchronous - there is no round
        // trip to guard here the way NetManager.toggle_peer_mute's `await
        // party.set_peer_muted(...)` had, so the GDScript's "stand the list down while
        // this is in flight" step has nothing left to protect and is not reproduced.
        var party = context.Platform.Party;
        party.SetPeerMuted(_peerId, !party.IsPeerMuted(_peerId));
        Close(context);
    }

    private async Task SubmitReportAsync(PlayerReportType type)
    {
        if (_pendingCall is not null || Manager.Context is not { } context)
        {
            return;
        }

        _actions.Clear();
        _statusText = "Sending report\u2026";

        if (!_network.Players.TryGetValue(_peerId, out var player) || string.IsNullOrEmpty(player.XboxUserId))
        {
            _statusText = "That report couldn't be sent right now.";
            return;
        }

        var task = context.Platform.Moderation.ReportPlayerAsync(player.XboxUserId, type);
        _pendingCall = task;
        var result = await task;
        _pendingCall = null;

        if (_closed)
        {
            return;
        }

        // One-shot: a second report of the same thing adds nothing, so the list is
        // replaced by a Close row rather than left open to invite one.
        _statusText = result.Succeeded
            ? "Report sent. Thanks for helping keep Xbox safe."
            : "That report couldn't be sent right now.";
        _actions.AddButton("Close", () => Close(context));
        _actions.FocusFirst();
    }

    private void OnProfile(UiContext context)
    {
        if (!_network.Players.TryGetValue(_peerId, out var player) || string.IsNullOrEmpty(player.XboxUserId))
        {
            return;
        }

        // The system card is modal on a real platform, so this closes behind it rather
        // than waiting underneath for a result nothing here consumes. Deliberately not
        // awaited: Close() below tears this screen down immediately after.
        _ = context.Platform.Moderation.ShowProfileCardAsync(player.XboxUserId);
        Close(context);
    }

    private void OnCancel(UiContext context)
    {
        if (_reporting)
        {
            _reporting = false;
            BuildActions(context);
            return;
        }

        Close(context);
    }

    private void Close(UiContext context)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        Manager.Pop();
    }

    private static Rectangle PanelBounds(UiContext context) => new(
        context.Screen.Center.X - 340,
        context.Screen.Center.Y - 260,
        680,
        520);

    private static Rectangle ActionArea(UiContext context)
    {
        var panel = PanelBounds(context);
        return new Rectangle(panel.X + 40, panel.Y + 150, panel.Width - 80, panel.Height - 190);
    }
}
