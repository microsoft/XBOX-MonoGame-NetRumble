using GDK.Net;
using GDK.Net.SystemInfo;
using GDK.Net.Users;
using GDK.Net.XboxLive;
using NetRumble.Platform.Composition;
using NetRumble.Platform.GameCore.Interop;

/// <summary>
/// Drives the real <c>Microsoft.Xbox.Services.C.Thunks.dll</c> through GDK.Net, the same
/// way <see cref="PartyInteropChecks"/> drives the real <c>Party.dll</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What changed when the port moved onto GDK.Net.</b> This file used to declare its
/// own <c>XblInitArgs</c> and P/Invoke <c>XblInitialize</c> directly, because the port
/// owned every binding and a struct layout mistake was the harness's problem to catch.
/// GDK.Net owns the bindings now and has its own tests for them, so proving the
/// marshalling is no longer this file's job. What is still worth proving here - and what
/// no unit test of GDK.Net can prove for this repo - is that <em>MGRumble's</em>
/// redistributable staging is correct: that the DLLs this project copies beside the
/// executable are found, load, and initialize against a SCID derived the way
/// <c>GameCoreXblContext</c> derives it.
/// </para>
/// <para>
/// Two checks that used to exist are deliberately gone rather than ported:
/// <c>XblContextCreateHandle</c> with a null user handle cannot be expressed at all now -
/// <see cref="XboxLiveService.CreateContext"/> takes a <see cref="User"/> and throws
/// <see cref="ArgumentNullException"/> before reaching native code - and that is a
/// strictly better outcome than the runtime check it replaces. The struct-layout check
/// went with the struct.
/// </para>
/// <para>
/// Everything past initialization still needs a signed-in Xbox account, which this
/// environment does not have - see <c>docs/design-notes.md</c>. Those paths are proven
/// instead at the service layer by <c>PlatformServicesChecks</c>, which asserts the thing
/// that actually can be proven without an account: that every one of them fails cleanly
/// rather than throwing or corrupting memory.
/// </para>
/// </remarks>
internal static class XblInteropChecks
{
    public static void Run(PlatformProviderMode mode)
    {
        const string Heading = "[11] Xbox Services through GDK.Net (real Microsoft.Xbox.Services.C.Thunks.dll)";

        if (!GdkCheckGate.ShouldRun(mode, Heading))
        {
            return;
        }

        Console.WriteLine(Heading);

        if (!XblRuntimeProbe.IsAvailable)
        {
            // Not a failure - see XblRuntimeProbe's own remarks: this redistributable
            // is not required to build or to run offline, only staged for this harness
            // by NetRumble.PlatformSpike.csproj when the GDK is installed.
            Console.WriteLine($"    SKIP {XblRuntimeProbe.UnavailableReason}");
            Console.WriteLine();
            return;
        }

        GameRuntime? runtime = null;

        try
        {
            // XSAPI refuses everything with E_GAMERUNTIME_NOT_INITIALIZED until this has
            // run - the same ordering PartyRuntimeProbe documents at length for Party.
            runtime = GameRuntime.Initialize();
            Check("GameRuntime.Initialize succeeds", true);
        }
        catch (Exception ex) when (ex is GameRuntimeException
                                      or DllNotFoundException
                                      or EntryPointNotFoundException
                                      or PlatformNotSupportedException)
        {
            Console.WriteLine($"    SKIP the Gaming Runtime could not be started: {ex.Message}");
            Console.WriteLine();
            return;
        }

        try
        {
            uint titleId;
            try
            {
                titleId = GameLauncher.GetXboxTitleId();
                Check("GameLauncher.GetXboxTitleId succeeds", true);
            }
            catch (GameRuntimeException ex)
            {
                Check($"GameLauncher.GetXboxTitleId succeeds ({ex.Message})", false);
                Console.WriteLine();
                return;
            }

            // See GameCoreXblContext's remarks for why this exact zero-padded shape is
            // used - it is the Godot addon's documented fallback for a title with no SCID
            // override, not something this environment could confirm end-to-end.
            var scid = $"00000000-0000-0000-0000-0000{titleId:x8}";
            Console.WriteLine($"    ---- derived SCID '{scid}' from title id 0x{titleId:x8}");

            try
            {
                runtime.XboxLive.Initialize(new XboxLiveOptions { Scid = scid });
                Check("XboxLive.Initialize succeeds against the staged redistributable", true);
            }
            catch (Exception ex) when (ex is GameRuntimeException
                                          or ArgumentException
                                          or InvalidOperationException)
            {
                Check($"XboxLive.Initialize succeeds against the staged redistributable ({ex.Message})", false);
                Console.WriteLine();
                return;
            }

            // Round-trips through XblGetScid, so this proves the SCID above actually
            // reached the native library rather than merely being accepted by GDK.Net.
            Check(
                "XboxLive.Scid round-trips the SCID that was passed in",
                string.Equals(runtime.XboxLive.Scid, scid, StringComparison.OrdinalIgnoreCase));

            Console.WriteLine(
                "    ---- everything past this point (achievements, social, privacy, " +
                "moderation calls) needs an XboxLiveContext bound to a signed-in Xbox " +
                "account, which is not available in this environment. See docs/design-notes.md.");
        }
        finally
        {
            // Unlike the old file, which deliberately skipped XblCleanupAsync because it
            // had no way to pump the completion, GDK.Net's disposal covers the whole
            // sequence - so this harness now exercises the shutdown path too.
            runtime.Dispose();
        }

        Console.WriteLine();
    }

    private static bool Check(string label, bool condition)
    {
        Console.WriteLine($"    {(condition ? "PASS" : "FAIL")} {label}");
        return condition;
    }
}
