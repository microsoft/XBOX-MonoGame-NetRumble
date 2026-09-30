using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using NetRumble.Core;
using NetRumble.Game.UI.Widgets;
using NetRumble.Platform;

namespace NetRumble.Game.UI.Elements;

/// <summary>
/// One player entry in the lobby roster, ported from
/// <c>scripts/ui/elements/nr_roster_row.gd</c>, itself
/// <c>Game/UI/Lobby/RosterUIElement</c>.
/// </summary>
/// <remarks>
/// <para>
/// The GDScript's row was a 520x61 sheared <c>Control</c> instantiated eight times up
/// front and re-bound rather than recreated as players join, leave or ready up. That
/// matters more here than it did there: this row is a <see cref="Widget"/> added to a
/// <see cref="MenuList"/>, and the list's focus-preservation contract (see
/// <c>MenuScreen.Rebuild</c>) only works if the same row instance keeps occupying the
/// same slot across a rebuild - a fresh <see cref="RosterRow"/> per refresh would lose
/// <see cref="Widget.HasFocus"/> exactly the way a fresh <c>Button</c> would.
/// </para>
/// <para>
/// <b>Divergence:</b> the original never showed a score on this row - only the
/// ship silhouette, the ready ring, the nameplate and the microphone icon. A small
/// right-aligned score readout is added here once a player is <see cref="PlayerState.InGame"/>,
/// because the task asked for it explicitly; it draws in the disabled-text colour so it
/// reads as auxiliary detail rather than competing with the original's layout.
/// </para>
/// </remarks>
public sealed class RosterRow : Widget
{
    private static readonly Color NeutralTint = Color.FromNonPremultiplied(189, 199, 199, 168);
    private static readonly Color MutedTint = Color.FromNonPremultiplied(255, 64, 64, 217);

    private PlayerState? _state;
    private bool _invitable;
    private bool _actionable;
    private bool _muted;
    private bool _restricted;
    private ChatIndicator _chatIndicator = ChatIndicator.None;

    /// <summary>An empty slot was activated, asking for the platform invite UI (XR-064).</summary>
    public event Action? InviteRequested;

    /// <summary>An occupied slot was activated, asking for that player's actions overlay.</summary>
    public event Action<int>? ActionsRequested;

    /// <summary>
    /// Focusable only while there is something to do here - an invitable empty slot or
    /// an actionable occupied one - exactly as <c>_apply_interactivity</c> gated
    /// <c>focus_mode</c> in the original. Everything else stays the inert placeholder.
    /// </summary>
    public override bool Focusable => _state is null ? _invitable : _actionable;

    /// <summary>
    /// 61 px, <c>NRRosterRow</c>'s <c>custom_minimum_size</c>. The list adds a 3 px
    /// separation to reach the source's 64 px <c>_ROSTER_ROW_PITCH</c>.
    /// </summary>
    public override int PreferredHeight => 61;

    /// <summary>
    /// Binds this row to a player, or clears it back to the "Invite To Game..." slot
    /// when <paramref name="state"/> is <c>null</c>.
    /// </summary>
    public void SetState(PlayerState? state) => _state = state;

    /// <summary>Whether an empty slot can send a platform invite right now.</summary>
    public void SetInvitable(bool invitable) => _invitable = invitable;

    /// <summary>
    /// Whether activating an occupied slot opens the player actions overlay, and the
    /// mute state to show while it is closed. The lobby resolves all three from the
    /// platform's mute list and privacy verdicts; this row only reflects them.
    /// </summary>
    public void SetActionsContext(bool actionable, bool muted, bool restricted = false)
    {
        _actionable = actionable;
        _muted = muted;
        _restricted = restricted;
    }

    /// <summary>
    /// Microphone state to draw. Godot's mic icon read <c>PartyService.ChatIndicator</c>
    /// through <c>NetManager.chat_indicator_for</c>'s entity-key lookup; this port's
    /// <see cref="IPartyService.GetChatIndicator"/> already takes a peer id directly, so
    /// the lobby screen resolves it and hands the row the finished value.
    /// </summary>
    public void SetChatIndicator(ChatIndicator indicator) => _chatIndicator = indicator;

    public override void OnFocusEntered(UiContext context)
    {
        if (CanFocus)
        {
            context.Audio.Play("MenuScroll");
        }
    }

    public override bool HandleAction(UiContext context, MenuAction action)
    {
        if (action != MenuAction.Accept || Disabled || !Visible)
        {
            return false;
        }

        if (_state is null)
        {
            if (!_invitable)
            {
                return false;
            }

            context.Audio.Play("MenuSelect");
            InviteRequested?.Invoke();
            return true;
        }

        if (!_actionable)
        {
            return false;
        }

        context.Audio.Play("MenuSelect");
        ActionsRequested?.Invoke(_state.PeerId);
        return true;
    }

    public override void Draw(UiContext context)
    {
        if (!Visible)
        {
            return;
        }

        var batch = context.Batch;
        var theme = context.Theme;

        // nr_roster_row.tscn backs every row with the LobbyBackground_Roster texture at
        // the neutral modulate, not a flat button slab - the texture is authored at
        // exactly one row (520x61), which is why it is drawn per row rather than
        // stretched down the whole column.
        batch.Draw(context.Assets.Texture("Lobby_BackgroundRoster"), Bounds, UiTheme.PanelTexture);

        if (HasFocus)
        {
            theme.Stroke(batch, Bounds, UiTheme.Accent);
        }

        if (_state is not { } state)
        {
            var alpha = (_invitable ? 255 : 128);
            var textColour = Color.FromNonPremultiplied(255, 255, 255, alpha);
            UiTheme.TextLeft(
                batch, theme.Body, "Invite To Game...", new Vector2(Bounds.X + 119, Bounds.Center.Y), textColour);
            return;
        }

        // Silhouette (5.5,5)-(56.5,56), ready ring (62,9.5)-(104,51.5) and the nameplate
        // at x=119, all from nr_roster_row.tscn.
        var iconRect = new Rectangle(Bounds.X + 6, Bounds.Y + 5, 51, 51);
        batch.Draw(
            context.Assets.ShipTexture(state.ShipStyleId, "Silhouette"),
            iconRect,
            new Color(state.Color.ToRgba()));

        var ringRect = new Rectangle(Bounds.X + 62, Bounds.Y + 10, 42, 42);
        batch.Draw(context.Assets.Texture("ReadyUp_RingBackground"), ringRect, Color.White);
        batch.Draw(context.Assets.Texture("ReadyUp_RingOutline"), ringRect, UiTheme.Neutral);

        if (state.IsReady)
        {
            batch.Draw(context.Assets.Texture("ReadyUp_Checkmark"), ringRect, UiTheme.Accent);
        }

        var name = state.DisplayName + (state.IsLocalPlayer ? " (You)" : string.Empty);
        UiTheme.TextLeft(batch, theme.Body, name, new Vector2(Bounds.X + 119, Bounds.Center.Y), UiTheme.Text);

        // Divergence: see the type remarks. The original had no score column at all.
        if (state.InGame)
        {
            UiTheme.TextRight(
                batch,
                theme.Small,
                state.Score.ToString(),
                new Vector2(Bounds.Right - 96, Bounds.Center.Y),
                UiTheme.TextDisabled);
        }

        DrawMic(context, batch);

        if (!_restricted)
        {
            return;
        }

        // A platform-forced mute is not the player's to lift, so it is worth saying why
        // a row the player cannot un-mute is muted anyway - an unexplained permanent
        // mute otherwise reads as a bug rather than a moderation outcome (XR-015).
        UiTheme.TextRight(
            batch, theme.Small, "Restricted", new Vector2(Bounds.Right - 16, Bounds.Center.Y), MutedTint);
    }

    private void DrawMic(UiContext context, SpriteBatch batch)
    {
        // The Mic node sits outside the row to its left, (-62,6)-(-12,56).
        var micRect = new Rectangle(Bounds.X - 62, Bounds.Y + 6, 50, 50);

        switch (_chatIndicator)
        {
            case ChatIndicator.Available:
                batch.Draw(context.Assets.Texture("Microphone_Available"), micRect, NeutralTint);
                break;
            case ChatIndicator.Talking:
                batch.Draw(context.Assets.Texture("Microphone_Talking"), micRect, NeutralTint);
                break;
            case ChatIndicator.Muted:
                batch.Draw(context.Assets.Texture("Microphone_Muted"), micRect, MutedTint);
                break;
            case ChatIndicator.None:
            default:
                break;
        }

        // _muted currently only feeds the "Unmute Player"/"Mute Player" wording in the
        // player actions overlay - the icon above is driven entirely by the platform's
        // own indicator, matching the original where NetManager, not this row, decided
        // which icon to show.
        _ = _muted;
    }
}
