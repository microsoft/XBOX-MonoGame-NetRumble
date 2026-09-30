namespace NetRumble.Platform.Offline;

/// <summary>
/// A fully functional <see cref="IPlatformProvider"/> that needs no native runtime.
/// </summary>
/// <remarks>
/// <para>
/// Online capabilities are absent; local capabilities are real. Sign-in produces a
/// local guest identity, saves go to <c>%LOCALAPPDATA%</c>, and everything else
/// returns <see cref="PlatformStatus.Unavailable"/> rather than throwing.
/// </para>
/// <para>
/// This mirrors the two-tier policy the Godot port used
/// (<c>scripts/autoload/services.gd</c>): best-effort services degrade to a working
/// no-op so the front end and practice match still run, while multiplayer is
/// explicitly gated and reports why.
/// </para>
/// </remarks>
public sealed class OfflinePlatformProvider : IPlatformProvider
{
    public OfflinePlatformProvider(string? saveRoot = null)
    {
        Runtime = new OfflineRuntime();
        Identity = new OfflineIdentityService();
        Party = new OfflinePartyService();
        Activity = new OfflineActivityService();
        Privileges = new OfflinePrivilegeService();
        Privacy = new OfflinePrivacyService();
        Moderation = new OfflineModerationService();
        Social = new OfflineSocialService();
        Achievements = new OfflineAchievementService();
        GameSaves = new LocalFileGameSaveService(saveRoot, () => Identity.CurrentUser?.LocalId);
        GameInput = new NullGameInputService();
        PlatformUi = new OfflinePlatformUiService();
    }

    public string Name => "Offline";

    /// <summary>
    /// Nothing online is available. Local save always works but is not a
    /// capability flag, because <see cref="IGameSaveService.SaveLocalAsync"/> is
    /// required of every provider. The game reads this to hide the online menu
    /// entries instead of letting the player walk into a failure.
    /// </summary>
    public PlatformCapabilities Capabilities => PlatformCapabilities.None;

    public IPlatformRuntime Runtime { get; }
    public IIdentityService Identity { get; }
    public IPartyService Party { get; }
    public IActivityService Activity { get; }
    public IPrivilegeService Privileges { get; }
    public IPrivacyService Privacy { get; }
    public IModerationService Moderation { get; }
    public ISocialService Social { get; }
    public IAchievementService Achievements { get; }
    public IGameSaveService GameSaves { get; }
    public IGameInputService GameInput { get; }
    public IPlatformUiService PlatformUi { get; }

    public async ValueTask DisposeAsync()
    {
        await Party.LeaveAsync().ConfigureAwait(false);
        await Runtime.ShutdownAsync().ConfigureAwait(false);
    }
}
