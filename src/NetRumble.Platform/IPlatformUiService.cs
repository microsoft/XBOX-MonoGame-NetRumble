namespace NetRumble.Platform;

/// <summary>
/// Platform-owned UI: the virtual keyboard, account picker and store pages.
/// </summary>
/// <remarks>
/// Ported from <c>scripts/ui/elements/nr_system_keyboard.gd</c>, which routed text
/// entry to the GDK virtual keyboard on console. Kept as a platform service rather
/// than a UI widget because on console the OS owns the surface entirely — the game
/// gets a string back, not keystrokes.
/// </remarks>
public interface IPlatformUiService
{
    /// <summary>
    /// True when text entry must go through the platform keyboard. False on desktop,
    /// where the game draws its own text field.
    /// </summary>
    bool RequiresVirtualKeyboard { get; }

    /// <summary>
    /// Shows the platform text-entry UI. Returns a canceled result when dismissed,
    /// which the caller must not treat as an error.
    /// </summary>
    Task<PlatformResult<string>> ShowTextEntryAsync(
        TextEntryRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Shows the account picker so the player can switch users.</summary>
    Task<PlatformResult> ShowAccountPickerAsync(CancellationToken cancellationToken = default);
}

public sealed record TextEntryRequest
{
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string DefaultText { get; init; } = string.Empty;
    public int MaxLength { get; init; } = 128;
    public TextEntryScope Scope { get; init; } = TextEntryScope.Default;
}

public enum TextEntryScope
{
    Default,
    /// <summary>Alphanumeric only - used by the five-character join-code entry.</summary>
    Alphanumeric,
    Password,
    EmailAddress,
    /// <summary>Free text destined for other players; must be moderated before sending.</summary>
    Chat,
}
