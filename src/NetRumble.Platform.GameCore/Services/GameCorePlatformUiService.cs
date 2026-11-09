using GDK.Net;
using GDK.Net.GameUI;

namespace NetRumble.Platform.GameCore.Services;

/// <summary>
/// GDK platform UI: the system on-screen keyboard for text entry.
/// </summary>
/// <remarks>
/// <para>
/// Ported from the console half of <c>scripts/ui/elements/nr_system_keyboard.gd</c>.
/// Text entry is handled by GDK.Net's <see cref="GameUiManager.ShowTextEntryAsync"/>
/// which wraps <c>XGameUiShowTextEntryAsync</c>; the managed projection owns the
/// two-call result-size pattern and the UTF-8 buffer.
/// </para>
/// <para>
/// <b>Unverifiable in this environment.</b> Text entry needs an interactive session
/// with the GDK's system UI actually able to draw, which a desktop dev machine without
/// a foreground game window in the right state may not provide even with the runtime
/// initialized. This class was verified to build and its GDK.Net calls to resolve; the
/// UI itself was not observed to open. See <c>docs/design-notes.md</c>.
/// </para>
/// </remarks>
internal sealed class GameCorePlatformUiService : IPlatformUiService
{
    private readonly GameCoreRuntime _runtime;
    internal GameCorePlatformUiService(GameCoreRuntime runtime)
    {
        _runtime = runtime;
    }

    /// <summary>
    /// Always true on this provider. The GDK owns text entry on every device it runs
    /// on, including desktop - there is no "physical keyboard is attached, so draw our
    /// own field" branch in the GDK the way there might be on a generic PC platform.
    /// </summary>
    public bool RequiresVirtualKeyboard => true;

    public async Task<PlatformResult<string>> ShowTextEntryAsync(
        TextEntryRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_runtime.IsInitialized || _runtime.Runtime is null)
        {
            return PlatformResult<string>.Unavailable("The system keyboard is not available in this build.");
        }

        var inputScope = ToInputScope(request.Scope);

        try
        {
            var text = await _runtime.Dispatcher
                .Marshal(_runtime.Runtime.GameUi.ShowTextEntryAsync(
                    request.Title,
                    request.Description,
                    request.DefaultText,
                    inputScope,
                    (uint)Math.Max(request.MaxLength, 1),
                    cancellationToken))
                .ConfigureAwait(false);

            return PlatformResult<string>.Ok(text);
        }
        catch (GameRuntimeException ex) when (ex.HResult == unchecked((int)0x800704C7))
        {
            // E_CANCELLED (dismissed by the player). Not an error - see the interface
            // contract on ShowTextEntryAsync.
            return PlatformResult<string>.Canceled();
        }
        catch (GameRuntimeException ex)
        {
            return PlatformResult<string>.Fail(
                PlatformStatus.Failed, "The system keyboard could not be shown.", ex.Message);
        }
        catch (OperationCanceledException)
        {
            return PlatformResult<string>.Canceled();
        }
    }

    /// <summary>Maps this title's <see cref="TextEntryScope"/> onto
    /// GDK.Net's <see cref="TextEntryInputScope"/>. <c>Password</c> maps to the GDK's
    /// own masked scope so the system keyboard itself hides the characters, not just this
    /// title's display of the result.</summary>
    private static TextEntryInputScope ToInputScope(TextEntryScope scope) => scope switch
    {
        TextEntryScope.Alphanumeric => TextEntryInputScope.Alphanumeric,
        TextEntryScope.Password => TextEntryInputScope.Password,
        TextEntryScope.EmailAddress => TextEntryInputScope.EmailSmtpAddress,
        TextEntryScope.Chat => TextEntryInputScope.ChatWithoutEmoji,
        _ => TextEntryInputScope.Default,
    };
}
