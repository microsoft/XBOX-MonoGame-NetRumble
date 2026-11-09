using NetRumble.Platform.GameCore.Interop;
using NetRumble.Platform.GameCore.Services;
using NetRumble.Platform.PlayFab;

namespace NetRumble.Platform.GameCore;

/// <summary>
/// <see cref="IPlatformProvider"/> over the native Microsoft GDK, PlayFab and Party SDKs,
/// reached through GDK.Net.
/// </summary>
/// <remarks>
/// <para>
/// Runtime, identity and online services are GDK-backed. <see cref="Capabilities"/>
/// reports only the services whose native dependencies are available.
/// </para>
/// <para>
/// <b>Replacing this provider.</b> A standalone C# GDK would replace this class and the
/// whole <c>Interop</c> folder, and nothing else in the solution. The contract it must
/// keep is in <see cref="IPlatformRuntime"/>: every task it hands out and every event it
/// raises must surface on the thread that calls <see cref="IPlatformRuntime.Pump"/>.
/// </para>
/// </remarks>
public sealed class GameCorePlatformProvider : IPlatformProvider
{
    private readonly GameCoreRuntime _runtime;
    private readonly GameCoreXblContext _xbl;
    private readonly GameCoreOnlineSession _online;
    private readonly GameCoreGameSaveService _saves;

    public GameCorePlatformProvider(string? saveRoot = null, PlayFabConfiguration? playFab = null)
    {
        _runtime = new GameCoreRuntime();
        GameInput = new NullGameInputService();
        _online = new GameCoreOnlineSession(
            _runtime,
            playFab ?? new PlayFabConfiguration { TitleId = PlayFabConfiguration.PlaceholderTitleId });

        Identity = new GameCoreIdentityService(_runtime, _online);
        var identity = (GameCoreIdentityService)Identity;

        _xbl = new GameCoreXblContext(_runtime, identity);

        _saves = new GameCoreGameSaveService(
            _runtime,
            identity,
            saveRoot,
            () => identity.CurrentUser?.LocalId);

        Privileges = new GameCorePrivilegeService(_runtime, identity);
        Achievements = new GameCoreAchievementService(_runtime, identity, _xbl);
        Social = new GameCoreSocialService(_runtime, identity, _xbl);
        Moderation = new GameCoreModerationService(_runtime, identity, _xbl);
        Privacy = new GameCorePrivacyService(_runtime, identity, _xbl);
        PlatformUi = new GameCorePlatformUiService(_runtime);

        Party = new GameCorePartyService(
            _runtime, identity, _online, _online.Configuration, (GameCoreModerationService)Moderation);

        var activity = new GameCoreActivityService(_runtime, identity, _xbl);
        Activity = activity;

        // Pumped from GameCoreRuntime.Pump rather than called directly from here - see
        // GameCoreActivityService's own remarks on why activation callbacks must land on
        // the same thread every other provider event does.
        _runtime.Activity = activity;
        _runtime.Party = (GameCorePartyService)Party;
        _runtime.Online = _online;
    }

    public string Name => "Microsoft GDK (GDK.Net)";

    /// <summary>
    /// Only what is actually implemented. Extend this as each service is wired, so the
    /// UI lights up in step with the provider rather than ahead of it.
    /// </summary>
    public PlatformCapabilities Capabilities
    {
        get
        {
            if (!_runtime.IsRuntimeAvailable)
            {
                return PlatformCapabilities.None;
            }

            var capabilities = PlatformCapabilities.Identity
                | PlatformCapabilities.Privileges
                | PlatformCapabilities.VirtualKeyboard;

            // These additionally need Xbox Services (Microsoft.Xbox.Services.C.Thunks.dll),
            // a separate redistributable from the GDK core - see XblRuntimeProbe. Reporting
            // them unconditionally on GameRuntimeProbe alone would advertise features that
            // fail on a GDK-only machine missing that one extra DLL.
            if (XblRuntimeProbe.IsAvailable)
            {
                capabilities |= PlatformCapabilities.Achievements
                    | PlatformCapabilities.Social
                    | PlatformCapabilities.Moderation
                    | PlatformCapabilities.Privacy
                    | PlatformCapabilities.Presence
                    | PlatformCapabilities.Invites;
            }

            if (XblRuntimeProbe.IsAvailable
                && PartyRuntimeProbe.IsAvailable
                && !_online.Configuration.IsPlaceholder)
            {
                capabilities |= PlatformCapabilities.Party
                    | PlatformCapabilities.Matchmaking
                    | PlatformCapabilities.VoiceChat;
            }

            // Connected storage is a Gaming Runtime service and knows nothing about
            // PlayFab, so gating it on a PlayFab title id would hide roaming saves on
            // exactly the console builds that are required to have them.
            capabilities |= PlatformCapabilities.CloudSave;

            return capabilities;
        }
    }

    public IPlatformRuntime Runtime => _runtime;

    public IIdentityService Identity { get; }

    public IPrivilegeService Privileges { get; }

    public IAchievementService Achievements { get; }

    public ISocialService Social { get; }

    public IModerationService Moderation { get; }

    public IPrivacyService Privacy { get; }

    public IPlatformUiService PlatformUi { get; }

    // --- Mixed-provider services -------------------------------------------
    // Party uses GameCoreOnlineSession; XGameSaveFiles uses the Xbox user directly.
    // GameInput is deliberately never GDK-backed - see its own remarks below.

    public IPartyService Party { get; }
    public IActivityService Activity { get; }

    /// <summary>
    /// Local saves plus XGameSaveFiles connected storage through GDK.Net.
    /// </summary>
    public IGameSaveService GameSaves => _saves;

    /// <summary>
    /// A null service on purpose, not left unimplemented. <c>docs/platform-abstraction.md</c>
    /// assigns <see cref="IGameInputService"/> to MonoGame's own <c>GamePad</c>/<c>Keyboard</c>
    /// APIs rather than the GDK's GameInput SDK - the Godot source's GameInput usage is
    /// for controller identification/haptics on console, which is a <c>NetRumble.Game</c>
    /// concern outside this provider's scope, not a gap in this phase's work.
    /// </summary>
    public IGameInputService GameInput { get; }

    /// <summary>
    /// Tears down the provider.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two of these steps await work that only completes on the pump, and the game
    /// disposes the provider synchronously on the pump thread, so they are driven through
    /// <see cref="GameCoreRuntime.PumpUntil"/> rather than awaited directly. See its
    /// remarks: awaiting them here is a deadlock on any machine where they do real work.
    /// </para>
    /// <para>
    /// The order is deliberate and unchanged: leave the party while its network is still
    /// up, sign out while the Xbox context still exists, tear down Xbox Services while the
    /// Gaming Runtime is still initialized, and only then shut the runtime down.
    /// </para>
    /// <para>
    /// <b>One budget across the pump-driven stages, not one per stage.</b> Both drains used to
    /// arm their own five seconds, so an exit that had to wait for both waited ten, on top
    /// of the cloud-save flush the game runs before calling this - thirteen seconds with
    /// the audio already stopped and the window still up. The reasoning that justified it
    /// measured the wrong thing: the player is not waiting for a stage, they are waiting
    /// for the window to go away, and what they experience is the sum. A later stage now
    /// joins the deadline already running instead of pushing it back. Nothing else
    /// changes - both stages are still waited on, and one that never lands still gives
    /// way rather than turning an exit into a hang.
    /// </para>
    /// <para>
    /// PlayFab sign-ins and borrowed entity handles do not need dispatcher pumping.
    /// Their shutdown is awaited without the pump budget so the Gaming Runtime cannot
    /// be released while those native calls are still using it.
    /// </para>
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        var deadline = DateTime.UtcNow + TeardownBudget;

        _runtime.PumpUntil(Party.LeaveAsync(), RemainingUntil(deadline));
        await Identity.SignOutAsync().ConfigureAwait(false);
        _runtime.PumpUntil(_xbl.ShutdownAsync(), RemainingUntil(deadline));
        await _saves.ShutdownAsync().ConfigureAwait(false);
        await _online.ShutdownAsync().ConfigureAwait(false);
        await _runtime.ShutdownAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// How long the whole teardown may spend waiting on platform calls that only complete
    /// on the pump.
    /// </summary>
    private static readonly TimeSpan TeardownBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// What is left of the shared budget, floored at zero so a stage that starts after the
    /// deadline has passed takes its one chance to complete and then gives way, rather
    /// than being handed a negative timeout.
    /// </summary>
    private static TimeSpan RemainingUntil(DateTime deadline)
    {
        var remaining = deadline - DateTime.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>True when the native GDK is present in this process.</summary>
    public static bool IsRuntimePresent => GameRuntimeProbe.IsAvailable;

    /// <summary>Why the runtime is unavailable, for the startup log.</summary>
    public static string UnavailableReason => GameRuntimeProbe.UnavailableReason;
}
