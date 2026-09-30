using System.Runtime.InteropServices;

namespace NetRumble.Platform.GameCore.Interop;

/// <summary>
/// Detects whether the native PlayFab Party library is present, before any P/Invoke.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="GameRuntimeProbe"/> on purpose. Party ships as its own
/// redistributable (<c>Party.dll</c>, plus <c>PlayFabCore.dll</c> and
/// <c>libHttpClient.dll</c>) and is versioned independently of the GDK, so a machine can
/// easily have one and not the other. Collapsing the two probes would make an online
/// build fail with a misleading "install the GDK" message when the real problem is a
/// missing Party redistributable.
/// </para>
/// <para>
/// Unlike <c>XGameRuntime.dll</c>, there is no known Windows system DLL called
/// <c>Party.dll</c> to be confused with - but the sentinel export check is kept anyway.
/// The name is generic enough that some other library could plausibly claim it, and the
/// check costs nothing.
/// </para>
/// </remarks>
internal static class PartyRuntimeProbe
{
    /// <summary>
    /// The PlayFab Party redistributable. GDK.Net binds its own Party projection to the
    /// same module; this probe only needs the name to answer whether it is present at
    /// all before anything tries to call into it.
    /// </summary>
    private const string Library = "Party.dll";

    /// <summary>
    /// Confirmed present in the exports of Party.dll 2.3.0 (x64). It is also the first
    /// function the title calls, so a build where this resolves but later calls do not is
    /// a version mismatch worth failing loudly on.
    /// </summary>
    private const string SentinelExport = "PartyInitialize";

    private static bool? _isAvailable;

    /// <summary>True when the Party runtime can be loaded in this process.</summary>
    internal static bool IsAvailable => _isAvailable ??= Probe();

    /// <summary>Why the runtime is unavailable, for logs. Empty when it is available.</summary>
    internal static string UnavailableReason { get; private set; } = string.Empty;

    private static bool Probe()
    {
        if (!OperatingSystem.IsWindows())
        {
            UnavailableReason = "PlayFab Party is only supported on Windows in this build.";
            return false;
        }

        // Checked before the module is even loaded, because the ordering it enforces is
        // not optional and not recoverable. The shipped Party, PlayFabCore and
        // libHttpClient binaries are GDK builds: each statically links a Gaming Runtime
        // shim that refuses every call with E_GAMERUNTIME_NOT_INITIALIZED (0x89240100)
        // until XGameRuntimeInitialize has succeeded. The refusal is only polite one level
        // down - PartyInitialize itself dereferences the PlayFabCore state it never got
        // and takes the process out with an access violation.
        //
        // No import table reveals this, because the shim is statically linked rather than
        // imported. It was found by calling the real DLL and then reading the diagnostic
        // string sitting next to the error constant in the binary.
        if (!GameRuntimeProbe.IsAvailable)
        {
            UnavailableReason =
                "PlayFab Party needs the Microsoft GDK, which is not available: " +
                GameRuntimeProbe.UnavailableReason;
            return false;
        }

        if (!NativeLibrary.TryLoad(Library, out var handle))
        {
            UnavailableReason =
                $"'{Library}' could not be loaded. The PlayFab Party redistributable " +
                "must sit next to the executable for online play.";
            return false;
        }

        if (!NativeLibrary.TryGetExport(handle, SentinelExport, out _))
        {
            NativeLibrary.Free(handle);
            UnavailableReason =
                $"A library named '{Library}' was found but does not export " +
                $"'{SentinelExport}', so it is not the PlayFab Party redistributable.";
            return false;
        }

        // Left loaded: GDK.Net's Party bindings resolve against this same module.
        return true;
    }
}
