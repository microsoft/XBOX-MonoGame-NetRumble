using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace NetRumble.Game.UI.Widgets;

/// <summary>
/// Base of the widget toolkit that replaces Godot's <c>Control</c> tree.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately much smaller than <c>Control</c>. The Godot UI used containers, anchors,
/// size flags and a focus graph; almost none of that is needed once the screens are hand
/// laid out, which they must be anyway because there is no scene format. What survives is
/// the part the original actually depended on: a rectangle, a visible/disabled pair, a
/// focus flag, and the ability to consume a menu action.
/// </para>
/// <para>
/// There is no parent pointer and no child list. Composition happens in
/// <see cref="MenuList"/> and in the screens, which is where the original put it too - the
/// <c>.tscn</c> hierarchies were flat lists of rows inside one panel.
/// </para>
/// </remarks>
public abstract class Widget
{
    /// <summary>Layout rectangle, in design-resolution units.</summary>
    public Rectangle Bounds { get; set; }

    public bool Visible { get; set; } = true;

    /// <summary>
    /// Godot's <c>BaseButton.disabled</c>: still drawn, still occupies a row, but skipped by
    /// focus navigation and unresponsive to activation.
    /// </summary>
    public bool Disabled { get; set; }

    /// <summary>Whether focus navigation can land here at all.</summary>
    public virtual bool Focusable => false;

    /// <summary>True while this is the focused row of its list.</summary>
    public bool HasFocus { get; internal set; }

    /// <summary>Preferred height when a list lays this widget out.</summary>
    public virtual int PreferredHeight => UiTheme.RowHeight;

    /// <summary>True when focus is allowed to land here right now.</summary>
    public bool CanFocus => Focusable && Visible && !Disabled;

    public virtual void Update(UiContext context)
    {
    }

    public abstract void Draw(UiContext context);

    /// <summary>
    /// Offers an action to the widget. Returning true stops the list and screen from also
    /// acting on it, which is how a spinner keeps left/right to itself.
    /// </summary>
    public virtual bool HandleAction(UiContext context, MenuAction action) => false;

    /// <summary>Called as focus arrives, for the <c>MenuScroll</c> feedback.</summary>
    public virtual void OnFocusEntered(UiContext context)
    {
    }

    /// <summary>
    /// True when a pointer at <paramref name="position"/> (design units) is over this
    /// widget and the widget is in a state to respond.
    /// </summary>
    public virtual bool HitTest(Point position)
        => Visible && !Disabled && Bounds.Contains(position);

    /// <summary>
    /// Handles a left click already known to be inside <see cref="Bounds"/>. The default
    /// is the pointer equivalent of Accept, which is what every focusable row wants;
    /// widgets with sub-regions - the spinner's arrows - override to use the position.
    /// </summary>
    public virtual bool HandleClick(UiContext context, Point position)
        => HandleAction(context, MenuAction.Accept);
}

/// <summary>
/// A non-focusable run of text. Covers Godot's <c>Label</c> plus the
/// <c>SectionHeader</c> and note rows <c>NRMenuList</c> added.
/// </summary>
public sealed class Label : Widget
{
    public Label(string text, SpriteFont? font = null)
    {
        Text = text;
        Font = font;
    }

    public string Text { get; set; }

    /// <summary>Null falls back to the theme body font at draw time.</summary>
    public SpriteFont? Font { get; set; }

    public Color Color { get; set; } = UiTheme.Text;

    public TextAlign Align { get; set; } = TextAlign.Left;

    public override int PreferredHeight { get; } = UiTheme.RowHeight;

    public override void Draw(UiContext context)
    {
        if (!Visible)
        {
            return;
        }

        var font = Font ?? context.Theme.Body;
        var midY = Bounds.Y + (Bounds.Height / 2f);

        switch (Align)
        {
            case TextAlign.Centre:
                UiTheme.TextCentre(
                    context.Batch, font, Text, new Vector2(Bounds.Center.X, midY), Color);
                break;
            case TextAlign.Right:
                UiTheme.TextRight(context.Batch, font, Text, new Vector2(Bounds.Right, midY), Color);
                break;
            default:
                UiTheme.TextLeft(context.Batch, font, Text, new Vector2(Bounds.X, midY), Color);
                break;
        }
    }
}

public enum TextAlign
{
    Left,
    Centre,
    Right,
}
