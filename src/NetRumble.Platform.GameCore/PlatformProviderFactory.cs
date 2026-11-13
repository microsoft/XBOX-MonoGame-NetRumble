using NetRumble.Platform.Diagnostics;
using NetRumble.Platform.GameCore;
using NetRumble.Platform.GameCore.Interop;
using NetRumble.Platform.PlayFab;

namespace NetRumble.Platform.Composition;

/// <summary>
/// The composition root: the one place that decides which provider the game runs on.
/// </summary>
/// <remarks>
/// <para>
/// This composition root selects the provider; game, gameplay, UI and netcode consume
/// <see cref="IPlatformProvider"/> instead of constructing native services themselves.
/// </para>
/// <para>
/// It is also the only assembly that references the concrete providers. Keeping it
/// separate from <c>NetRumble.Game</c> means a headless test host can exercise the
/// platform without dragging MonoGame in.
/// </para>
/// </remarks>
public static class PlatformProviderFactory
{
    /// <summary>
    /// Builds the GDK provider. Missing native dependencies are reported by its runtime.
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
    /// identity.
    /// Null, or the placeholder title, leaves the provider untouched.
    /// </param>
    public static IPlatformProvider Create(
        PlatformProviderMode mode = PlatformProviderMode.Auto,
        string? saveRoot = null,
        PlayFabConfiguration? playFab = null)
    {
        return mode switch
        {
            PlatformProviderMode.Auto or PlatformProviderMode.GameCore
                => new GameCorePlatformProvider(saveRoot, playFab),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unsupported platform mode."),
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
    /// Resolves the mode from the command line. Auto and GameCore both use the GDK.
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

            if (Enum.TryParse<PlatformProviderMode>(value, ignoreCase: true, out var mode)
                && Enum.IsDefined(mode))
            {
                return mode;
            }
        }

        return PlatformProviderMode.Auto;
    }
}

public enum PlatformProviderMode
{
    /// <summary>Use the GDK provider.</summary>
    Auto = 0,

    /// <summary>Force the GDK provider; fails visibly when the runtime is missing.</summary>
    GameCore = 2,
}
