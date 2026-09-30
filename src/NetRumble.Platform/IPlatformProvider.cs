namespace NetRumble.Platform;

/// <summary>
/// Root of the platform abstraction: the single object the game resolves every
/// platform capability from.
/// </summary>
/// <remarks>
/// <para>
/// Ported from the <c>Services</c> autoload facade in
/// <c>scripts/autoload/services.gd</c>, which fronted the <c>XBOX</c> and
/// <c>PlayFab</c> GDExtension singletons for the rest of the game.
/// </para>
/// <para>
/// <b>Swapping providers.</b> The game never references a provider assembly
/// directly; it takes an <see cref="IPlatformProvider"/> and nothing else. To move
/// from the P/Invoke provider to a standalone C# GDK, implement this interface in a
/// new assembly and change the one composition-root line that constructs it. No
/// gameplay, UI or netcode file should need to change.
/// </para>
/// <para>
/// <b>Two service tiers, deliberately different</b> — preserved from the Godot port:
/// multiplayer is *not* optional-online (Party is the transport and Lobby is the
/// matchmaking, both requiring a signed-in user), whereas achievements,
/// saves and presence are best-effort and degrade to working no-ops. Providers must
/// honour that: best-effort services return empty/failed results rather than throwing.
/// </para>
/// </remarks>
public interface IPlatformProvider : IAsyncDisposable
{
    /// <summary>Human-readable provider name, for logs and the debug overlay.</summary>
    string Name { get; }

    /// <summary>
    /// Capabilities this provider actually implements. The game uses this to hide UI
    /// (for example the Achievements panel) rather than calling and handling failure.
    /// </summary>
    PlatformCapabilities Capabilities { get; }

    /// <summary>Lifecycle and the per-frame pump. Always present.</summary>
    IPlatformRuntime Runtime { get; }

    /// <summary>Sign-in and the local user's identity. Always present.</summary>
    IIdentityService Identity { get; }

    /// <summary>Networking transport, chat and matchmaking.</summary>
    IPartyService Party { get; }

    /// <summary>Multiplayer activity, invites, presence and recent players.</summary>
    IActivityService Activity { get; }

    /// <summary>Account privilege checks (XR-045).</summary>
    IPrivilegeService Privileges { get; }

    /// <summary>Per-player communication privacy (XR-015).</summary>
    IPrivacyService Privacy { get; }

    /// <summary>Text and name moderation.</summary>
    IModerationService Moderation { get; }

    /// <summary>Friends and player profiles.</summary>
    ISocialService Social { get; }

    /// <summary>Achievement unlock and progress.</summary>
    IAchievementService Achievements { get; }

    /// <summary>Local and cloud save storage.</summary>
    IGameSaveService GameSaves { get; }

    /// <summary>Gamepad reading and haptics.</summary>
    IGameInputService GameInput { get; }

    /// <summary>Platform UI: account picker, virtual keyboard, store pages.</summary>
    IPlatformUiService PlatformUi { get; }
}
