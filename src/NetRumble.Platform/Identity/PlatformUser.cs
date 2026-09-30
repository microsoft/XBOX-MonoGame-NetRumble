namespace NetRumble.Platform;

/// <summary>
/// The signed-in local player, flattened from the GDK <c>XUser</c> and the PlayFab
/// user into one plain object.
/// </summary>
/// <remarks>
/// Ported from <c>scripts/services/identity_service.gd</c>, which held the SDK objects
/// as <c>Variant</c> precisely because the native types only exist when the extension
/// loads. Here the equivalent discipline is stricter: <b>no native handle ever leaves
/// the provider</b>. Providers keep their own <c>XUserHandle</c> / entity-token state
/// keyed by <see cref="LocalId"/>.
/// </remarks>
public sealed record PlatformUser
{
    /// <summary>
    /// Provider-local stable id for this user. Opaque to game code; pass it back to
    /// the provider to identify the user. Never parse it.
    /// </summary>
    public required string LocalId { get; init; }

    /// <summary>Display name shown in the roster and lobby (the Xbox gamertag).</summary>
    public required string DisplayName { get; init; }

    /// <summary>Xbox user id. Empty when signed in by a non-Xbox path.</summary>
    public string XboxUserId { get; init; } = string.Empty;

    /// <summary>PlayFab entity id, needed by Party and Lobby. Empty when offline.</summary>
    public string EntityId { get; init; } = string.Empty;

    /// <summary>
    /// True when this session used the <c>--pf-user</c> custom-id developer override
    /// rather than a real Xbox sign-in. Debug desktop builds only; see
    /// <see cref="IIdentityService.SignInAsync"/>.
    /// </summary>
    public bool IsDeveloperOverride { get; init; }

    /// <summary>
    /// The account category the platform reports for this user (XR-013).
    /// </summary>
    /// <remarks>
    /// Read from <c>XUserGetAgeGroup</c> on the GDK provider and
    /// <see cref="PlatformAgeGroup.Unknown"/> everywhere else. XR-013 requires this to be
    /// validated before the title offers account creation or linking, because a child
    /// account needs parental consent through the publisher's own compliant flow and
    /// cannot have one provisioned for it silently. See
    /// <see cref="PlatformAgeGroupExtensions.AllowsSilentAccountCreation"/>.
    /// </remarks>
    public PlatformAgeGroup AgeGroup { get; init; } = PlatformAgeGroup.Unknown;

    public override string ToString() => $"{DisplayName} ({LocalId})";
}

/// <summary>
/// The account categories XR-013 requires a title to distinguish, mirroring
/// <c>XUserAgeGroup</c>.
/// </summary>
public enum PlatformAgeGroup
{
    /// <summary>
    /// The platform could not tell, or there is no platform to ask. Treated as the most
    /// restrictive case, not the least: an unknown age is not a confirmed adult.
    /// </summary>
    Unknown = 0,
    Child,
    Teen,
    Adult,
}

public static class PlatformAgeGroupExtensions
{
    /// <summary>
    /// Whether a publisher account may be created for this user without a separate
    /// consent step (XR-013).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Adults and teens, yes. Children and unknown, no: the requirement states that "for
    /// child accounts, publishers must obtain parental consent before creating an account
    /// where required by law", and that consent "must be provided through the publisher's
    /// compliant account flow, not assumed from XBOX network participation alone". An
    /// unknown age group is grouped with children because the title cannot demonstrate
    /// otherwise.
    /// </para>
    /// <para>
    /// This gates <i>creation</i> only. A user who already has a linked publisher account
    /// still signs in to it, which is the single sign-on behaviour the same requirement
    /// asks for.
    /// </para>
    /// </remarks>
    public static bool AllowsSilentAccountCreation(this PlatformAgeGroup ageGroup)
        => ageGroup is PlatformAgeGroup.Adult or PlatformAgeGroup.Teen;
}
