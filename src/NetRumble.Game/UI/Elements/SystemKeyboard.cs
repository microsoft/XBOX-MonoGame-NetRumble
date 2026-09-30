using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using NetRumble.Platform;

namespace NetRumble.Game.UI.Elements;

/// <summary>
/// Ports <c>scripts/ui/elements/nr_system_keyboard.gd</c>: the bridge between a text
/// field and whatever surface actually captures keystrokes on this machine.
/// </summary>
/// <remarks>
/// <para>
/// The GDScript existed only for its console branch. On Xbox there is no hardware
/// keyboard, so <c>NRJoinCodeEntry</c>'s <c>LineEdit</c> is untypable and every field
/// had to hand off to the GDK's <c>XGameUiShowTextEntryAsync</c> surface instead; on
/// desktop the same <c>LineEdit</c> already received keystrokes for free from Godot's
/// own <c>Control</c> focus system, so <c>NRSystemKeyboard.is_available()</c> simply
/// returned <c>false</c> and the original script did nothing else.
/// </para>
/// <para>
/// MonoGame has no equivalent free ride: there is no <c>Control</c> tree capturing
/// keystrokes for a focused widget. This class therefore has two branches, and they
/// are not equally faithful ports:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <b>Console (<see cref="IsPlatformKeyboardRequired"/> true):</b> a direct, faithful
/// port of the GDScript's only real branch - forward to
/// <see cref="IPlatformUiService.ShowTextEntryAsync"/> and let the platform own the
/// whole entry surface.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Desktop (added, not ported):</b> <see cref="BeginDesktopEntry"/> subscribes to
/// <see cref="GameWindow.TextInput"/>, which is MonoGame's character-composition-aware
/// text source - it already accounts for Shift, layout and IME composition the way a
/// raw <see cref="KeyboardState"/> scan never could, which is exactly the job Godot's
/// <c>Control</c> focus system used to do invisibly. Backspace is not a printable
/// character and Windows/DirectX report it as <c>'\b'</c> (0x08) through this event
/// rather than as a key that never fires <c>TextInput</c>, so it is special-cased here
/// rather than left to fall through the printable-character filter.
/// </description>
/// </item>
/// </list>
/// </remarks>
public sealed class SystemKeyboard
{
    private readonly System.Text.StringBuilder _buffer = new();

    private GameWindow? _window;
    private int _maxLength = 128;
    private Func<char, bool>? _filter;

    /// <summary>Current desktop-entry buffer. Meaningless until <see cref="BeginDesktopEntry"/>.</summary>
    public string Text => _buffer.ToString();

    /// <summary>
    /// True when there is no hardware keyboard to type into and entry must go through
    /// the platform's own UI - the exact condition <c>NRSystemKeyboard.is_available()</c>
    /// tested, just asked of the platform abstraction instead of a GDK feature tag.
    /// </summary>
    public static bool IsPlatformKeyboardRequired(UiContext context)
        => context.Platform.PlatformUi.RequiresVirtualKeyboard;

    /// <summary>
    /// Console branch: shows the platform's text-entry surface and returns what the
    /// player typed, or <c>null</c> when they cancelled or the call failed - mirroring
    /// the GDScript's <c>if result == null or not result.ok or result.data == null:
    /// return null</c> guard, which treated cancellation as a quiet no-op rather than
    /// an error worth a dialog.
    /// </summary>
    public static async Task<string?> RequestPlatformTextAsync(
        UiContext context, TextEntryRequest request, CancellationToken cancellationToken = default)
    {
        var result = await context.Platform.PlatformUi.ShowTextEntryAsync(request, cancellationToken);
        return result.Succeeded ? result.Value : null;
    }

    /// <summary>
    /// Desktop branch: starts capturing <see cref="GameWindow.TextInput"/> into an
    /// internal buffer seeded with <paramref name="initialText"/>.
    /// </summary>
    /// <param name="filter">
    /// Rejects a typed character outright - the join-code field uses this for its
    /// alphanumeric-only scope, matching <see cref="TextEntryScope.Alphanumeric"/>.
    /// </param>
    public void BeginDesktopEntry(GameWindow window, string initialText, int maxLength, Func<char, bool>? filter = null)
    {
        EndDesktopEntry();

        _window = window;
        _maxLength = maxLength;
        _filter = filter;
        _buffer.Clear();
        _buffer.Append(initialText.Length > maxLength ? initialText[..maxLength] : initialText);
        _window.TextInput += OnTextInput;
    }

    /// <summary>Stops capturing text input. Safe to call when entry was never started.</summary>
    public void EndDesktopEntry()
    {
        if (_window is null)
        {
            return;
        }

        _window.TextInput -= OnTextInput;
        _window = null;
    }

    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        var character = e.Character;

        // Backspace arrives as a printable-looking control character through this
        // event rather than as a key TextInput otherwise ignores, so it must be
        // handled before the control-character filter below discards it.
        if (character == '\b')
        {
            if (_buffer.Length > 0)
            {
                _buffer.Length--;
            }

            return;
        }

        if (char.IsControl(character) || _buffer.Length >= _maxLength)
        {
            return;
        }

        if (_filter is not null && !_filter(character))
        {
            return;
        }

        _buffer.Append(character);
    }
}
