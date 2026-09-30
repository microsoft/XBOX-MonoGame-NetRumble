using NetRumble.Platform.Composition;
using NetRumble.Platform.Diagnostics;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NetRumble.Game;

/// <summary>
/// Entry point. Replaces <c>Platforms/windows/Core/Main.cpp</c> from the C++ sample and
/// <c>scripts/main.gd</c> from the Godot port.
/// </summary>
internal static partial class Program
{
#if GDKX
    private static void Main()
    {
        MainCore([]);
    }
#else
    [STAThread]
    private static void Main(string[] args)
    {
        MainCore(args);
    }
#endif

    private static void MainCore(string[] args)
    {
        InstallCrashLog();

        try
        {
            // The provider is chosen here and nowhere else. Pass a "platform=" switch to
            // force one; by default the GDK is used when genuinely installed and the
            // offline provider otherwise.
            var mode = PlatformProviderFactory.ResolveMode(args);

            CrashLog.Mark($"boot: platform mode {mode}");

            using var game = new NetRumbleGame(mode, args);
            game.Run();

            CrashLog.Mark("boot: clean exit");
        }
        catch (Exception ex)
        {
            LogUnhandledException(ex);
            throw;
        }
    }

    /// <summary>
    /// Points <see cref="CrashLog"/> at somewhere writable and gives it the debugger
    /// channel to echo through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The console roots are ordered for retrieval, not for availability. <c>D:\</c> is
    /// the developer scratch partition: devkit-only, absent on retail, and - the reason it
    /// is first - reachable from the build machine with <c>xbcp xd:\NetRumble\...</c> after
    /// the title has exited. <c>T:\</c>, the per-title temporary drive every Xbox title
    /// gets unconditionally, is the retail-safe fallback, but a title's scratch is not
    /// something a PC can browse, so a log written there is only useful to a debugger that
    /// was already attached. A crash with no debugger attached is the case this exists for.
    /// </para>
    /// <para>
    /// The install folder is not a candidate at all on console, because a packaged title
    /// has it mapped read-only; LocalAppData covers the desktop build instead.
    /// </para>
    /// </remarks>
    private static void InstallCrashLog()
    {
#if GDKX
        CrashLog.Echo = static line => OutputDebugString("[NetRumble] " + line + "\r\n");
        CrashLog.Install(@"D:\NetRumble", @"T:\NetRumble");
#else
        CrashLog.Echo = static line => Debug.WriteLine("[NetRumble] " + line);
        CrashLog.Install(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetRumble",
            "logs"));
#endif
    }

    private static void LogUnhandledException(Exception ex)
    {
        var message = "[NetRumble] Unhandled exception before NativeAOT failfast:"
            + Environment.NewLine
            + ex;
        Debug.WriteLine(message);

        CrashLog.Fatal("Program.MainCore", ex);

#if GDKX
        OutputDebugString(message);
#endif
    }

#if GDKX
    [LibraryImport("api-ms-win-core-debug-l1-1-0", EntryPoint = "OutputDebugStringW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial void OutputDebugString(string message);
#endif
}
