using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using NetRumble.Game.UI.Widgets;
using NetRumble.Platform;

namespace NetRumble.Game.UI.Elements;

/// <summary>
/// Ports <c>scripts/ui/elements/nr_join_code_entry.gd</c> - itself
/// <c>Game/UI/JoinCodeEntryUIElement</c>: a modal overlay that captures a fixed-length
/// lobby join code.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="Screen"/> rather than a bespoke widget, on the same reasoning as
/// <see cref="DialogBox"/>: it needs its own place in the input-routing
/// stack (<see cref="Screen.IsPopup"/> so the lobby stays visible underneath, explicit
/// <c>Back</c> handling so Cancel and Back agree) and the manager already gives that
/// to any pushed <see cref="Screen"/> for free.
/// </para>
/// <para>
/// <b>The dismiss-then-notify ordering matters here too.</b> <see cref="ScreenManager.Pop"/>
/// must run before <see cref="Screen.OnBackPressed"/>'s caller sees the submitted code,
/// for the same reason <c>DialogBox.Dismiss</c> documents: the lobby's join handler
/// pushes a loading screen and this overlay must already be off the stack when it does.
/// </para>
/// <para>
/// The GDScript's cancel path had no callback at all - <c>NRJoinCodeEntry</c> simply
/// <c>queue_free()</c>'d itself, because no caller in the original ever needed to know
/// a join code entry was abandoned. That is preserved: <see cref="JoinCodeEntry"/> only
/// ever calls back on success.
/// </para>
/// </remarks>
public sealed class JoinCodeEntry : Screen
{
    /// <summary>
    /// Matches <c>PartyService.JOIN_CODE_LENGTH</c> and the five-character check in the
    /// C++ sample's <c>PlayFabManager::FindLobbyByJoinCodeAsync</c>.
    /// </summary>
    public const int JoinCodeLength = 5;

    private readonly string _title;
    private readonly Action<string> _onSubmitted;
    private readonly MenuList _buttons = new();
    private readonly SystemKeyboard _keyboard = new();

    private string _text = string.Empty;
    private Button? _okButton;
    private Button? _cancelButton;
    private GameWindow? _capturedWindow;
    private Task<string?>? _platformRequest;
    private bool _closed;

    public JoinCodeEntry(string title, Action<string> onSubmitted)
    {
        _title = title;
        _onSubmitted = onSubmitted;

        IsPopup = true;

        // Back is handled explicitly below (it must behave exactly like Cancel), so
        // the base class's plain-pop behaviour must stand down.
        AllowBack = false;
    }

    public override void Enter(UiContext context)
    {
        _buttons.RowSeparation = 16;
        _okButton = _buttons.AddButton("OK", Submit);
        _okButton.Disabled = true;
        _cancelButton = _buttons.AddButton("Cancel", Cancel);

        if (SystemKeyboard.IsPlatformKeyboardRequired(context))
        {
            // Console: the field itself never receives keystrokes. Kick the platform
            // surface off now and pick up its result in Update, mirroring the
            // GDScript's `_open_system_keyboard` call from `_ready`.
            _platformRequest = SystemKeyboard.RequestPlatformTextAsync(
                context,
                new TextEntryRequest
                {
                    Title = _title,
                    MaxLength = JoinCodeLength,
                    Scope = TextEntryScope.Alphanumeric,
                });
        }
        else
        {
            // Desktop: capture keystrokes directly. See SystemKeyboard's remarks for
            // why this branch exists at all where the GDScript's did not.
            _capturedWindow = context.Game.Window;
            _keyboard.BeginDesktopEntry(_capturedWindow, string.Empty, JoinCodeLength, char.IsLetterOrDigit);
        }

        _buttons.FocusFirst();
    }

    public override void Update(UiContext context)
    {
        if (_platformRequest is { IsCompleted: true } task)
        {
            _platformRequest = null;
            var typed = task.Result;

            // A null result is a cancel or a failure, not a value to apply - the
            // GDScript's `if text != null:` guard did the same, leaving whatever the
            // field held (here, nothing) rather than clearing it a second time.
            if (typed is not null)
            {
                SetText(typed.ToUpperInvariant());
            }

            // Reopening the platform keyboard from the field itself (the GDScript's
            // `_on_line_edit_gui_input`) has no MonoGame equivalent worth the ceremony:
            // Accept on this row would just relaunch the same async call. Instead focus
            // lands on whichever button makes sense next, exactly as the original did
            // once its await returned.
            _buttons.FocusRow(_okButton!.Disabled ? 1 : 0, context);
        }
        else if (_capturedWindow is not null)
        {
            SetText(_keyboard.Text);
        }

        _buttons.Bounds = ButtonArea(context);
        _buttons.Update(context);
    }

    public override bool HandleAction(UiContext context, MenuAction action)
    {
        if (action == MenuAction.Back)
        {
            Cancel();
            return true;
        }

        return _buttons.HandleAction(context, action);
    }

    public override void Exit() => _keyboard.EndDesktopEntry();

    public override void Draw(UiContext context)
    {
        var panel = PanelBounds(context);

        context.Theme.Fill(context.Batch, context.Screen, Color.FromNonPremultiplied(0, 0, 0, 140));
        context.Theme.Box(context.Batch, panel, UiTheme.DialogPanel, UiTheme.DialogBorder);

        var titleBar = new Rectangle(panel.X, panel.Y, panel.Width, 84);
        context.Theme.Box(context.Batch, titleBar, UiTheme.DialogTitle, UiTheme.Accent);
        UiTheme.TextCentre(
            context.Batch,
            context.Theme.Subtitle,
            _title,
            new Vector2(titleBar.Center.X, titleBar.Center.Y),
            UiTheme.Text);

        DrawCells(context, panel, titleBar.Bottom + 56);
        _buttons.Draw(context);
    }

    /// <summary>Five boxes, one per character, blank past the length typed so far.</summary>
    private void DrawCells(UiContext context, Rectangle panel, int y)
    {
        const int cellSize = 96;
        const int gap = 16;
        var totalWidth = (cellSize * JoinCodeLength) + (gap * (JoinCodeLength - 1));
        var x = panel.Center.X - (totalWidth / 2);

        for (var i = 0; i < JoinCodeLength; i++)
        {
            var cell = new Rectangle(x + (i * (cellSize + gap)), y, cellSize, cellSize);
            var filled = i < _text.Length;
            context.Theme.Box(
                context.Batch,
                cell,
                filled ? UiTheme.ButtonHover : UiTheme.ButtonNormal,
                filled ? UiTheme.Accent : UiTheme.DialogBorder);

            if (filled)
            {
                UiTheme.TextCentre(
                    context.Batch,
                    context.Theme.Title,
                    _text[i].ToString(),
                    new Vector2(cell.Center.X, cell.Center.Y),
                    UiTheme.Text);
            }
        }
    }

    private void SetText(string text)
    {
        var upper = text.ToUpperInvariant();
        _text = upper.Length > JoinCodeLength ? upper[..JoinCodeLength] : upper;

        // The GDScript disabled OK below JOIN_CODE_LENGTH after trimming whitespace;
        // there is no whitespace to trim here since the desktop filter already rejects
        // anything but letters and digits and the platform keyboard is scope-limited
        // to alphanumeric too.
        if (_okButton is not null)
        {
            _okButton.Disabled = _text.Length < JoinCodeLength;
        }
    }

    private void Submit()
    {
        if (_okButton!.Disabled)
        {
            return;
        }

        Close(() => _onSubmitted(_text));
    }

    private void Cancel() => Close(null);

    /// <summary>
    /// Pops before invoking <paramref name="onClosed"/>, for the same reason
    /// <c>DialogBox.Dismiss</c> documents: the lobby's submit handler pushes a loading
    /// screen, and popping afterwards would discard it.
    /// </summary>
    private void Close(Action? onClosed)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        Manager.Pop();
        onClosed?.Invoke();
    }

    private static Rectangle PanelBounds(UiContext context) => new(
        context.Screen.Center.X - 420,
        context.Screen.Center.Y - 260,
        840,
        460);

    private static Rectangle ButtonArea(UiContext context)
    {
        var panel = PanelBounds(context);
        return new Rectangle(panel.Center.X - 180, panel.Bottom - 150, 360, 130);
    }
}
