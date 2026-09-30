using Microsoft.Xna.Framework;
using NetRumble.Game.UI.Widgets;

namespace NetRumble.Game.UI;

/// <summary>
/// Ports <c>scripts/ui/screens/screen.gd</c> - itself <c>Game/Screens/Screen</c> plus
/// <c>BaseMenuScreen</c>.
/// </summary>
/// <remarks>
/// Where the C++ <c>Screen</c> owned a <c>UIElement</c> tree and a <c>ScreenManager</c>
/// pointer, and the Godot screen was a full-rect <c>Control</c>, this is a plain object the
/// manager drives. The lifecycle hooks are the same four the manager called:
/// <see cref="Configure"/> before entry, <see cref="OnCovered"/>, <see cref="OnRevealed"/>
/// and <see cref="SetActive"/>.
/// </remarks>
public abstract class Screen
{
    /// <summary>
    /// Keeps the screen beneath this one visible, as <c>is_popup</c> did. The dialog box and
    /// the in-game menu are the two that set it.
    /// </summary>
    public bool IsPopup { get; protected set; }

    /// <summary>True while this is the top of the stack and therefore receives input.</summary>
    public bool IsActive { get; private set; } = true;

    /// <summary>False for screens where back must be handled explicitly, like the dialog.</summary>
    public bool AllowBack { get; protected set; } = true;

    /// <summary>Set by the manager as the screen is pushed.</summary>
    public ScreenManager Manager { get; internal set; } = null!;

    /// <summary>Called before the screen enters the stack, so it can initialise from a payload.</summary>
    public virtual void Configure(object? payload)
    {
    }

    /// <summary>Called once the screen is on the stack and has a context to build against.</summary>
    public virtual void Enter(UiContext context)
    {
    }

    /// <summary>Called when another screen is pushed on top of this one.</summary>
    public virtual void OnCovered()
    {
    }

    /// <summary>Called when this becomes the top screen again.</summary>
    public virtual void OnRevealed()
    {
    }

    /// <summary>Called as the screen leaves the stack for good.</summary>
    public virtual void Exit()
    {
    }

    internal void SetActive(bool active) => IsActive = active;

    /// <summary>
    /// Frame update. Only called for the active screen, matching Godot disabling
    /// <c>process_mode</c> on covered screens.
    /// </summary>
    public virtual void Update(UiContext context)
    {
    }

    public abstract void Draw(UiContext context);

    /// <summary>
    /// Draws anything that needs its own blend states and camera transform, before the UI
    /// batch opens. Only the gameplay screen uses this.
    /// </summary>
    public virtual void DrawWorld(UiContext context, float renderScale, int viewportWidth, int viewportHeight)
    {
    }

    /// <summary>
    /// Routes a menu action. The base handles back; overrides should call it last.
    /// </summary>
    public virtual bool HandleAction(UiContext context, MenuAction action)
    {
        if (action == MenuAction.Back && AllowBack)
        {
            OnBackPressed(context);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Default back behaviour: pop unless this is the root. Options screens override to
    /// persist settings first.
    /// </summary>
    public virtual void OnBackPressed(UiContext context)
    {
        if (Manager.StackSize > 1)
        {
            Manager.Pop();
        }
    }
}

/// <summary>
/// A screen built from the shared <c>NRMenuPanel</c>: a titled panel with a
/// <see cref="MenuList"/> inside it.
/// </summary>
/// <remarks>
/// <c>menu_panel.tscn</c> existed so screens could own their rows while the title bar and
/// panel shell stayed in one place. Same intent here: derive, override
/// <see cref="BuildRows"/>, and the frame comes for free.
/// </remarks>
public abstract class MenuScreen : Screen
{
    /// <summary>Panel size, from <c>NRMenuPanel.panel_size</c>.</summary>
    protected virtual Point PanelSize => new(960, 540);

    /// <summary>
    /// Distance from the top of the panel to the first row. The default clears the 80 px
    /// title bar; screens that draw their own art or status text above the rows - the
    /// acquire-user screen is the one that does - raise this so the list does not land on
    /// top of it.
    /// </summary>
    protected virtual int ListTopInset => 110;

    /// <summary>Space kept clear below the last row.</summary>
    protected virtual int ListBottomInset => 40;

    protected abstract string Title { get; }

    /// <summary>The panel's rows. Rebuilt by <see cref="Rebuild"/>.</summary>
    protected MenuList List { get; } = new();

    /// <summary>Populates <see cref="List"/>. Called on entry and by <see cref="Rebuild"/>.</summary>
    protected abstract void BuildRows(UiContext context);

    /// <summary>Draws behind the panel. The lobby uses this for its background art.</summary>
    protected virtual void DrawBackground(UiContext context)
    {
    }

    /// <summary>Draws over the panel, for screens with extra chrome.</summary>
    protected virtual void DrawOverlay(UiContext context)
    {
    }

    public override void Enter(UiContext context) => Rebuild(context);

    /// <summary>
    /// Clears and repopulates the rows, restoring the focused index where it still exists.
    /// </summary>
    /// <remarks>
    /// Preserving the index matters because the lobby rebuilds its list on every roster
    /// change, which on a busy lobby is several times a second; without this the focus would
    /// jump to the top under the player's hands.
    /// </remarks>
    protected void Rebuild(UiContext context)
    {
        var previous = List.FocusIndex;
        var panel = PanelBounds(context);

        List.Clear();

        // Inset below the title bar, with a margin all round.
        List.Bounds = new Rectangle(
            panel.X + 40,
            panel.Y + ListTopInset,
            panel.Width - 80,
            panel.Height - ListTopInset - ListBottomInset);

        BuildRows(context);

        if (previous >= 0 && previous < List.Rows.Count)
        {
            List.FocusRow(previous);
        }
        else
        {
            List.FocusFirst();
        }
    }

    protected Rectangle PanelBounds(UiContext context) => new(
        context.Screen.Center.X - (PanelSize.X / 2),
        context.Screen.Center.Y - (PanelSize.Y / 2),
        PanelSize.X,
        PanelSize.Y);

    public override void Update(UiContext context) => List.Update(context);

    public override bool HandleAction(UiContext context, MenuAction action)
        => List.HandleAction(context, action) || base.HandleAction(context, action);

    /// <summary>
    /// Whether to paint the panel slab and title bar. False for screens the source drew
    /// straight onto the background with no <c>NRMenuPanel</c> - the acquire-user screen
    /// is laid out as a full-screen composition in <c>acquire_user_screen.tscn</c>, with
    /// no panel and no title anywhere in it.
    /// </summary>
    protected virtual bool ShowChrome => true;

    public override void Draw(UiContext context)
    {
        DrawBackground(context);

        if (ShowChrome)
        {
            var panel = PanelBounds(context);
            context.Theme.Box(context.Batch, panel, UiTheme.PanelBackground, UiTheme.DialogBorder);

            var titleBar = new Rectangle(panel.X, panel.Y, panel.Width, 80);
            context.Theme.Box(context.Batch, titleBar, UiTheme.DialogTitle, UiTheme.Accent);

            UiTheme.TextCentre(
                context.Batch,
                context.Theme.Title,
                Title,
                new Vector2(titleBar.Center.X, titleBar.Center.Y),
                UiTheme.Text);
        }

        List.Draw(context);
        DrawOverlay(context);
    }
}
