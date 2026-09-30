using NetRumble.Platform.Diagnostics;
using NetRumble.Platform.GameCore;
using NetRumble.Platform.GameCore.Interop;
using NetRumble.Platform.Lan;
using NetRumble.Platform.Offline;
using NetRumble.Platform.PlayFab;

namespace NetRumble.Platform.Composition;

/// <summary>
/// The composition root: the one place that decides which provider the game runs on.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the file you edit to swap P/Invoke for a standalone C# GDK.</b> Add a
/// case to <see cref="Create"/>, and nothing else in the solution changes. Game,
/// gameplay, UI and netcode code only ever see <see cref="IPlatformProvider"/>.
/// </para>
/// <para>
/// It is also the only assembly that references the concrete providers. Keeping it
/// separate from <c>NetRumble.Game</c> means a headless test host can compose an
/// offline provider without dragging MonoGame in.
/// </para>
/// </remarks>
public static class PlatformProviderFactory
{
    /// <summary>
    /// Builds a provider for the requested mode, falling back to offline whenever the
    /// native runtime is missing.
    /// </summary>
    /// <param name="mode">
    /// Which implementation to prefer. <see cref="PlatformProviderMode.Auto"/> is what
    /// shipping builds use.
    /// </param>
    /// <param name="saveRoot">
    /// Override for local save storage. Tests pass a temp directory.
    /// </param>
    /// <param name="playFab">
    /// When supplied with a real title id, enables PlayFab-backed services. The GDK
    /// provider consumes this directly so its Xbox user produces exactly one PlayFab
    /// identity; the other providers are wrapped by <see cref="PlayFabPlatformProvider"/>.
    /// Null, or the placeholder title, leaves the provider untouched.
    /// </param>
    public static IPlatformProvider Create(
        PlatformProviderMode mode = PlatformProviderMode.Auto,
        string? saveRoot = null,
        PlayFabConfiguration? playFab = null)
    {
        var provider = CreateCore(mode, saveRoot, playFab);

        // PlayFab is a layer, not a mode. Cloud save is orthogonal to
        // which transport and which platform runtime are in use, so making them a mode
        // would need one enum member per combination - LanPlayFab, GameCorePlayFab - and
        // a new one every time either axis grows. Wrapping instead means
        // "--platform=lan --playfab-title=<id>" composes a real UDP transport with real
        // cloud save without either side knowing about the other.
        //
        // A placeholder title id is skipped entirely rather than wrapped and disabled:
        // an unwrapped provider reports its capabilities honestly for free. GameCore is
        // also skipped, but for the opposite reason: it owns a GameCoreOnlineSession
        // directly, so wrapping it here would add a second PlayFabIdentityService and a
        // second login for the same player.
        return provider is GameCorePlatformProvider || playFab is null || playFab.IsPlaceholder
            ? provider
            : new PlayFabPlatformProvider(provider, playFab);
    }

    private static IPlatformProvider CreateCore(
        PlatformProviderMode mode,
        string? saveRoot,
        PlayFabConfiguration? playFab)
    {
        return mode switch
        {
            PlatformProviderMode.Offline => new OfflinePlatformProvider(saveRoot),

            PlatformProviderMode.GameCore => new GameCorePlatformProvider(saveRoot, playFab),

            // LAN: everything local from the offline provider, but a real UDP transport
            // instead of its no-op party service. This is the only mode in which a match
            // actually runs between two machines today; see docs/design-notes.md for why
            // the GDK/PlayFab path cannot.
            PlatformProviderMode.Lan => new LanPlatformProvider(new OfflinePlatformProvider(saveRoot)),

            // Auto: use the GDK when it is genuinely installed, otherwise run offline.
            // A dev box without the GDK is a supported configuration, so this is a
            // normal outcome and not a warning.
            PlatformProviderMode.Auto => GameCorePlatformProvider.IsRuntimePresent
                ? new GameCorePlatformProvider(saveRoot, playFab)
                : new OfflinePlatformProvider(saveRoot),

            _ => new OfflinePlatformProvider(saveRoot),
        };
    }

    /// <summary>
    /// Returns a writable directory for local saves and settings on console, or
    /// <see langword="null"/> when there is no GDK runtime to ask.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A packaged title's install folder is mapped read-only, so the obvious answer -
    /// a <c>LocalData</c> folder beside the executable - is silently wrong on hardware:
    /// <c>PlayerProfile.Save</c> throws <see cref="UnauthorizedAccessException"/> into its
    /// own catch on every write, and console settings simply never persist. The writable
    /// per-title location is persistent local storage, which survives title updates and
    /// is not cloud-synchronised - exactly the right shape for the local tier, which the
    /// cloud tier is layered on top of rather than replaced by.
    /// </para>
    /// <para>
    /// This lives on the composition root because it is the one place allowed to touch a
    /// concrete platform assembly. The game asks for a root before any provider exists -
    /// the provider is constructed *with* it - so it cannot come off
    /// <see cref="IPlatformProvider"/> without an initialisation cycle.
    /// </para>
    /// <para>
    /// <b>The Gaming Runtime has to be up before the call, and it is not yet.</b> This
    /// runs while the game object is being constructed, which is well before
    /// <c>GameCoreRuntime.InitializeAsync</c>; calling a GDK entry point before
    /// <c>XGameRuntimeInitialize</c> does not return an error, it dereferences state that
    /// was never created and takes the process out with an access violation - a crash on
    /// startup, on hardware only, with nothing in the log after "boot: platform mode".
    /// Hence <see cref="GameRuntimeHost"/>, which brings the runtime up here and lets the
    /// later owner find it already running instead of initialising it a second time.
    /// </para>
    /// <para>
    /// Persistent local storage also has to be declared in <c>MicrosoftGame.config</c> -
    /// see <c>packaging\MicrosoftGame.GDKX.*.config</c>. Without the element the title
    /// has no allocation and the call cannot succeed however correctly it is ordered.
    /// </para>
    /// </remarks>
    public static string? ResolveConsoleLocalDataRoot()
    {
        if (!GameCorePlatformProvider.IsRuntimePresent)
        {
            return null;
        }

        try
        {
            CrashLog.Mark("storage: initialising the Gaming Runtime");

            if (!GameRuntimeHost.EnsureInitialized())
            {
                return null;
            }

            CrashLog.Mark("storage: asking for persistent local storage");

            var path = GDK.Net.Storage.PersistentLocalStorage.GetPath();

            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Nothing here is worth failing a boot over: the caller keeps its previous
            // answer, which is no worse than it was before. Logged rather than swallowed,
            // because "settings never persist" is otherwise invisible on hardware.
            CrashLog.Mark($"storage: persistent local storage unavailable ({ex.GetType().Name}: {ex.Message})");

            return null;
        }
    }

    /// <summary>
    /// Resolves the mode from the command line, so a developer can force offline on a
    /// machine that does have the GDK: <c>--platform=offline</c>.
    /// </summary>
    public static PlatformProviderMode ResolveMode(IReadOnlyList<string> commandLineArgs)
    {
        foreach (var arg in commandLineArgs)
        {
            if (!arg.StartsWith("--platform=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = arg["--platform=".Length..];

            if (Enum.TryParse<PlatformProviderMode>(value, ignoreCase: true, out var mode))
            {
                return mode;
            }
        }

        return PlatformProviderMode.Auto;
    }
}

public enum PlatformProviderMode
{
    /// <summary>Use the native runtime when present, otherwise offline.</summary>
    Auto = 0,

    /// <summary>Force the offline provider, even with the GDK installed.</summary>
    Offline,

    /// <summary>Force the GDK provider; fails visibly when the runtime is missing.</summary>
    GameCore,

    /// <summary>
    /// Offline services with a real UDP transport, for LAN play and for testing the
    /// netcode between two processes without a GDK, a PlayFab title or an Xbox account.
    /// </summary>
    Lan,
}
