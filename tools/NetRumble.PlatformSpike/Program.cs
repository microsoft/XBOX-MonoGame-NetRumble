using System.Diagnostics;
using NetRumble.Core;
using NetRumble.Platform;
using NetRumble.Platform.Composition;

namespace NetRumble.PlatformSpike;

/// <summary>
/// Phase 0 spike: exercises the platform abstraction end to end without MonoGame.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var mode = PlatformProviderFactory.ResolveMode(args);

        // Resolved from --playfab-title=<id> or NETRUMBLE_PLAYFAB_TITLE_ID. With a real
        // id the provider gains PlayFab cloud save and an entity id; with
        // the placeholder it is left untouched, which is the normal state of a dev box.
        var playFab = PlayFabConfiguration.Resolve(args);

        Console.WriteLine("NetRumble platform spike");
        Console.WriteLine("========================");
        Console.WriteLine($"Requested mode : {mode}");
        Console.WriteLine($"PlayFab title  : {(playFab.IsPlaceholder ? "(none configured)" : playFab.TitleId)}");
        Console.WriteLine();

        await using var platform = PlatformProviderFactory.Create(mode, saveRoot: null, playFab);

        Console.WriteLine($"Provider       : {platform.Name}");
        Console.WriteLine($"Capabilities   : {platform.Capabilities}");
        Console.WriteLine($"Runtime present: {platform.Runtime.IsRuntimeAvailable}");
        Console.WriteLine();

        var initialized = await StepInitialize(platform).ConfigureAwait(false);
        if (initialized)
        {
            await StepSignIn(platform).ConfigureAwait(false);
        }

        await StepLocalSave(platform).ConfigureAwait(false);
        StepCoreTypes();
        StepPumpBudget(platform);
        PhysicsChecks.Run();
        ArsenalChecks.Run();
        MatchChecks.Run();
        ReplicationChecks.Run();
        PartyTransportChecks.Run();
        PartyInteropChecks.Run(mode);
        XblInteropChecks.Run(mode);
        await PlatformServicesChecks.Run(platform).ConfigureAwait(false);
        await GameCorePartyAndActivityChecks.Run(mode).ConfigureAwait(false);
        await LanTransportChecks.Run().ConfigureAwait(false);
        await PlayFabRestChecks.Run().ConfigureAwait(false);
        FontCoverageChecks.Run();
        await IdentityChecks.Run().ConfigureAwait(false);
        MatchEndChecks.Run();

        Console.WriteLine();
        Console.WriteLine("Spike complete.");
        return 0;
    }

    private static async Task<bool> StepInitialize(IPlatformProvider platform)
    {
        Console.WriteLine("[1] Runtime initialize");

        var result = await platform.Runtime.InitializeAsync().ConfigureAwait(false);
        Report(result);

        if (result.Failed && !platform.Runtime.IsRuntimeAvailable)
        {
            // Expected on a box without the GDK. Not a spike failure.
            Console.WriteLine("    (no native runtime installed - this is a supported configuration)");
        }

        Console.WriteLine();
        return result.Succeeded;
    }

    private static async Task StepSignIn(IPlatformProvider platform)
    {
        Console.WriteLine("[2] Sign in");

        platform.Identity.SignInStageChanged += stage =>
        {
            if (stage.Length > 0)
            {
                Console.WriteLine($"    stage: {stage}");
            }
        };

        // The pump must run while sign-in is outstanding: GDK completions are only
        // delivered from Pump(), so awaiting without pumping would deadlock. This is
        // exactly what Game.Update will do every frame.
        var signInTask = platform.Identity.SignInAsync(SignInOptions.Interactive);
        await PumpUntil(platform, signInTask, TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        if (!signInTask.IsCompleted)
        {
            Console.WriteLine("    TIMED OUT waiting for sign-in");
            Console.WriteLine();
            return;
        }

        var result = await signInTask.ConfigureAwait(false);
        Report(result.Result);

        if (result.Succeeded && result.Value is { } user)
        {
            Console.WriteLine($"    gamertag : {user.DisplayName}");
            Console.WriteLine($"    xuid     : {user.XboxUserId}");
        }

        Console.WriteLine();
    }

    private static async Task StepLocalSave(IPlatformProvider platform)
    {
        Console.WriteLine("[3] Local save round-trip");

        var payload = "netrumble-spike"u8.ToArray();

        var save = await platform.GameSaves
            .SaveLocalAsync("spike", payload).ConfigureAwait(false);
        Report(save);

        var load = await platform.GameSaves
            .LoadLocalAsync("spike").ConfigureAwait(false);

        var roundTripped = load.Succeeded
            && load.Value is not null
            && load.Value.AsSpan().SequenceEqual(payload);

        Console.WriteLine($"    round-trip: {(roundTripped ? "OK" : "FAILED")}");
        Console.WriteLine();
    }

    /// <summary>
    /// Sanity-checks the ported enums, in particular the composite MatchState masks,
    /// which are the easiest thing to get wrong in the port.
    /// </summary>
    private static void StepCoreTypes()
    {
        Console.WriteLine("[4] Core type parity");

        Check("Loading is only Loading",
            MatchState.Loading.HasMatchState(MatchState.Loading));

        Check("Running is not Loading",
            !MatchState.Running.HasMatchState(MatchState.Loading));

        Check("PlayersJoining is Waiting",
            MatchState.PlayersJoining.HasMatchState(MatchState.Waiting));

        Check("WarmingUp is Waiting",
            MatchState.WarmingUp.HasMatchState(MatchState.Waiting));

        Check("Running is Playable",
            MatchState.Running.HasMatchState(MatchState.Playable));

        // Starting (8) is NOT in the Playable mask (19 == 16|2|1). This is easy to get
        // wrong when porting and is asserted here on purpose.
        Check("Starting is NOT Playable",
            !MatchState.Starting.HasMatchState(MatchState.Playable));

        Check("MatchComplete is not Playable",
            !MatchState.MatchComplete.HasMatchState(MatchState.Playable));

        Check("Waiting mask == 3", (int)MatchState.Waiting == 3);
        Check("Playable mask == 19", (int)MatchState.Playable == 19);
        Check("World is 2400 square",
            NRConst.WorldWidth == 2400 && NRConst.WorldHeight == 2400);

        Console.WriteLine();
    }

    /// <summary>
    /// Measures the per-frame cost of the pump. It runs inside Update, so it has to be
    /// negligible against a 16.6 ms budget.
    /// </summary>
    private static void StepPumpBudget(IPlatformProvider platform)
    {
        Console.WriteLine("[5] Pump cost");

        const int iterations = 10_000;
        var stopwatch = Stopwatch.StartNew();

        for (var i = 0; i < iterations; i++)
        {
            platform.Runtime.Pump();
        }

        stopwatch.Stop();

        var perCallUs = stopwatch.Elapsed.TotalMilliseconds * 1000.0 / iterations;
        Console.WriteLine($"    {perCallUs:F2} us per idle pump ({iterations:N0} calls)");
        Console.WriteLine();
    }

    /// <summary>
    /// Drives <see cref="IPlatformRuntime.Pump"/> while waiting, standing in for the
    /// game loop.
    /// </summary>
    private static async Task PumpUntil(IPlatformProvider platform, Task task, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            platform.Runtime.Pump();
            await Task.Delay(16).ConfigureAwait(false);
        }

        platform.Runtime.Pump();
    }

    private static void Report(PlatformResult result)
        => Console.WriteLine($"    {(result.Succeeded ? "OK" : "--")} {result}");

    private static void Check(string label, bool condition)
        => Console.WriteLine($"    {(condition ? "PASS" : "FAIL")} {label}");
}
