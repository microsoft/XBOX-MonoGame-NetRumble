namespace NetRumble.Platform;

/// <summary>
/// Sign-in and the local user's identity.
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>scripts/services/identity_service.gd</c>. The shipping flow is the
/// GDK <i>check → silent → UI</i> fallback
/// (<c>get_primary_user</c> → <c>add_default_user_async</c> →
/// <c>add_user_with_ui_async</c>), whose result is then exchanged for a PlayFab user
/// via <c>sign_in_with_xuser_async</c>. That whole chain is one call here: providers
/// own the sequencing, the game owns the UI.
/// </para>
/// <para>
/// <b>Sign-in is required for multiplayer.</b> Party is the transport and Lobby the
/// matchmaking, and both need a PlayFab user, so there is no "play online as guest".
/// The practice match needs no identity at all.
/// </para>
/// </remarks>
public interface IIdentityService
{
    /// <summary>The signed-in user, or null.</summary>
    PlatformUser? CurrentUser { get; }

    bool IsSignedIn => CurrentUser is not null;

    /// <summary>
    /// True while <see cref="SignInAsync"/> is still running, including the online
    /// identity step that follows the platform one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exists because "signed in" and "ready to play online" are not the same moment.
    /// The Xbox half of <see cref="PlatformUser"/> resolves first and the PlayFab entity
    /// is added after; anything that needs <see cref="PlatformUser.EntityId"/> - joining
    /// from an invite, above all - has to wait for the whole chain rather than for the
    /// first sign of a user.
    /// </para>
    /// <para>
    /// A caller that waits on this must still cope with the chain ending without an
    /// entity id: the PlayFab step is deliberately non-fatal, so "no longer in progress"
    /// means the answer is final, not that it is a success.
    /// </para>
    /// </remarks>
    bool IsSignInInProgress => false;

    /// <summary>
    /// Raised when the user changes underneath the title — a console user switch, or a
    /// sign-out. The game must tear down the session and return to the sign-in screen.
    /// </summary>
    event Action<PlatformUser?>? UserChanged;

    /// <summary>
    /// Raised when the <i>platform</i> changes something about the signed-in account
    /// (XR-052, XR-115), as opposed to <see cref="UserChanged"/>, which answers "who is
    /// the user now" and is also raised by this title's own sign-in and sign-out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two are separate because they are consumed for different reasons and at
    /// different moments. <see cref="UserChanged"/> is the identity; this is the
    /// <i>notification</i>, and its value is entirely in its kind — a handler cannot tell
    /// "your privileges were re-evaluated" from "your account is going away" if all it is
    /// given is the new user.
    /// </para>
    /// <para>
    /// <b>Raised synchronously, on whatever thread the platform used.</b> Handlers must
    /// not <c>await</c>. <see cref="PlatformAccountChange.SigningOut"/> in particular is
    /// the last moment the title is guaranteed to be running - the platform terminates a
    /// title whose user has signed out - so work parked behind a continuation is work that
    /// never happens.
    /// </para>
    /// </remarks>
    event Action<PlatformAccountChange>? AccountChanged;

    /// <summary>
    /// Whether the platform is answering the controller-association question on this
    /// machine at all (XR-115).
    /// </summary>
    /// <remarks>
    /// False on desktop, in a build with no GDK, and on console until the platform has
    /// reported at least one association for this account. While it is false,
    /// <see cref="HasAssociatedController"/> means nothing and callers must fall back to
    /// whatever the engine can see.
    /// </remarks>
    bool TracksControllerAssociations => false;

    /// <summary>
    /// Whether the signed-in account currently owns at least one controller. Only
    /// meaningful while <see cref="TracksControllerAssociations"/> is true.
    /// </summary>
    /// <remarks>
    /// Scoped to the account on purpose, and that is the whole reason this lives on the
    /// platform rather than being read off the engine's pad list: on a console with two
    /// players signed in, the other player's controller disconnecting is not this
    /// player's problem, and a raw connected-pad count cannot tell the difference.
    /// </remarks>
    bool HasAssociatedController => false;

    /// <summary>
    /// Raised when the set of controllers paired with the signed-in account changes.
    /// Carries no payload: the question is only ever "does the player still have a pad",
    /// and the answer is <see cref="HasAssociatedController"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately <i>not</i> on <see cref="IGameInputService"/>, even though that
    /// interface has device events. This comes from the GDK's user manager, not its
    /// GameInput SDK - it is a fact about an account, not about a reading - and
    /// <c>docs/platform-abstraction.md</c> assigns input reading to MonoGame. Godot draws
    /// the line in the same place: its <c>DeviceService</c> subscribes to
    /// <c>XboxUsers.device_association_changed</c>.
    /// </remarks>
    event Action? ControllerAssociationChanged;

    /// <summary>
    /// Progress through the sign-in chain, in words fit to show a player
    /// ("Starting the Microsoft GDK", "Signing in to PlayFab").
    /// </summary>
    /// <remarks>
    /// Kept because sign-in is a chain of platform calls that cannot be stepped through
    /// on a desktop machine — a stall is otherwise indistinguishable from any other.
    /// The acquire-user screen shows this under its spinner.
    /// </remarks>
    event Action<string>? SignInStageChanged;

    /// <summary>Most recent stage, for a screen that subscribes after it was raised.</summary>
    string CurrentStage { get; }

    /// <summary>
    /// Runs the full sign-in chain.
    /// </summary>
    /// <param name="options">
    /// Controls whether platform UI may be shown. The acquire-user screen allows it;
    /// a silent refresh does not.
    /// </param>
    Task<PlatformResult<PlatformUser>> SignInAsync(
        SignInOptions options = default,
        CancellationToken cancellationToken = default);

    /// <summary>Clears local identity state. Does not sign the user out of the console.</summary>
    Task SignOutAsync();

    /// <summary>
    /// Re-reads the signed-in account's profile from the platform and raises
    /// <see cref="UserChanged"/> if anything about it moved (XR-048).
    /// </summary>
    /// <remarks>
    /// A gamertag can change while the title is suspended, or in the shell behind a
    /// <see cref="PlatformAccountChange.SignedInAgain"/>, and everything downstream of
    /// <see cref="CurrentUser"/> - the lobby roster, the identity a peer is sent, the
    /// name on a save - would otherwise carry the name read at sign-in for the rest of
    /// the session. Providers that cannot change identity behind the title's back, such
    /// as the offline one, are correct to do nothing.
    /// </remarks>
    Task RefreshUserAsync() => Task.CompletedTask;
}

/// <summary>
/// What the platform changed about the signed-in account. Ported from the
/// <c>change_kind</c> strings in <c>scripts/autoload/services.gd</c>
/// <c>_on_user_changed()</c>, which are in turn the GDK's <c>XUserChangeEvent</c>.
/// </summary>
/// <remarks>
/// The GDK also reports gamertag and gamer-picture changes. Neither is here, because
/// neither invalidates anything this title caches and an enum member nothing acts on is
/// an invitation to write a handler that does nothing.
/// </remarks>
public enum PlatformAccountChange
{
    /// <summary>
    /// The signed-in account is going away, and this is the last moment the title is
    /// guaranteed to run: the platform terminates a title whose user has signed out.
    /// Commit durable state now, synchronously, and do nothing else.
    /// </summary>
    SigningOut,

    /// <summary>The account is gone. Anything held on its behalf is now invalid.</summary>
    SignedOut,

    /// <summary>
    /// The account was re-authenticated. It is the same player, but every cached answer
    /// about them was obtained with credentials that have since been replaced.
    /// </summary>
    SignedInAgain,

    /// <summary>
    /// The account's privileges were re-evaluated - a child account whose parent changed
    /// a setting, or a lapsed subscription. Cached privilege answers are now stale rather
    /// than absent, which is the more dangerous of the two.
    /// </summary>
    PrivilegesChanged,
}

/// <summary>Options for <see cref="IIdentityService.SignInAsync"/>.</summary>
public readonly record struct SignInOptions
{
    /// <summary>
    /// Allow the platform account picker to be shown when a silent sign-in fails.
    /// False performs a silent-only attempt.
    /// </summary>
    public bool AllowUserInterface { get; init; }

    /// <summary>
    /// Developer override selecting a per-instance PlayFab custom id
    /// (<c>--pf-user=&lt;name&gt;</c> / <c>PF_CUSTOM_ID</c>).
    /// </summary>
    /// <remarks>
    /// The GDK binds one Xbox user per PC, so two signed-in GDK instances cannot coexist
    /// on one machine and Party could otherwise only be tested with two PCs. Providers
    /// <b>must</b> ignore this outside debug desktop builds — it reaches PlayFab with no
    /// Xbox identity behind it and creates the account on first use.
    /// </remarks>
    public string? DeveloperCustomId { get; init; }

    public static SignInOptions Interactive => new() { AllowUserInterface = true };
    public static SignInOptions Silent => new() { AllowUserInterface = false };
}
