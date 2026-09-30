using NetRumble.Platform.Composition;

/// <summary>
/// Decides whether the checks that drive the native GDK stack directly should run.
/// </summary>
/// <remarks>
/// <para>
/// Most checks in this harness go through <see cref="NetRumble.Platform.IPlatformProvider"/>
/// and therefore test whatever provider <c>--platform</c> selected. Three do not:
/// <see cref="PartyInteropChecks"/>, <see cref="XblInteropChecks"/> and
/// <c>GameCorePartyAndActivityChecks</c> construct GDK-backed objects and P/Invoke the
/// vendored redistributables on purpose, because proving those bindings load and
/// degrade cleanly is the whole point of them.
/// </para>
/// <para>
/// That made them wrong under <c>--platform=offline</c> in two different ways. On a
/// machine with the GDK they ran anyway and reported PASS, so the run did not mean what
/// it said - offline was never actually exercised end to end. On a machine without it
/// they ran anyway and reported FAIL, turning "this box has no GDK", which is a
/// supported and expected configuration, into a harness failure. Asking for the offline
/// provider is an explicit statement that the GDK is not in play, so these are skipped
/// rather than run.
/// </para>
/// <para>
/// <see cref="PlatformProviderMode.Auto"/> still runs them: Auto means "use the GDK when
/// it is genuinely installed", so the checks are the right thing to attempt, and each
/// already has its own SKIP path for the redistributable being absent.
/// </para>
/// </remarks>
internal static class GdkCheckGate
{
    /// <summary>
    /// True when the requested mode can legitimately touch the GDK.
    /// <see cref="PlatformProviderMode.Lan"/> is excluded with Offline: it composes a
    /// real UDP transport over the offline provider and deliberately never initializes
    /// the Gaming Runtime.
    /// </summary>
    public static bool RunsNativeGdk(PlatformProviderMode mode)
        => mode is PlatformProviderMode.Auto or PlatformProviderMode.GameCore;

    /// <summary>
    /// Prints the standard skip banner for a check that needs the GDK, and returns false
    /// so the caller can <c>return</c> immediately.
    /// </summary>
    public static bool ShouldRun(PlatformProviderMode mode, string checkHeading)
    {
        if (RunsNativeGdk(mode))
        {
            return true;
        }

        Console.WriteLine(checkHeading);
        Console.WriteLine(
            $"    SKIP --platform={mode.ToString().ToLowerInvariant()} does not use the GDK; " +
            "these checks drive the native runtime directly and would report a result " +
            "that says nothing about the provider under test.");
        Console.WriteLine();
        return false;
    }
}
