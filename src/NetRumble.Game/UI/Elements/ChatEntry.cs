using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using NetRumble.Platform;

namespace NetRumble.Game.UI.Elements;

/// <summary>
/// Ports <c>scripts/ui/elements/nr_chat_entry.gd</c> (itself
/// <c>Game/UI/ChatTextEntryUIElement</c>): a modal overlay that captures a single free-text
/// chat message.
/// </summary>
/// <remarks>
/// <para>
/// The GDScript's text entry was a native <c>LineEdit</c>, backed on console by
/// <c>NRSystemKeyboard</c>. This keeps both halves of that split, through
/// <see cref="SystemKeyboard"/>: on a platform that owns text entry (every GDK build,
/// console and PC alike) the platform's own on-screen keyboard is shown, and on a
/// platform that does not, keystrokes are read off <see cref="UiInput.Keyboard"/> with
/// a hard-coded US-QWERTY map that covers what a chat line actually needs.
/// </para>
/// <para>
/// The platform branch is not optional polish: without it a controller has nothing to
/// type with, so pressing the chat action on a console opened a message box that could
/// never be filled in.
/// </para>
/// <para>
/// <b>Closing is the caller's decision, not this class's.</b> <see cref="Submitted"/>
/// fires with the typed text and the box stays open; <c>ChatTextEntryUIElement::OnOkButtonPressed</c>
/// did the same so a message the platform refused could stay on screen for a retry
/// instead of being silently discarded. The caller closes explicitly via <see cref="Close"/>
/// once it decides the send succeeded, or calls <see cref="Retry"/> to offer the
/// platform keyboard again with the refused text still in it.
/// </para>
/// </remarks>
public sealed class ChatEntry
{
    /// <summary>Godot's <c>MAX_MESSAGE_LENGTH</c>.</summary>
    public const int MaxMessageLength = 100;

    private readonly System.Text.StringBuilder _text = new();

    private Task<string?>? _platformRequest;
    private bool _usesPlatformKeyboard;

    /// <summary>Raised with the typed text when Enter is pressed, even if it is empty.</summary>
    public event Action<string>? Submitted;

    /// <summary>Raised when Escape/Back closes the box without sending.</summary>
    public event Action? Cancelled;

    /// <summary>True while the overlay is up and capturing keystrokes.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>
    /// True while the platform's own keyboard owns entry, so the caller knows this
    /// screen's own key handling is not what the player is typing into.
    /// </summary>
    public bool IsAwaitingPlatformKeyboard => _platformRequest is not null;

    public string Text => _text.ToString();

    /// <summary>
    /// Opens the box with an empty message, matching a fresh <c>nr_chat_entry.tscn</c>
    /// instance, and shows the platform keyboard when this platform owns text entry.
    /// </summary>
    public void Open(UiContext context)
    {
        IsOpen = true;
        _text.Clear();
        _usesPlatformKeyboard = SystemKeyboard.IsPlatformKeyboardRequired(context);

        if (_usesPlatformKeyboard)
        {
            BeginPlatformEntry(context);
        }
    }

    /// <summary>
    /// Offers entry again with the current text still in it, for a message the send
    /// path refused. A no-op where the player is typing directly, because the box they
    /// are typing into never went away.
    /// </summary>
    public void Retry(UiContext context)
    {
        if (!IsOpen || !_usesPlatformKeyboard || _platformRequest is not null)
        {
            return;
        }

        BeginPlatformEntry(context);
    }

    /// <summary>Closes the box. Safe to call when already closed.</summary>
    public void Close()
    {
        IsOpen = false;

        // The platform surface cannot be recalled once shown, so an outstanding request
        // is simply abandoned: Update drops a result that arrives after the box closed.
        _platformRequest = null;
    }

    /// <summary>Captures this frame's keystrokes. Does nothing while closed.</summary>
    public void Update(UiContext context)
    {
        if (!IsOpen)
        {
            return;
        }

        if (_platformRequest is { } request)
        {
            if (!request.IsCompleted)
            {
                return;
            }

            _platformRequest = null;
            var typed = request.IsCompletedSuccessfully ? request.Result : null;

            // A null result is a cancel or a failure, not a value to apply - the same
            // guard JoinCodeEntry uses on this surface. Cancelling the platform keyboard
            // dismisses the whole box, because there is nothing left on this platform
            // that could fill it in.
            if (typed is null)
            {
                Cancelled?.Invoke();
                return;
            }

            SetText(typed);
            Submitted?.Invoke(Text.Trim());
            return;
        }

        if (_usesPlatformKeyboard)
        {
            return;
        }

        var keys = context.Input.Keyboard;
        var previous = context.Input.PreviousKeyboard;

        foreach (var key in keys.GetPressedKeys())
        {
            // Edge-only: a held key must not retype every frame. Backspace and the
            // printable keys all go through this same gate, unlike UiInput's menu
            // actions, which get auto-repeat on purpose - typing wants exactly one
            // character per press, not a repeat timer.
            if (previous.IsKeyDown(key))
            {
                continue;
            }

            HandleKey(keys, key);
        }
    }

    private void BeginPlatformEntry(UiContext context)
        => _platformRequest = SystemKeyboard.RequestPlatformTextAsync(
            context,
            new TextEntryRequest
            {
                Title = "Chat Message",
                DefaultText = Text,
                MaxLength = MaxMessageLength,

                // ChatWithoutEmoji on the GDK: the platform keyboard itself then applies
                // the same policy this title's moderation pass does to the result.
                Scope = TextEntryScope.Chat,
            });

    private void SetText(string text)
    {
        _text.Clear();
        _text.Append(text.Length > MaxMessageLength ? text[..MaxMessageLength] : text);
    }

    private void HandleKey(KeyboardState keys, Keys key)
    {
        if (key == Keys.Enter)
        {
            Submitted?.Invoke(_text.ToString().Trim());
            return;
        }

        if (key is Keys.Escape)
        {
            Cancelled?.Invoke();
            return;
        }

        if (key == Keys.Back)
        {
            if (_text.Length > 0)
            {
                _text.Length -= 1;
            }

            return;
        }

        if (_text.Length >= MaxMessageLength)
        {
            return;
        }

        var shift = keys.IsKeyDown(Keys.LeftShift) || keys.IsKeyDown(Keys.RightShift);
        var character = KeyToChar(key, shift);

        if (character is not null)
        {
            _text.Append(character.Value);
        }
    }

    /// <summary>
    /// A deliberately small US-QWERTY map. Enough for a chat line; not an IME, and not
    /// locale-aware - the GDScript never had to solve this itself because <c>LineEdit</c>
    /// and the system keyboard did.
    /// </summary>
    private static char? KeyToChar(Keys key, bool shift) => key switch
    {
        >= Keys.A and <= Keys.Z => shift ? (char)key : char.ToLowerInvariant((char)key),
        >= Keys.D0 and <= Keys.D9 when !shift => (char)('0' + (key - Keys.D0)),
        Keys.D1 when shift => '!',
        Keys.D2 when shift => '@',
        Keys.D3 when shift => '#',
        Keys.D4 when shift => '$',
        Keys.D5 when shift => '%',
        Keys.D6 when shift => '^',
        Keys.D7 when shift => '&',
        Keys.D8 when shift => '*',
        Keys.D9 when shift => '(',
        Keys.D0 when shift => ')',
        Keys.Space => ' ',
        Keys.OemComma => shift ? '<' : ',',
        Keys.OemPeriod => shift ? '>' : '.',
        Keys.OemQuestion => shift ? '?' : '/',
        Keys.OemMinus => shift ? '_' : '-',
        Keys.OemPlus => shift ? '+' : '=',
        Keys.OemQuotes => shift ? '"' : '\'',
        Keys.OemSemicolon => shift ? ':' : ';',
        Keys.OemOpenBrackets => shift ? '{' : '[',
        Keys.OemCloseBrackets => shift ? '}' : ']',
        Keys.OemBackslash => shift ? '|' : '\\',
        _ => null,
    };

    /// <summary>Draws the dimmed backdrop, the panel and the caret-suffixed message.</summary>
    public void Draw(UiContext context)
    {
        if (!IsOpen)
        {
            return;
        }

        // Matches the GDScript's Dim ColorRect: (0.02, 0.02, 0.06, 0.72).
        context.Theme.Fill(context.Batch, context.Screen, Color.FromNonPremultiplied(5, 5, 15, 184));

        var panel = new Rectangle(context.Screen.Center.X - 360, context.Screen.Center.Y - 120, 720, 240);
        context.Theme.Box(context.Batch, panel, UiTheme.DialogPanel, UiTheme.DialogBorder);

        UiTheme.TextCentre(
            context.Batch,
            context.Theme.Subtitle,
            "Chat Message",
            new Vector2(panel.Center.X, panel.Y + 48),
            UiTheme.Accent);

        var fieldBounds = new Rectangle(panel.X + 40, panel.Y + 96, panel.Width - 80, 48);
        context.Theme.Box(context.Batch, fieldBounds, UiTheme.ProgressBackground, UiTheme.OverlayBorder);

        // A blinking caret is the only feedback a player gets that the field still has
        // focus, since there is nothing else on screen to draw that attention away. It
        // is wrong while the platform keyboard owns entry, though: this field is not
        // what the player is typing into then.
        var caret = !IsAwaitingPlatformKeyboard && ((int)(context.Time * 2f) % 2 == 0)
            ? "|"
            : string.Empty;
        UiTheme.TextLeft(
            context.Batch,
            context.Theme.Body,
            Text + caret,
            new Vector2(fieldBounds.X + 12, fieldBounds.Center.Y),
            UiTheme.Text);

        ButtonPrompt.DrawCentre(
            context.Batch,
            context.Assets,
            context.Theme.Small,
            IsAwaitingPlatformKeyboard ? "Use the on-screen keyboard to type your message" : context.Input.TextSubmitPrompt,
            new Vector2(panel.Center.X, panel.Bottom - 36),
            UiTheme.TextDisabled);
    }
}
