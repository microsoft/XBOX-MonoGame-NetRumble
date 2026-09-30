using Microsoft.Xna.Framework;

namespace NetRumble.Game.UI.Widgets;

/// <summary>
/// Ports <c>scripts/ui/elements/nr_button.gd</c> and, through it,
/// <c>Game/UI/UIElements/ButtonUIElement</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The two visual variations are both real.</b> Godot's theme had a boxed
/// <c>NRMenuButton</c> and a transparent one, and <c>NRMenuList</c> documents why: the C++
/// <c>MainMenuScreen</c> never set <c>ButtonUIConfig::backgroundTexture</c>, so its rows
/// drew as bare text, while every <c>BaseMenuScreen</c>-derived screen passed a blank
/// texture and got the grey box. <see cref="Boxed"/> preserves that inconsistency rather
/// than tidying it away.
/// </para>
/// <para>
/// The audio feedback is the other half of the original: <c>MenuScroll</c> as focus
/// arrives, <c>MenuSelect</c> on activation, and neither while disabled.
/// </para>
/// </remarks>
public sealed class Button : Widget
{
    private float _pressedFor;

    public Button(string text, Action? onSelected = null)
    {
        Text = text;
        Selected = onSelected;
    }

    public string Text { get; set; }

    /// <summary>Invoked on activation. Null makes the row focusable but inert.</summary>
    public Action? Selected { get; set; }

    /// <summary>False draws bare text, as the main menu's rows did.</summary>
    public bool Boxed { get; set; } = true;

    /// <summary>Suppresses the menu sounds, for rows that fire during a rebuild.</summary>
    public bool SoundsEnabled { get; set; } = true;

    public override bool Focusable => true;

    public override void OnFocusEntered(UiContext context)
    {
        if (SoundsEnabled && !Disabled)
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

        if (SoundsEnabled)
        {
            context.Audio.Play("MenuSelect");
        }

        // Held long enough to read as a flash, not a single frame.
        _pressedFor = 0.12f;
        Selected?.Invoke();
        return true;
    }

    public override void Update(UiContext context)
        => _pressedFor = MathF.Max(0f, _pressedFor - context.Delta);

    public override void Draw(UiContext context)
    {
        if (!Visible)
        {
            return;
        }

        var pressed = _pressedFor > 0f;

        if (Boxed)
        {
            var fill = Disabled
                ? UiTheme.ButtonDisabled
                : pressed
                    ? UiTheme.ButtonPressed
                    : HasFocus ? UiTheme.ButtonHover : UiTheme.ButtonNormal;

            // Only the focused and pressed styles carried a border in the theme.
            Color? border = !Disabled && (HasFocus || pressed) ? UiTheme.Accent : null;
            context.Theme.Box(context.Batch, Bounds, fill, border);
        }

        var colour = Disabled
            ? UiTheme.TextDisabled
            : pressed
                ? UiTheme.TextPressed
                : HasFocus ? UiTheme.Accent : UiTheme.Text;

        UiTheme.TextCentre(
            context.Batch,
            context.Theme.Button,
            Text,
            new Vector2(Bounds.Center.X, Bounds.Center.Y),
            colour);
    }
}
