using System.Runtime.InteropServices;

namespace NetRumble.Platform.GameCore.Interop;

/// <summary>
/// Detects whether the Gaming Runtime can actually be called before GDK.Net is asked to
/// call it.
/// </summary>
/// <remarks>
/// <para>
/// Without this, the first call into a missing GDK throws from deep inside a service. A
/// dev box with no GDK is a supported configuration - it is how the practice match and
/// the whole front end are developed - so absence must be a clean, early, reported
/// condition.
/// </para>
/// <para>
/// <b>What is being probed.</b> <c>xgameruntime.thunks.dll</c>, the DLL GDK.Net binds
/// every core entry point to. There is no point probing <c>XGameRuntime.dll</c>: it
/// exports none of the GDK entry points on any machine, with or without the GDK
/// installed, because the flat API is linked in statically from <c>xgameruntime.lib</c>.
/// The thunks DLL is the redistributable that re-exports those stubs - shipped by the GDK
/// on desktop, and built by <c>console/XGameRuntimeThunks</c> for the console layout,
/// where the GDK ships no such DLL. See <c>docs/xbox-console-build.md</c>.
/// </para>
/// <para>
/// This replaced a probe of our own C++ shim, which also checked an ABI version and
/// <c>sizeof(XAsyncBlock)</c> against the managed declarations. Neither check has an
/// equivalent now and neither is needed: the shim was ours to get out of step with, while
/// the thunks DLL is a versioned redistributable and GDK.Net owns the struct layouts on
/// both sides of the boundary.
/// </para>
/// <para>
/// The probe result is cached: the answer cannot change during a run.
/// </para>
/// </remarks>
internal static class GameRuntimeProbe
{
    /// <summary>The DLL GDK.Net binds the Gaming Runtime's flat API to.</summary>
    internal const string Library = "xgameruntime.thunks.dll";

    /// <summary>
    /// Export used to confirm the loaded module really is the Gaming Runtime thunks
    /// rather than some other DLL that happens to share the name. Chosen because it is
    /// present in every GDK edition and in our console build of the same stubs.
    /// </summary>
    private const string SentinelExport = "XGameRuntimeInitialize";

    private static bool? _isAvailable;

    /// <summary>True when the GDK can be called in this process.</summary>
    internal static bool IsAvailable => _isAvailable ??= Probe();

    /// <summary>Why the runtime is unavailable, for logs. Empty when it is available.</summary>
    internal static string UnavailableReason { get; private set; } = string.Empty;

    private static bool Probe()
    {
        if (!OperatingSystem.IsWindows())
        {
            UnavailableReason = "The Microsoft GDK is only available on Windows.";
            return false;
        }

        if (!NativeLibrary.TryLoad(Library, out var handle))
        {
            UnavailableReason =
                $"'{Library}' could not be loaded. Install the Microsoft GDK, or - for a " +
                "console layout - make sure the build staged the thunks DLL beside the " +
                "executable.";
            return false;
        }

        if (!NativeLibrary.TryGetExport(handle, SentinelExport, out _))
        {
            NativeLibrary.Free(handle);
            UnavailableReason =
                $"A library named '{Library}' was found but does not export " +
                $"'{SentinelExport}'. It is not the Gaming Runtime thunks library.";
            return false;
        }

        // Left loaded on purpose: GDK.Net's P/Invoke marshaller resolves against this
        // same module, and unloading now would only force a second load.

        return true;
    }
}
