using System.Runtime.InteropServices;

namespace NetRumble.Platform.GameCore.Interop;

/// <summary>
/// Detects whether Xbox Services (XSAPI) is present, before any P/Invoke.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="GameRuntimeProbe"/> for the same reason as
/// <see cref="PartyRuntimeProbe"/>: <c>Microsoft.Xbox.Services.C.Thunks.dll</c> is its
/// own redistributable and can be missing independently of the GDK proper.
/// </para>
/// <para>
/// <b>Ordering matters here too, and for the same underlying reason as Party.</b>
/// XSAPI's Gaming Runtime shim also refuses everything with
/// <c>E_GAMERUNTIME_NOT_INITIALIZED</c> (<c>0x89240100</c>) until
/// <c>XGameRuntimeInitialize</c> has run, so <see cref="GameRuntimeProbe.IsAvailable"/>
/// is checked first. Whether XSAPI additionally requires a signed-in
/// <c>XUserHandle</c> before <c>XblContextCreateHandle</c> will succeed was not verified
/// end to end here - see <c>docs/design-notes.md</c> - but load-time and initialize-time
/// probing behave identically to Party regardless, so the same defensive ordering is
/// used.
/// </para>
/// </remarks>
internal static class XblRuntimeProbe
{
    /// <summary>
    /// The Xbox Services redistributable. GDK.Net binds its XSAPI projection to the same
    /// module; this probe only needs the name to answer whether it is present at all.
    /// </summary>
    private const string Library = "Microsoft.Xbox.Services.C.Thunks.dll";

    /// <summary>Export used to confirm the loaded module really is XSAPI.</summary>
    private const string SentinelExport = "XblInitialize";

    private static bool? _isAvailable;

    /// <summary>True when Xbox Services can be loaded in this process.</summary>
    internal static bool IsAvailable => _isAvailable ??= Probe();

    /// <summary>Why the runtime is unavailable, for logs. Empty when it is available.</summary>
    internal static string UnavailableReason { get; private set; } = string.Empty;

    private static bool Probe()
    {
        if (!OperatingSystem.IsWindows())
        {
            UnavailableReason = "Xbox Services is only supported on Windows in this build.";
            return false;
        }

        if (!GameRuntimeProbe.IsAvailable)
        {
            UnavailableReason =
                "Xbox Services needs the Microsoft GDK, which is not available: " +
                GameRuntimeProbe.UnavailableReason;
            return false;
        }

        if (!NativeLibrary.TryLoad(Library, out var handle))
        {
            UnavailableReason =
                $"'{Library}' could not be loaded. The Xbox Services " +
                "redistributable must sit next to the executable for achievements, social, " +
                "privacy and moderation features.";
            return false;
        }

        if (!NativeLibrary.TryGetExport(handle, SentinelExport, out _))
        {
            NativeLibrary.Free(handle);
            UnavailableReason =
                $"A library named '{Library}' was found but does not export " +
                $"'{SentinelExport}', so it is not the Xbox Services redistributable.";
            return false;
        }

        // Left loaded: GDK.Net's XSAPI bindings resolve against this same module.
        return true;
    }
}
