using System.Reflection;
using System.Runtime.InteropServices;
using GDK.Net.PlayFab.Party;
using NetRumble.Platform;
using NetRumble.Platform.Composition;
using NetRumble.Platform.GameCore.Interop;

/// <summary>
/// Drives the real vendored <c>Party.dll</c> through GDK.Net.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this file is for now.</b> It used to declare its own P/Invoke signatures and
/// prove them against the real binary, because the port owned every binding and a
/// marshalling mistake was this harness's problem to catch. GDK.Net owns the bindings
/// today and has its own tests for them, so struct layouts, UTF-8 marshalling and the
/// state-change pump's double indirection are no longer this file's business. Three
/// things remain that no test inside GDK.Net can prove for this repository, and they are
/// what is checked below:
/// </para>
/// <list type="number">
///   <item><b>MGRumble's own staging is correct</b> - the vendored redistributable in
///     <c>third_party/party/</c> is found, loads, and brings its siblings
///     (<c>PlayFabCore.dll</c>, <c>libHttpClient.dll</c>) with it.</item>
///   <item><b>The initialization order hazard is now prevented rather than merely
///     documented.</b> This is the important one. These are GDK builds of Party and
///     PlayFabCore: each statically links a Gaming Runtime shim, and calling
///     <c>PartyInitialize</c> before <c>XGameRuntimeInitialize</c> does not fail politely -
///     <c>PartyInitialize</c> dereferences PlayFabCore state it never got and takes the
///     process out with an access violation. No import table reveals this, because the
///     shim is statically linked rather than imported; it was originally found by calling
///     the real DLL and reading the diagnostic string sitting beside the error constant in
///     the binary. GDK.Net's <c>RuntimeLifetime.RequireGameRuntime</c> now turns that
///     access violation into an <see cref="InvalidOperationException"/> thrown before any
///     native code runs. That promise is worth asserting, and - unlike almost everything
///     else here - it can be asserted on a machine with no GDK at all, because a machine
///     with no GDK is exactly the condition it guards.</item>
///   <item><b>Argument validation still rejects before dispatch</b>, so a misconfigured
///     title cannot reach the native library.</item>
/// </list>
/// <para>
/// Everything past initialization - creating a local user, a network, endpoints - needs a
/// real PlayFab title, a signed-in player and a live service, none of which exist here.
/// Those paths are proven instead at the service layer by
/// <c>GameCorePartyAndActivityChecks</c>, which asserts the thing that actually can be
/// proven without them: that every one of them fails cleanly rather than throwing or
/// crashing. See <c>docs/design-notes.md</c>.
/// </para>
/// </remarks>
internal static class PartyInteropChecks
{
    /// <summary>The PlayFab Party redistributable, vendored in <c>third_party/party/</c>.</summary>
    private const string PartyLibrary = "Party.dll";

    /// <summary>
    /// The configured PlayFab title, or the placeholder until a real one is supplied.
    /// </summary>
    /// <remarks>
    /// These checks work with either: nothing below gets far enough to have the id
    /// validated, which happens on the service at the first authenticated operation.
    /// </remarks>
    private static readonly PlayFabConfiguration Configuration =
        PlayFabConfiguration.Resolve(Environment.GetCommandLineArgs());

    public static void Run(PlatformProviderMode mode)
    {
        if (!GdkCheckGate.ShouldRun(mode, "[10] Party through GDK.Net (real Party.dll)"))
        {
            return;
        }

        Console.WriteLine("[10] Party through GDK.Net (real Party.dll)");

        // Party imports the WinRT and COM API sets, and the harness reaches this point on
        // a thread-pool thread continuing an await. Give it a dedicated MTA thread with
        // COM explicitly initialized rather than assuming the ambient one will do.
        var thread = new Thread(RunOnDedicatedThread, 1024 * 1024);
        if (OperatingSystem.IsWindows())
        {
            thread.SetApartmentState(ApartmentState.MTA);
        }

        thread.IsBackground = true;
        thread.Start();
        thread.Join();
    }

    private static void RunOnDedicatedThread()
    {
        var comInitialized = CoInitializeEx(nint.Zero, CoinitMultithreaded) >= 0;

        try
        {
            RunChecks();
        }
        finally
        {
            if (comInitialized)
            {
                CoUninitialize();
            }
        }
    }

    /// <summary>COINIT_MULTITHREADED.</summary>
    private const uint CoinitMultithreaded = 0x0;

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    private static void RunChecks()
    {
        var libraryPath = FindPartyLibrary();
        if (libraryPath is null)
        {
            // Not a failure. The redistributable is not required to build or to run an
            // offline match, and this harness must stay runnable on a machine without it.
            Console.WriteLine("    SKIP Party.dll not found; Party checks skipped");
            Console.WriteLine();
            return;
        }

        Console.WriteLine($"    ---- using {libraryPath}");
        Console.WriteLine(
            $"    ---- PlayFab title '{Configuration.TitleId}'" +
            (Configuration.IsPlaceholder ? " (placeholder)" : string.Empty));
        RedirectPartyLoads(libraryPath);

        if (!Check("Party.dll loads and exports PartyInitialize", TryPreload(libraryPath)))
        {
            Console.WriteLine();
            return;
        }

        ArgumentValidation();
        InitializationOrder();
        DescriptorRejection();

        Console.WriteLine();
    }

    /// <summary>
    /// Rejection happens in managed code, before the title id can reach the library.
    /// </summary>
    /// <remarks>
    /// Worth a check of its own because the placeholder title id is a real state this
    /// build spends most of its life in - see <see cref="PlayFabConfiguration"/> - and
    /// the provider is required to refuse at the door rather than let a meaningless id
    /// travel several async hops into the SDK.
    /// </remarks>
    private static void ArgumentValidation()
    {
        var rejected = false;
        try
        {
            PartyManager.Initialize(string.Empty);
        }
        catch (ArgumentException)
        {
            rejected = true;
        }
        catch (InvalidOperationException)
        {
            // The order guard fired first. That is a correct outcome too - it is asserted
            // properly by InitializationOrder below - but it means this check proved
            // nothing, so it must not be reported as a pass.
            Console.WriteLine(
                "    ---- the Gaming Runtime guard fired before the argument check, so " +
                "the empty-title-id rejection could not be observed independently");
            return;
        }

        Check("PartyManager.Initialize rejects an empty title id", rejected);
    }

    /// <summary>
    /// The crash hazard is prevented, not merely documented. See the type remarks.
    /// </summary>
    private static void InitializationOrder()
    {
        if (GameRuntimeProbe.IsAvailable)
        {
            // The guard cannot be observed once the Gaming Runtime is up, and forcing it
            // down again to see it is not something a harness should do to a shared
            // process. Reported rather than silently skipped, so a green run on a GDK
            // machine is not mistaken for having exercised this.
            Console.WriteLine(
                "    ---- the Gaming Runtime is available here, so the pre-initialization " +
                "guard cannot be observed; it is asserted on GDK-less machines instead");
            return;
        }

        var guarded = false;
        var crashedDifferently = string.Empty;

        try
        {
            PartyManager.Initialize(Configuration.TitleId);
        }
        catch (InvalidOperationException)
        {
            guarded = true;
        }
        catch (Exception ex)
        {
            crashedDifferently = $"{ex.GetType().Name}: {ex.Message}";
        }

        Check(
            "PartyManager.Initialize refuses to run before the Gaming Runtime is up" +
            (crashedDifferently.Length > 0 ? $" (got {crashedDifferently})" : string.Empty),
            guarded);
    }

    /// <summary>
    /// Deserializing garbage is rejected rather than accepted or fatal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The surviving half of the old round-trip test. A real descriptor needs a live
    /// network, so only the rejecting direction can be exercised - but that is the
    /// direction that matters for the invite path, where the string arrives from outside
    /// the process: <c>GameCoreActivityService</c> hands whatever a protocol activation
    /// carried straight to the join path, and a malformed one must produce a refusal the
    /// player can act on rather than an exception nobody catches.
    /// </para>
    /// <para>
    /// The old file allocated an over-sized stack buffer and checked a canary either side
    /// of it, because it owned the buffer and Party wrote into it. GDK.Net owns that
    /// buffer now, so there is nothing here left to overrun - which is the point of
    /// having moved.
    /// </para>
    /// </remarks>
    private static void DescriptorRejection()
    {
        var rejected = false;
        var detail = string.Empty;

        try
        {
            PartyNetworkDescriptor.Deserialize("not-a-real-descriptor");
        }
        catch (Exception ex)
        {
            // Any exception is a pass: what is being proved is that nonsense does not
            // come back as a usable descriptor and does not take the process down. Which
            // exception GDK.Net chooses is its business, and pinning it here would make
            // this harness brittle against a library it does not own.
            rejected = true;
            detail = ex.GetType().Name;
        }

        Check(
            $"PartyNetworkDescriptor.Deserialize rejects malformed input" +
            (detail.Length > 0 ? $" ({detail})" : string.Empty),
            rejected);
    }

    /// <summary>
    /// Points the marshaller at the vendored redistributable.
    /// </summary>
    /// <remarks>
    /// The shipping game puts Party next to its executable and needs none of this. The
    /// resolver is installed on GDK.Net's assembly rather than this port's, because that
    /// is where the <c>DllImport</c>s live now.
    /// </remarks>
    private static void RedirectPartyLoads(string libraryPath)
    {
        var bindingAssembly = typeof(PartyManager).Assembly;
        NativeLibrary.SetDllImportResolver(bindingAssembly, (name, assembly, searchPath) =>
            name == PartyLibrary && NativeLibrary.TryLoad(libraryPath, out var loaded)
                ? loaded
                : nint.Zero);
    }

    private static bool TryPreload(string libraryPath)
    {
        // Loading by full path also brings PlayFabCore.dll and libHttpClient.dll in from
        // the same folder, which the default probing order would not find.
        return NativeLibrary.TryLoad(libraryPath, out var handle)
            && NativeLibrary.TryGetExport(handle, "PartyInitialize", out _);
    }

    /// <summary>Walks up from the binaries looking for the vendored redistributable.</summary>
    private static string? FindPartyLibrary()
    {
        // Preferred: the copy the build staged next to this executable. Party loads its
        // own siblings (PlayFabCore, libHttpClient) through the normal search order, so
        // they must be beside the binary - resolving Party alone from elsewhere leaves it
        // to fault when it reaches for them.
        var staged = Path.Combine(AppContext.BaseDirectory, PartyLibrary);
        if (File.Exists(staged))
        {
            return staged;
        }

        var directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        while (!string.IsNullOrEmpty(directory))
        {
            var candidate = Path.Combine(directory, "third_party", "party", PartyLibrary);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    private static bool Check(string label, bool condition)
    {
        Console.WriteLine($"    {(condition ? "PASS" : "FAIL")} {label}");
        return condition;
    }
}
