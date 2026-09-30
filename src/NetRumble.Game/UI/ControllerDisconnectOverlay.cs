using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace NetRumble.Game.UI;

/// <summary>
/// The "reconnect your controller" prompt (XR-115). Ported from
/// <c>scripts/ui/controller_disconnect_overlay.gd</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately not a dialog</b>, and the reason is the whole point of the feature: a
/// dialog is dismissed by pressing a button, and the player has nothing to press it with.
/// So this draws above the screen stack, takes no input at all, and has no focusable
/// controls - nothing about it can trap a player holding a dead pad. It comes down when
/// the controller comes back, and that is the only way it comes down.
/// </para>
/// <para>
/// <b>It pauses an offline match, and nothing else.</b> A networked match cannot stop
/// because one player unplugged something, so online play keeps running underneath and
/// the ship simply stops taking input - which is already what happens to a ship whose
/// owner is idle. Offline and practice do freeze, because there is nobody else to hold
/// up and a player with nothing to press should not be losing a match while they go and
/// find a pad. <see cref="LifecycleCoordinator.SetControllerMissing"/> raises the gate;
/// <c>MatchDirector.ConstrainFreezesSimulation</c> decides whether it bites.
/// </para>
/// <para>
/// It also survives a constrain. The platform can put the Guide up while this is showing -
/// plausibly the very thing the player is doing about it - and the prompt has to still be
/// there on the way back. Nothing here is driven by the simulation clock, so a frozen
/// match does not freeze the prompt.
/// </para>
/// </remarks>
public sealed class ControllerDisconnectOverlay
{
    private const string Message = "Please reconnect your controller to continue.";

    private static readonly Point PanelSize = new(760, 132);

    /// <summary>Whether the prompt is showing.</summary>
    public bool IsVisible { get; private set; }

    public void Show() => IsVisible = true;

    public void Hide() => IsVisible = false;

    /// <summary>
    /// Draws the prompt, if it is up. Called after the screen stack and inside the same
    /// batch, which is what puts it above everything.
    /// </summary>
    public void Draw(UiContext context)
    {
        if (!IsVisible)
        {
            return;
        }

        var theme = context.Theme;
        var screen = context.Screen;

        // Dimmed, not blacked out: the player needs to see the match they are still in
        // underneath, running or held.
        theme.Fill(context.Batch, screen, UiTheme.OverlayBackground);

        var panel = new Rectangle(
            screen.X + ((screen.Width - PanelSize.X) / 2),
            screen.Y + ((screen.Height - PanelSize.Y) / 2),
            PanelSize.X,
            PanelSize.Y);

        theme.Box(context.Batch, panel, UiTheme.DialogPanel, UiTheme.DialogBorder);

        var centre = new Vector2(panel.Center.X, panel.Y + 34);
        UiTheme.TextCentre(context.Batch, theme.PanelHeader, "Controller Disconnected", centre, UiTheme.Text);
        UiTheme.TextCentre(
            context.Batch,
            theme.Body,
            Message,
            new Vector2(panel.Center.X, panel.Y + 82),
            UiTheme.Neutral);
    }
}
