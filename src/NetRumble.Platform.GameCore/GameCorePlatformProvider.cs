using NetRumble.Platform.GameCore.Interop;
using NetRumble.Platform.GameCore.Services;
using NetRumble.Platform.Offline;
using NetRumble.Platform.PlayFab;

namespace NetRumble.Platform.GameCore;

/// <summary>
/// <see cref="IPlatformProvider"/> over the native Microsoft GDK, PlayFab and Party SDKs,
/// reached through GDK.Net.
/// </summary>
/// <remarks>
/// <para>
/// <b>Partial by design.</b> Runtime and identity are GDK-backed; the remaining
/// services still delegate to their offline counterparts and are filled in through
/// Phase 5 and Phase 6. Because <see cref="Capabilities"/> reports only what is really
/// wired, the game hides the rest rather than calling into a stub.
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
    private readonly OfflinePlatformProvider _fallback;
#if !NETRUMBLE_PLAYFAB_GAMESAVE
    private readonly GameCoreGameSaveService _saves;
#endif

    public GameCorePlatformProvider(string? saveRoot = null, PlayFabConfiguration? playFab = null)
    {
        _runtime = new GameCoreRuntime();
        _fallback = new OfflinePlatformProvider(saveRoot);
        _online = new GameCoreOnlineSession(
            _runtime,
            playFab ?? new PlayFabConfiguration { TitleId = PlayFabConfiguration.PlaceholderTitleId },
            saveRoot);

        Identity = new GameCoreIdentityService(_runtime, _online);
        var identity = (GameCoreIdentityService)Identity;

        _xbl = new GameCoreXblContext(_runtime, identity);

#if !NETRUMBLE_PLAYFAB_GAMESAVE
        // The cloud tier is the GDK's connected storage (M10). The local tier moves with
        // it onto the Xbox account, because a save service that no longer talks to
        // PlayFab must not still need a PlayFab login to decide which folder a file
        // belongs in; the PlayFab entity id is passed as the legacy key so a player
        // upgrading from a PlayFab-backed build still reads their existing files.
        _saves = new GameCoreGameSaveService(
            _runtime,
            identity,
            saveRoot,
            () => identity.CurrentUser?.LocalId,
            () => _online.EntityId is { Length: > 0 } entityId ? entityId : null);
#endif

        Privileges = new GameCorePrivilegeService(_runtime, identity);
        Achievements = new GameCoreAchievementService(_runtime, identity, _xbl);
        Social = new GameCoreSocialService(_runtime, identity, _xbl);
        Moderation = new GameCoreModerationService(_runtime, identity, _xbl);
        Privacy = new GameCorePrivacyService(_runtime, identity, _xbl);
        PlatformUi = new GameCorePlatformUiService(_runtime, identity);

        Party = new GameCorePartyService(
            _runtime, identity, _online, _online.Configuration, (GameCoreModerationService)Moderation);

        var activity = new GameCoreActivityService(_runtime, identity, _xbl);
        Activity = activity;

        // Pumped from GameCoreRuntime.Pump rather than called directly from here - see
        // GameCoreActivityService's own remarks on why activation callbacks must land on
        // the same thread every other provider event does.
        _runtime.Activity = activity;
        _runtime.Party = (GameCorePartyService)Party;
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

#if NETRUMBLE_PLAYFAB_GAMESAVE
            // The PlayFab user-data tier is the whole of cloud save in this build, so a
            // placeholder title id means there is no cloud save to advertise.
            if (!_online.Configuration.IsPlaceholder)
            {
                capabilities |= PlatformCapabilities.CloudSave;
            }
#else
            // Connected storage is a Gaming Runtime service and knows nothing about
            // PlayFab, so gating it on a PlayFab title id would hide roaming saves on
            // exactly the console builds that are required to have them.
            capabilities |= PlatformCapabilities.CloudSave;
#endif

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
    // Party, Activity and GameSaves are all backed by the same
    // GameCoreOnlineSession, so the Xbox user produces exactly one PlayFab identity.
    // GameInput is deliberately never GDK-backed - see its own remarks below.

    public IPartyService Party { get; }
    public IActivityService Activity { get; }

    /// <summary>
    /// Local save plus a roaming cloud tier.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Which cloud tier is a build-time choice, because the two do not coexist usefully:
    /// they would roam the same keys to two different places and disagree. Connected
    /// storage (<see cref="GameCoreGameSaveService"/>) is the default and the one XBOX
    /// certification is written against; defining <c>NETRUMBLE_PLAYFAB_GAMESAVE</c> -
    /// through <c>/p:NetRumbleCloudSave=PlayFab</c> - selects the PlayFab user-data tier
    /// instead, which is what the desktop <c>PlayFabPlatformProvider</c> uses
    /// unconditionally because it has no GDK user handle to give connected storage.
    /// </para>
    /// </remarks>
#if NETRUMBLE_PLAYFAB_GAMESAVE
    public IGameSaveService GameSaves => _online.GameSaves;
#else
    public IGameSaveService GameSaves => _saves;
#endif

    /// <summary>
    /// Delegated on purpose, not left unimplemented. <c>docs/platform-abstraction.md</c>
    /// assigns <see cref="IGameInputService"/> to MonoGame's own <c>GamePad</c>/<c>Keyboard</c>
    /// APIs rather than the GDK's GameInput SDK - the Godot source's GameInput usage is
    /// for controller identification/haptics on console, which is a <c>NetRumble.Game</c>
    /// concern outside this provider's scope, not a gap in this phase's work.
    /// </summary>
    public IGameInputService GameInput => _fallback.GameInput;

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
    /// <b>One budget across the whole teardown, not one per stage.</b> Both drains used to
    /// arm their own five seconds, so an exit that had to wait for both waited ten, on top
    /// of the cloud-save flush the game runs before calling this - thirteen seconds with
    /// the audio already stopped and the window still up. The reasoning that justified it
    /// measured the wrong thing: the player is not waiting for a stage, they are waiting
    /// for the window to go away, and what they experience is the sum. A later stage now
    /// joins the deadline already running instead of pushing it back. Nothing else
    /// changes - both stages are still waited on, and one that never lands still gives
    /// way rather than turning an exit into a hang.
    /// </para>
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        var deadline = DateTime.UtcNow + TeardownBudget;

        _runtime.PumpUntil(Party.LeaveAsync(), RemainingUntil(deadline));
        await Identity.SignOutAsync().ConfigureAwait(false);
        _runtime.PumpUntil(_xbl.ShutdownAsync(), RemainingUntil(deadline));
#if !NETRUMBLE_PLAYFAB_GAMESAVE
        // Before the runtime goes down: the provider holds a native handle bound to a
        // user that sign-out has already released.
        _saves.Dispose();
#endif
        _online.Dispose();
        await _runtime.ShutdownAsync().ConfigureAwait(false);
        await _fallback.DisposeAsync().ConfigureAwait(false);
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
