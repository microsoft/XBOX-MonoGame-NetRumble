using Microsoft.Xna.Framework;

namespace NetRumble.Game.UI;

/// <summary>
/// Stack-based screen navigation, ported from <c>scripts/autoload/screen_manager.gd</c> and
/// <c>Game/Managers/ScreenManager.cpp</c>.
/// </summary>
/// <remarks>
/// <para>
/// Only the top screen updates and receives input. Screens below stay visible only while
/// every screen above them is flagged <see cref="Screen.IsPopup"/>, which is what lets the
/// dialog box and the in-game menu float over live gameplay.
/// </para>
/// <para>
/// <b>Mutation during traversal is the hazard here.</b> Screens routinely push or pop from
/// inside their own update - a dialog's accept handler calling <see cref="ReplaceAll"/> is
/// the common case. Update and draw therefore snapshot the stack before walking it, and
/// <see cref="ShowDialog"/> takes the dialog off the stack before announcing its result, for
/// exactly the reason the GDScript documented: the caller's continuation runs inline and
/// commonly replaces the whole stack, so popping afterwards would discard it.
/// </para>
/// </remarks>
public sealed class ScreenManager
{
    private readonly List<Screen> _stack = [];
    private readonly List<Screen> _scratch = [];

    /// <summary>Raised after a screen is pushed.</summary>
    public event Action<Screen>? ScreenPushed;

    /// <summary>Raised after a screen is popped.</summary>
    public event Action<Screen>? ScreenPopped;

    public int StackSize => _stack.Count;

    public Screen? Current => _stack.Count == 0 ? null : _stack[^1];

    /// <summary>The context handed to screens; set once by the game.</summary>
    public UiContext Context { get; set; } = null!;

    /// <summary>Pushes a screen and returns it.</summary>
    public T Push<T>(T screen, object? payload = null)
        where T : Screen
    {
        screen.Manager = this;
        screen.Configure(payload);

        var previous = Current;
        _stack.Add(screen);

        RefreshActivation();
        previous?.OnCovered();

        screen.Enter(Context);

        // The accept press that opened this screen must not also activate its default row.
        Context.Input.ConsumeAll();

        ScreenPushed?.Invoke(screen);
        return screen;
    }

    /// <summary>Removes the top screen. Returns false when the stack was already empty.</summary>
    public bool Pop()
    {
        if (_stack.Count == 0)
        {
            return false;
        }

        var screen = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        screen.Exit();
        ScreenPopped?.Invoke(screen);

        RefreshActivation();
        Current?.OnRevealed();
        Context.Input.ConsumeAll();
        return true;
    }

    /// <summary>Clears the stack and pushes <paramref name="screen"/> as the only entry.</summary>
    public T ReplaceAll<T>(T screen, object? payload = null)
        where T : Screen
    {
        Clear();
        return Push(screen, payload);
    }

    public void Clear()
    {
        for (var i = _stack.Count - 1; i >= 0; i--)
        {
            _stack[i].Exit();
        }

        _stack.Clear();
    }

    /// <summary>
    /// Pops until a screen of type <typeparamref name="T"/> is on top, leaving the stack alone
    /// when none is present.
    /// </summary>
    public void PopTo<T>()
        where T : Screen
    {
        for (var i = _stack.Count - 1; i >= 0; i--)
        {
            if (_stack[i] is T)
            {
                while (_stack.Count > i + 1)
                {
                    Pop();
                }

                return;
            }
        }
    }

    /// <summary>The topmost screen of a given type, or null.</summary>
    public T? Find<T>()
        where T : class
    {
        for (var i = _stack.Count - 1; i >= 0; i--)
        {
            if (_stack[i] is T match)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>
    /// Shows a modal message. <paramref name="onDismissed"/> receives true when the user
    /// accepted and false when they cancelled.
    /// </summary>
    /// <remarks>
    /// A callback rather than the GDScript's <c>await dialog.dismissed</c>. An awaited
    /// <c>Task</c> would resume on the thread pool unless a synchronisation context were
    /// installed, and every one of these continuations touches the screen stack - which is
    /// main-thread-only. The callback runs inline on the update thread, which is what the
    /// GDScript's coroutine did too.
    /// </remarks>
    /// <param name="extraLabel">
    /// Optional third row, shown above OK. Added for XR-047, where the end-of-match
    /// standings dialog needs a route to the gamercards of the players it just named
    /// without becoming a screen of its own.
    /// </param>
    /// <param name="onExtra">
    /// What the extra row does. It does not dismiss the dialog - the caller decides
    /// whether to leave it up underneath whatever it pushes.
    /// </param>
    public DialogBox ShowDialog(
        string title,
        string message,
        DialogSeverity severity = DialogSeverity.Default,
        bool showCancel = false,
        Action<bool>? onDismissed = null,
        string extraLabel = "",
        Action? onExtra = null)
        => Push(new DialogBox(title, message, severity, showCancel, onDismissed, extraLabel, onExtra));

    /// <summary>Updates and routes input to the active screen only.</summary>
    public void Update(UiContext context)
    {
        if (Current is not { } top)
        {
            return;
        }

        top.Update(context);

        foreach (var action in Enum.GetValues<MenuAction>())
        {
            if (action == MenuAction.None || !context.Input.Pressed(action))
            {
                continue;
            }

            // The stack can change under us mid-dispatch; stop as soon as it does so the new
            // top screen does not also see this frame's actions.
            if (top.HandleAction(context, action) || !ReferenceEquals(Current, top))
            {
                break;
            }
        }
    }

    /// <summary>Draws every visible screen, bottom-up.</summary>
    public void Draw(UiContext context)
    {
        CollectVisible();

        foreach (var screen in _scratch)
        {
            screen.Draw(context);
        }
    }

    /// <summary>
    /// Runs the pre-UI world pass for every visible screen, so gameplay stays drawn
    /// beneath a popup.
    /// </summary>
    public void DrawWorld(UiContext context, float renderScale, int viewportWidth, int viewportHeight)
    {
        CollectVisible();

        foreach (var screen in _scratch)
        {
            screen.DrawWorld(context, renderScale, viewportWidth, viewportHeight);
        }
    }

    /// <summary>
    /// Fills <see cref="_scratch"/> with the visible slice: the top screen, plus everything
    /// beneath an unbroken run of popups.
    /// </summary>
    private void CollectVisible()
    {
        _scratch.Clear();

        var visibleFrom = _stack.Count - 1;

        for (var i = _stack.Count - 1; i > 0; i--)
        {
            if (!_stack[i].IsPopup)
            {
                break;
            }

            visibleFrom = i - 1;
        }

        for (var i = Math.Max(visibleFrom, 0); i < _stack.Count; i++)
        {
            _scratch.Add(_stack[i]);
        }
    }

    private void RefreshActivation()
    {
        for (var i = 0; i < _stack.Count; i++)
        {
            _stack[i].SetActive(i == _stack.Count - 1);
        }
    }
}

/// <summary>Which title-bar style a dialog uses, from the theme's three variations.</summary>
public enum DialogSeverity
{
    Default,
    Warning,
    Error,
}

/// <summary>
/// Ports <c>scripts/ui/dialog_box.gd</c> / <c>Game/UI/DialogBoxUIElement</c>: a modal popup
/// with a severity-coloured title bar, a wrapped message and one or two buttons.
/// </summary>
public sealed class DialogBox : Screen
{
    /// <summary>Title bar height, from <c>dialog_box.tscn</c>.</summary>
    private const int TitleBarHeight = 84;

    /// <summary>Panel top to the first message line's centre.</summary>
    private const int MessageTopPadding = 48;

    /// <summary>Clear space between the last message line and the buttons.</summary>
    private const int MessageBottomPadding = 32;

    /// <summary>Height of one button row, from <c>NRMenuList._ROW_HEIGHT</c>.</summary>
    private const int ButtonRowHeight = UiTheme.RowHeight;

    /// <summary>Gap between the OK and Cancel rows.</summary>
    private const int ButtonRowSeparation = 16;

    /// <summary>Space below the button block, inside the panel.</summary>
    private const int ButtonAreaBottomMargin = 20;

    private const int PanelWidth = 920;

    /// <summary>The source's fixed panel size, now the floor rather than the whole story.</summary>
    private const int MinPanelHeight = 460;

    /// <summary>Margin either side of the message text.</summary>
    private const int MessageSideMargin = 48;

    private readonly string _title;
    private readonly string _message;
    private readonly DialogSeverity _severity;
    private readonly bool _showCancel;
    private readonly Action<bool>? _onDismissed;
    private readonly string _extraLabel;
    private readonly Action? _onExtra;
    private readonly Widgets.MenuList _buttons = new();

    private List<string>? _lines;
    private bool _dismissing;

    public DialogBox(
        string title,
        string message,
        DialogSeverity severity,
        bool showCancel,
        Action<bool>? onDismissed,
        string extraLabel = "",
        Action? onExtra = null)
    {
        _title = title;
        _message = message;
        _severity = severity;
        _showCancel = showCancel;
        _onDismissed = onDismissed;
        _extraLabel = extraLabel;
        _onExtra = onExtra;

        IsPopup = true;

        // Back is handled explicitly below, so it maps to a cancel/accept decision rather
        // than a plain pop.
        AllowBack = false;
    }

    public override void Enter(UiContext context)
    {
        _buttons.RowSeparation = ButtonRowSeparation;

        // Above OK, because OK is what tears the dialog down: a player who wants the
        // extra action should not have to travel past the row that ends the dialog to
        // reach it.
        if (_extraLabel.Length > 0 && _onExtra is not null)
        {
            _buttons.AddButton(_extraLabel, _onExtra);
        }

        _buttons.AddButton("OK", () => Dismiss(true));

        if (_showCancel)
        {
            _buttons.AddButton("Cancel", () => Dismiss(false));
        }

        // Here as well as in Update, because the dialog is drawn on the frame it is
        // pushed but not updated until the next one.
        _buttons.Bounds = ButtonArea(context);
        _buttons.FocusFirst();
    }

    public override void Update(UiContext context)
    {
        _buttons.Bounds = ButtonArea(context);
        _buttons.Update(context);
    }

    public override bool HandleAction(UiContext context, MenuAction action)
    {
        if (action == MenuAction.Back)
        {
            Dismiss(!_showCancel);
            return true;
        }

        return _buttons.HandleAction(context, action);
    }

    public override void Draw(UiContext context)
    {
        var panel = PanelBounds(context);

        // Scrim: the popup keeps the screen beneath visible, so it needs to be pushed back.
        context.Theme.Fill(context.Batch, context.Screen, Color.FromNonPremultiplied(0, 0, 0, 140));
        context.Theme.Box(context.Batch, panel, UiTheme.DialogPanel, UiTheme.DialogBorder);

        var titleBar = new Rectangle(panel.X, panel.Y, panel.Width, 84);
        var (fill, border) = _severity switch
        {
            DialogSeverity.Error => (UiTheme.DialogTitleError, UiTheme.DialogBorderError),
            DialogSeverity.Warning => (UiTheme.DialogTitleWarning, UiTheme.Accent),
            _ => (UiTheme.DialogTitle, UiTheme.Accent),
        };

        context.Theme.Box(context.Batch, titleBar, fill, border);
        UiTheme.TextCentre(
            context.Batch,
            context.Theme.Subtitle,
            _title,
            new Vector2(titleBar.Center.X, titleBar.Center.Y),
            UiTheme.Text);

        var lines = Lines(context);
        var y = titleBar.Bottom + (float)MessageTopPadding;
        var lineHeight = LineHeight(context);

        // The panel is sized to the message, so this limit only bites for a message too
        // long for the screen - and then it stops the text short of the buttons rather
        // than letting it run over them, which is the failure it exists to prevent.
        var messageLimit = ButtonArea(context).Y - MessageBottomPadding;

        foreach (var line in lines)
        {
            if (y + (lineHeight / 2f) > messageLimit)
            {
                break;
            }

            UiTheme.TextCentre(
                context.Batch, context.Theme.Button, line, new Vector2(panel.Center.X, y), UiTheme.Text);
            y += lineHeight;
        }

        _buttons.Draw(context);
    }

    /// <summary>
    /// Wraps the message once. The width and font never change for a given dialog, so the
    /// result is cached: both the panel height and the draw depend on the line count, and
    /// measuring it twice per frame to reach the same answer would be waste.
    /// </summary>
    private List<string> Lines(UiContext context)
        => _lines ??= UiTheme.Wrap(context.Theme.Button, _message, PanelWidth - (MessageSideMargin * 2));

    private static float LineHeight(UiContext context) => context.Theme.Button.LineSpacing + 6;

    /// <summary>
    /// The panel, grown to fit its message.
    /// </summary>
    /// <remarks>
    /// <c>dialog_box.tscn</c> was a fixed 920x460 because every message the source put in
    /// one was a sentence or two. The end-of-match standings are not: a four-player result
    /// is four rows, and a last-player-standing result adds two more, which a fixed panel
    /// drew straight through the OK button. Sizing to content keeps the button block
    /// anchored under the text instead of behind it, and the original height stays as the
    /// floor so every short dialog looks exactly as it did.
    /// </remarks>
    private Rectangle PanelBounds(UiContext context)
    {
        var content = TitleBarHeight
            + MessageTopPadding
            + (int)MathF.Ceiling(Lines(context).Count * LineHeight(context))
            + MessageBottomPadding
            + ButtonAreaHeight
            + ButtonAreaBottomMargin;

        // A message long enough to need more than the screen is clipped rather than
        // drawn off the top and bottom edges; see Draw, which stops at the button block.
        var height = Math.Clamp(content, MinPanelHeight, context.Screen.Height - 80);

        return new Rectangle(
            context.Screen.Center.X - (PanelWidth / 2),
            context.Screen.Center.Y - (height / 2),
            PanelWidth,
            height);
    }

    /// <summary>
    /// Exactly the rows this dialog has, so a one-button dialog does not reserve - and
    /// leave visibly empty - the room a two-button one needs.
    /// </summary>
    private int ButtonAreaHeight
    {
        get
        {
            var rows = 1
                + (_showCancel ? 1 : 0)
                + (_extraLabel.Length > 0 && _onExtra is not null ? 1 : 0);

            return (ButtonRowHeight * rows) + (ButtonRowSeparation * (rows - 1));
        }
    }

    private Rectangle ButtonArea(UiContext context)
    {
        var panel = PanelBounds(context);

        return new Rectangle(
            panel.Center.X - 180,
            panel.Bottom - ButtonAreaHeight - ButtonAreaBottomMargin,
            360,
            ButtonAreaHeight);
    }

    /// <summary>
    /// Takes the dialog off the stack <em>before</em> announcing the result. The callback
    /// commonly pushes or replaces screens, and popping afterwards would discard whatever it
    /// just did - the GDScript hit exactly this with the lobby's Leave button.
    /// </summary>
    private void Dismiss(bool accepted)
    {
        if (_dismissing)
        {
            return;
        }

        _dismissing = true;
        Manager.Pop();
        _onDismissed?.Invoke(accepted);
    }
}
