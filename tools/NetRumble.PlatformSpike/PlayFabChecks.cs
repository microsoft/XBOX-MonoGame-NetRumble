using System.Reflection;
using GDK.Net;
using GDK.Net.PlayFab;
using NetRumble.Platform;
using NetRumble.Platform.Networking;
using NetRumble.Platform.PlayFab;
using GDK.Net.PlayFab.Multiplayer;
using GDK.Net.PlayFab.Party;
using NetRumble.Platform.GameCore;
using NetRumble.Platform.GameCore.Interop;
using NetRumble.Platform.GameCore.Services;

namespace NetRumble.PlatformSpike;

/// <summary>Native-independent checks for the GDK.Net PlayFab adapter.</summary>
internal static class PlayFabChecks
{
    public static async Task<int> Run()
    {
        var failures = 0;
        void Check(string label, bool condition)
        {
            Console.WriteLine($"    {(condition ? "PASS" : "FAIL")} {label}");
            if (!condition)
            {
                failures++;
            }
        }

        Console.WriteLine("[15] PlayFab GDK.Net adapters");
        var config = new PlayFabConfiguration { TitleId = "A1B2C" };
        var placeholderConfig = new PlayFabConfiguration { TitleId = PlayFabConfiguration.PlaceholderTitleId };
        using (var placeholder = new PlayFabOnlineServices(placeholderConfig))
        {
            var login = await placeholder.SignInWithXboxUserAsync(null!, createAccount: false);
            Check("placeholder refuses login before any native call", login.Status == PlatformStatus.Unavailable);
            Check("a placeholder cannot publish an online identity", !placeholder.IsSignedIn && placeholder.EntityId.Length == 0);
        }
        using (var services = new PlayFabOnlineServices(config))
        {
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            var login = await services.SignInWithXboxUserAsync(null!, createAccount: false, canceled.Token);
            Check("canceled login never initializes native PlayFab", login.WasCanceled);
            var notifications = 0;
            services.SessionChanged += session => { if (session is null) { notifications++; } };
            services.SignOut();
            Check("sign-out clears identity and notifies subscribers", notifications == 1 && services.Session is null);
            await services.ShutdownAsync();
            Check("shutdown refuses further entity leases", services.AcquireEntity() is null);
        }

        await AccountSwitchFailureCheck(config, Check);
        TokenRefreshFailureChecks(config, Check);
        await ProviderShutdownChecks(config, Check);
        LobbyDisconnectChecks(config, Check);
        await PartyTeardownBudgetChecks(Check);
        await PartyPumpTeardownChecks(Check);
        PartyReentrantResetChecks(Check);
        PartyRegionResetChecks(Check);
        await PartyAttemptCancellationChecks(Check);
        await PartyEarlyAuthenticationChecks(Check);
        await PartyLateLobbyChecks(Check);

        Check("adult account creation remains allowed", PlatformAgeGroup.Adult.AllowsSilentAccountCreation());
        Check("child account creation remains forbidden", !PlatformAgeGroup.Child.AllowsSilentAccountCreation());
        Check("unknown age cannot create a publisher account", !PlatformAgeGroup.Unknown.AllowsSilentAccountCreation());
        Check("banned accounts map to NoPrivilege",
            Map(PlayFabErrors.AccountBanned).Status == PlatformStatus.NoPrivilege);
        Check("missing publisher accounts map to NoPrivilege",
            Map(PlayFabErrors.AccountNotFound).Status == PlatformStatus.NoPrivilege);
        Check("invalid titles map to Unavailable",
            Map(PlayFabErrors.InvalidTitleId).Status == PlatformStatus.Unavailable);
        Check("unactivated titles map to Unavailable",
            Map(PlayFabErrors.TitleNotActivated).Status == PlatformStatus.Unavailable);
        Check("service outages remain retryable network failures",
            Map(PlayFabErrors.ServiceUnavailable).Status == PlatformStatus.NetworkFailure);
        Check("throttling remains retryable",
            Map(PlayFabErrors.LobbyRateLimitExceeded).Status == PlatformStatus.NetworkFailure);
        Check("HTTP-stack timeouts retain TimedOut",
            Map(unchecked((int)0x80072EE2)).Status == PlatformStatus.TimedOut);
        Check("HTTP-stack connection failures retain NetworkFailure",
            Map(unchecked((int)0x80072EFD)).Status == PlatformStatus.NetworkFailure);
        Check("expired native sign-in maps to NotSignedIn",
            Map(PlayFabErrors.Noentitytoken).Status == PlatformStatus.NotSignedIn);
        foreach (var error in new[] { PlayFabErrors.LobbyNotJoinable, PlayFabErrors.LobbyDoesNotExist })
        {
            var exception = new PlayFabException(error);
            var refusal = PlayFabFailure.FromException(exception, "The request failed.");
            Check($"{error:X8}: unavailable lobbies map to SessionNotJoinable",
                refusal.Failed && refusal.Status == PlatformStatus.SessionNotJoinable);
            Check($"{error:X8}: unavailable lobbies retain the player-facing refusal",
                refusal.Message == "This match is full or no longer accepts players.");
            Check($"{error:X8}: unavailable lobbies retain native diagnostics",
                refusal.Diagnostics == exception.Message);
            var typedRefusal = PlayFabFailure.For<PlayFabLobby>(refusal);
            Check($"{error:X8}: typed lobby results preserve the refusal",
                typedRefusal.Status == PlatformStatus.SessionNotJoinable
                && typedRefusal.Message == refusal.Message
                && typedRefusal.Result.Diagnostics == refusal.Diagnostics);
        }
        Check("unknown errors remain generic failures",
            Map(unchecked((int)0x81234567)).Status == PlatformStatus.Failed);
        Check("unknown errors keep native diagnostics",
            Map(unchecked((int)0x81234567)).Diagnostics?.Contains("81234567") == true);
        Check("native-library absence is explicit",
            PlayFabFailure.FromException(new DllNotFoundException("PlayFabServices"), "missing").Status == PlatformStatus.Unavailable);
        Check("cancellation stays cancellation",
            PlayFabFailure.FromException(new OperationCanceledException(), "canceled").WasCanceled);

        Check("join codes normalize case", PlayFabLobbyClient.IsJoinCode("abcde"));
        Check("join-code filters retain the deployed search key",
            PlayFabLobbyClient.BuildJoinCodeFilter("abcde") == "string_key1 eq 'ABCDE'");
        Check("filter injection and ambiguous codes are rejected",
            !PlayFabLobbyClient.IsJoinCode("AB'CD") && !PlayFabLobbyClient.IsJoinCode("AB0CD"));
        Check("null and wrong-length codes are rejected",
            !PlayFabLobbyClient.IsJoinCode(null) && !PlayFabLobbyClient.IsJoinCode("ABCD"));
        var codes = Enumerable.Range(0, 1000).Select(_ => PlayFabLobbyClient.NewJoinCode()).ToArray();
        Check("generated codes use the existing five-character alphabet", codes.All(PlayFabLobbyClient.IsJoinCode));
        Check("join-code generation has a useful spread", codes.Distinct().Count() > 990);
        using (var lobbies = new PlayFabLobbyClient(config, () => null))
        {
            var invalid = await lobbies.FindLobbyByJoinCodeAsync("AB'CD");
            Check("invalid searches fail before any native call", invalid.Failed);
            var find = await lobbies.FindLobbyByJoinCodeAsync("ABCDE");
            Check("signed-out searches return NotSignedIn", find.Status == PlatformStatus.NotSignedIn);
            var join = await lobbies.JoinLobbyAsync("connection");
            Check("signed-out joins return NotSignedIn", join.Status == PlatformStatus.NotSignedIn);
            Check("empty connection strings are rejected", (await lobbies.JoinLobbyAsync("")).Failed);
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            Check("canceled searches never start a native operation",
                (await lobbies.FindLobbyByJoinCodeAsync("ABCDE", cancel.Token)).WasCanceled);
            Check("leaving without a lobby is an idempotent no-op", (await lobbies.LeaveLobbyAsync("")).Succeeded);
        }
        var row = PlayFabLobbyClient.Snapshot(new LobbySearchResult
        {
            LobbyId = "lobby",
            ConnectionString = "connection",
            OwnerEntity = new EntityKey { Id = "host", Type = "title_player_account" },
            MaxMemberCount = 8,
            CurrentMemberCount = 3,
            SearchProperties = new Dictionary<string, string>
            {
                [PlayFabLobbyClient.JoinCodeSearchKey] = "ABCDE",
                [NRProtocol.LobbyKey] = NRProtocol.VersionString(),
            },
        });
        Check("typed search snapshots preserve metadata",
            row.LobbyId == "lobby" && row.ConnectionString == "connection" && row.OwnerEntityId == "host"
            && row.MaxPlayers == 8 && row.CurrentPlayers == 3 && row.JoinCode == "ABCDE");
        Check("typed search snapshots preserve protocol gating", row.ProtocolVersion == NRProtocol.VersionString());
        Check("missing protocol remains incompatible",
            PlayFabLobbyClient.Snapshot(new LobbySearchResult()).ProtocolVersion.Length == 0);
        var now = DateTimeOffset.UtcNow;
        Check("lobby tokens refresh before expiration",
            PlayFabLobbyClient.RefreshAt(new EntityToken { Expiration = now.AddHours(1) }, now) == now.AddMinutes(55));
        Check("cached near-expiry tokens cannot cause a refresh every frame",
            PlayFabLobbyClient.RefreshAt(new EntityToken { Expiration = now.AddMinutes(1) }, now) == now.AddSeconds(30));

        Console.WriteLine("    SKIP live authentication and connected lobbies: require a configured native runtime and test accounts.");
        Console.WriteLine();
        return failures == 0 ? 0 : 1;
    }

    private static PlatformResult Map(int hr)
        => PlayFabFailure.FromException(new PlayFabException(hr), "The request failed.");

    private static async Task AccountSwitchFailureCheck(
        PlayFabConfiguration config, Action<string, bool> check)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var online = new GameCoreOnlineSession(new GameCoreRuntime(), config);
        var services = (PlayFabOnlineServices)typeof(GameCoreOnlineSession)
            .GetField("_services", Flags)!.GetValue(online)!;
        var sessionField = typeof(PlayFabOnlineServices).GetField("_session", Flags)!;
        var generationField = typeof(PlayFabOnlineServices).GetField("_generation", Flags)!;
        sessionField.SetValue(services, new PlayFabSession
        {
            EntityId = "account-a",
            EntityType = "title_player_account",
            PlayFabId = "player-a",
        });
        var generation = (long)generationField.GetValue(services)!;
        var signOuts = 0;
        services.SessionChanged += session => { if (session is null) { signOuts++; } };
        check("account-switch fixture starts with the previous identity",
            online.IsSignedIn && online.EntityId == "account-a");

        // The uninitialized runtime rejects the replacement before accessing the Xbox user.
        var result = await online.SignInAsync(null!, allowAccountCreation: false);
        check("replacement login reports runtime failure", result.Status == PlatformStatus.Unavailable);
        check("failed replacement login clears the previous identity",
            !online.IsSignedIn && online.EntityId.Length == 0 && services.Session is null);
        check("replacement login notifies identity consumers of sign-out", signOuts == 1);
        check("replacement login invalidates older in-flight sign-ins",
            (long)generationField.GetValue(services)! > generation);
    }

    private static void TokenRefreshFailureChecks(
        PlayFabConfiguration config, Action<string, bool> check)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var lobbies = new PlayFabLobbyClient(config, () => null);
        var failed = typeof(PlayFabLobbyClient).GetMethod("TokenRefreshFailed", Flags)!;
        var refreshAt = typeof(PlayFabLobbyClient).GetField("_refreshTokenAt", Flags)!;
        var disconnects = new List<PlatformResult>();
        lobbies.ConnectionLost += disconnects.Add;
        Exception[] retryable =
        [
            new OperationCanceledException(),
            new PlayFabException(unchecked((int)0x80072EE2)),
            new PlayFabException(unchecked((int)0x80072EFD)),
            new PlayFabException(PlayFabErrors.ApiClientRequestRateLimitExceeded),
            new PlayFabException(PlayFabErrors.LobbyRateLimitExceeded),
            new PlayFabException(PlayFabErrors.ServiceUnavailable),
            new PlayFabException(PlayFabErrors.DownstreamServiceUnavailable),
        ];
        foreach (var error in retryable)
        {
            var before = DateTimeOffset.UtcNow;
            failed.Invoke(lobbies, [error]);
            var after = DateTimeOffset.UtcNow;
            var next = (DateTimeOffset)refreshAt.GetValue(lobbies)!;
            var label = $"{error.GetType().Name} 0x{error.HResult:X8}";
            check($"retryable refresh {label} does not disconnect", disconnects.Count == 0);
            check($"retryable refresh {label} schedules a ten-second retry",
                next >= before.AddSeconds(10) && next <= after.AddSeconds(10));
        }

        disconnects.Clear();
        failed.Invoke(lobbies, [new PlayFabException(PlayFabErrors.Noentitytoken)]);
        check("terminal token loss notifies the connection consumer",
            disconnects.Count == 1 && disconnects[0].Status == PlatformStatus.NotSignedIn);
        check("terminal token loss stops refresh retries",
            (DateTimeOffset)refreshAt.GetValue(lobbies)! == DateTimeOffset.MaxValue);
    }

    private static async Task ProviderShutdownChecks(
        PlayFabConfiguration config, Action<string, bool> check)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        foreach (var holdSignIn in new[] { true, false })
        {
            var provider = new GameCorePlatformProvider(playFab: config);
            var online = (GameCoreOnlineSession)typeof(GameCorePlatformProvider)
                .GetField("_online", Flags)!.GetValue(provider)!;
            var services = (PlayFabOnlineServices)typeof(GameCoreOnlineSession)
                .GetField("_services", Flags)!.GetValue(online)!;
            var gate = (SemaphoreSlim)typeof(PlayFabOnlineServices)
                .GetField("_signInGate", Flags)!.GetValue(services)!;
            var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (holdSignIn)
            {
                await gate.WaitAsync();
            }
            else
            {
                typeof(PlayFabOnlineServices).GetField("_idle", Flags)!.SetValue(services, idle);
            }
            try
            {
                var shutdown = provider.DisposeAsync().AsTask();
                check($"provider shutdown waits for outstanding {(holdSignIn ? "sign-ins" : "entity leases")}",
                    !shutdown.IsCompleted);
                if (holdSignIn)
                {
                    gate.Release();
                }
                else
                {
                    idle.SetResult();
                }
                await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
                check("provider shutdown completes after native work drains", shutdown.IsCompletedSuccessfully);
            }
            finally
            {
                if (holdSignIn && gate.CurrentCount == 0)
                {
                    gate.Release();
                }
                idle.TrySetResult();
                await provider.DisposeAsync();
            }
        }
    }

    private static void LobbyDisconnectChecks(
        PlayFabConfiguration config, Action<string, bool> check)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        foreach (var hr in new[] { PlayFabErrors.Noentitytoken, PlayFabErrors.CoreLocalUserNotLoggedIn, 0 })
        {
            var runtime = new GameCoreRuntime();
            using var online = new GameCoreOnlineSession(runtime, config);
            var identity = new GameCoreIdentityService(runtime, online);
            var services = (PlayFabOnlineServices)typeof(GameCoreOnlineSession)
                .GetField("_services", Flags)!.GetValue(online)!;
            typeof(PlayFabOnlineServices).GetField("_session", Flags)!.SetValue(services, new PlayFabSession
            {
                EntityId = "expired-account",
                EntityType = "title_player_account",
                PlayFabId = "expired-player",
            });
            using var party = new GameCorePartyService(runtime, identity, online, config, null!);
            typeof(GameCorePartyService).GetProperty(nameof(party.HasNetwork))!.SetValue(party, true);
            typeof(GameCorePartyService).GetProperty(nameof(party.IsHost))!.SetValue(party, true);
            typeof(GameCorePartyService).GetProperty(nameof(party.ConnectionString))!.SetValue(party, "old-invite");
            typeof(GameCorePartyService).GetProperty(nameof(party.JoinCode))!.SetValue(party, "ABCDE");
            var assignment = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            typeof(GameCorePartyService).GetField("_peerAssignment", Flags)!.SetValue(party, assignment);
            var operation = (Task)typeof(GameCorePartyService).GetMethod("AwaitOperationAsync", Flags)!
                .MakeGenericMethod(typeof(PartyOperationCompleted))
                .Invoke(party, [default(PartyOperationId), CancellationToken.None])!;
            var invalidIdentity = hr != 0;
            var events = 0;
            var clearedBeforeNotification = false;
            party.NetworkDestroyed += failure =>
            {
                events++;
                clearedBeforeNotification = !party.HasNetwork && !party.IsHost
                    && party.ConnectionString.Length == 0 && party.JoinCode.Length == 0
                    && online.IsSignedIn != invalidIdentity;
            };
            if (invalidIdentity)
            {
                typeof(PlayFabLobbyClient).GetMethod("TokenRefreshFailed", Flags)!
                    .Invoke(online.Lobbies, [new PlayFabException(hr)]);
            }
            else
            {
                var connectionLost = (Action<PlatformResult>)typeof(PlayFabLobbyClient)
                    .GetField("ConnectionLost", Flags)!.GetValue(online.Lobbies)!;
                connectionLost(PlatformResult.Fail(PlatformStatus.NetworkFailure, "The match directory connection was lost."));
            }
            var label = invalidIdentity ? $"terminal identity failure 0x{hr:X8}" : "native Lobby disconnect";
            check($"{label} clears Party before reporting loss",
                events == 1 && clearedBeforeNotification);
            check($"{label} cancels pending peer assignment", assignment.Task.IsCanceled);
            check($"{label} cancels pending Party operations without wrapping cancellation", operation.IsCanceled);
            check($"{label} retains a recoverable identity state",
                online.IsSignedIn != invalidIdentity && online.CanRetrySignIn);
        }
    }

    private static async Task PartyTeardownBudgetChecks(Action<string, bool> check)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var party = new GameCorePartyService(new GameCoreIdentityService(new GameCoreRuntime()));
        var budget = (TimeSpan)typeof(GameCorePartyService)
            .GetField("LeaveTeardownBudget", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        check("Party teardown shares a six-second budget", budget == TimeSpan.FromSeconds(6));

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var wait = (Task)typeof(GameCorePartyService).GetMethod("TryAwait", Flags)!
            .Invoke(party, [default(PartyOperationId), cancellation.Token])!;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await wait.WaitAsync(TimeSpan.FromSeconds(2));
            check("Party teardown cancellation is not swallowed", false);
        }
        catch (OperationCanceledException)
        {
            check("Party teardown stops waiting without a frame pump",
                wait.IsCanceled && elapsed.Elapsed < TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
            check("Party teardown stops waiting without a frame pump", false);
        }
        finally
        {
            await party.LeaveAsync();
        }

        var pending = (System.Collections.IDictionary)typeof(GameCorePartyService)
            .GetField("_pendingOperations", Flags)!.GetValue(party)!;
        check("Party reset clears abandoned teardown operations", pending.Count == 0);
    }

    private static async Task PartyPumpTeardownChecks(Action<string, bool> check, bool nativeManager = false)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var party = new GameCorePartyService(new GameCoreIdentityService(new GameCoreRuntime()));
        var type = typeof(GameCorePartyService);
        var manager = nativeManager ? PartyManager.Initialize("A1B2C") : null;
        if (manager is not null)
        {
            type.GetField("_party", Flags)!.SetValue(party, manager);
        }
        type.GetProperty(nameof(party.HasNetwork))!.SetValue(party, true);
        var revision = (int)type.GetField("_sessionRevision", Flags)!.GetValue(party)!;
        using var cancellation = new CancellationTokenSource();
        var wait = (Task)type.GetMethod("TryTeardownAsync", Flags)!.Invoke(party,
            [new Func<PartyOperationId?>(() => default(PartyOperationId)), revision, cancellation.Token])!;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cleanupRequested = new ManualResetEventSlim();
        var assignment = new TaskCompletionSource<int>();
        type.GetField("_peerAssignment", Flags)!.SetValue(party, assignment);
        type.GetField("_peerAssignmentCancellation", Flags)!.SetValue(party, new CancellationToken(true));
        var callback = assignment.Task.ContinueWith(_ =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The blocked Party pump was not released.");
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var pump = Task.Factory.StartNew(party.Pump, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task cleanup = Task.CompletedTask;
        try
        {
            check("Party race fixture enters the real pump", entered.Wait(TimeSpan.FromSeconds(2)));
            if (!entered.IsSet)
            {
                return;
            }
            cleanup = Task.Run(async () =>
            {
                try
                {
                    await wait;
                }
                catch (OperationCanceledException)
                {
                    cleanupRequested.Set();
                    type.GetMethod("CleanupSession", Flags)!.Invoke(party, [revision]);
                }
            });
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
            check("Party teardown deadline fires while the pump is active",
                cleanupRequested.Wait(TimeSpan.FromSeconds(2)));
            await Task.WhenAny(cleanup, Task.Delay(100));
            check("deadline cleanup cannot reset a session during its pump",
                cleanupRequested.IsSet && !cleanup.IsCompleted && party.HasNetwork
                && ReferenceEquals(type.GetField("_party", Flags)!.GetValue(party), manager));
        }
        finally
        {
            release.Set();
            await pump.WaitAsync(TimeSpan.FromSeconds(3));
            await callback.WaitAsync(TimeSpan.FromSeconds(3));
            await cleanup.WaitAsync(TimeSpan.FromSeconds(3));
        }

        var pending = (System.Collections.IDictionary)type.GetField("_pendingOperations", Flags)!.GetValue(party)!;
        check("deadline cleanup completes after the pump exits",
            wait.IsCanceled && !party.HasNetwork && pending.Count == 0);
    }

    private static void PartyRegionResetChecks(Action<string, bool> check)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var party = new GameCorePartyService(new GameCoreIdentityService(new GameCoreRuntime()));
        var type = typeof(GameCorePartyService);
        var regions = (System.Collections.IList)type.GetField("_pendingRegions", Flags)!.GetValue(party)!;
        var regionType = type.GetNestedType("PendingRegions", BindingFlags.NonPublic)!;
        TaskCompletionSource<IReadOnlyList<PartyRegion>> RegisterRegion()
        {
            var region = Activator.CreateInstance(regionType, [new CancellationToken(true)])!;
            regions.Add(region);
            return (TaskCompletionSource<IReadOnlyList<PartyRegion>>)regionType
                .GetProperty("Completion")!.GetValue(region)!;
        }
        var first = RegisterRegion();
        var second = RegisterRegion();
        var reset = first.Task.ContinueWith(_ =>
            type.GetMethod("ResetSessionState", Flags)!.Invoke(party, null),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var canceledInsidePump = false;
        var observe = second.Task.ContinueWith(_ =>
            canceledInsidePump = (bool)type.GetField("_pumping", Flags)!.GetValue(party)!,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        party.Pump();
        reset.GetAwaiter().GetResult();
        observe.GetAwaiter().GetResult();
        check("region cancellation skips waits detached by a reentrant reset",
            first.Task.IsCanceled && second.Task.IsCanceled && !canceledInsidePump && regions.Count == 0);
    }

    private static void PartyReentrantResetChecks(Action<string, bool> check, bool nativeManager = false)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var party = new GameCorePartyService(new GameCoreIdentityService(new GameCoreRuntime()));
        var type = typeof(GameCorePartyService);
        var manager = nativeManager ? PartyManager.Initialize("A1B2C") : null;
        if (manager is not null)
        {
            type.GetField("_party", Flags)!.SetValue(party, manager);
        }
        var awaitOperation = type.GetMethod("AwaitOperationAsync", Flags)!
            .MakeGenericMethod(typeof(PartyOperationCompleted));
        Task RegisterOperation() => (Task)awaitOperation.Invoke(party,
            [default(PartyOperationId), CancellationToken.None])!;
        var pending = (System.Collections.IDictionary)type.GetField("_pendingOperations", Flags)!.GetValue(party)!;
        var regions = (System.Collections.IList)type.GetField("_pendingRegions", Flags)!.GetValue(party)!;
        var regionType = type.GetNestedType("PendingRegions", BindingFlags.NonPublic)!;
        var region = Activator.CreateInstance(regionType, [CancellationToken.None])!;
        var regionCompletion = (TaskCompletionSource<IReadOnlyList<PartyRegion>>)regionType
            .GetProperty("Completion")!.GetValue(region)!;
        regions.Add(region);
        var oldRevision = (int)type.GetField("_sessionRevision", Flags)!.GetValue(party)!;
        var oldOperation = (Task)type.GetMethod("TryTeardownAsync", Flags)!.Invoke(party,
            [new Func<PartyOperationId?>(() => default(PartyOperationId)), oldRevision, CancellationToken.None])!;
        var managerRetiredBeforeCleanup = false;
        var oldCleanup = oldOperation.ContinueWith(_ =>
        {
            managerRetiredBeforeCleanup = type.GetField("_party", Flags)!.GetValue(party) is null
                && !(bool)type.GetField("_pumping", Flags)!.GetValue(party)!;
            type.GetMethod("CleanupSession", Flags)!.Invoke(party, [oldRevision]);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        Task newOperation = Task.CompletedTask;
        var assignment = new TaskCompletionSource<int>();
        type.GetField("_peerAssignment", Flags)!.SetValue(party, assignment);
        type.GetField("_peerAssignmentCancellation", Flags)!.SetValue(party, new CancellationToken(true));
        var reset = assignment.Task.ContinueWith(_ =>
        {
            type.GetMethod("ResetSessionState", Flags)!.Invoke(party, null);
            check("reentrant reset atomically detaches operation and region waits",
                pending.Count == 0 && regions.Count == 0);
            check("reentrant reset defers detached cancellations until the pump exits",
                !oldOperation.IsCompleted && !regionCompletion.Task.IsCompleted);
            check("every reentrant reset schedules old manager retirement",
                (bool)type.GetField("_disposeAfterPump", Flags)!.GetValue(party)!
                && ReferenceEquals(type.GetField("_party", Flags)!.GetValue(party), manager));
            newOperation = RegisterOperation();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        party.Pump();
        reset.GetAwaiter().GetResult();
        oldCleanup.GetAwaiter().GetResult();
        check("reset retires the manager before canceled leave cleanup runs",
            managerRetiredBeforeCleanup);
        check("detached Party waits cancel even when no native manager exists",
            oldOperation.IsCanceled && regionCompletion.Task.IsCanceled);
        check("old reset cancellation preserves a new operation registration",
            !newOperation.IsCompleted && pending.Count == 1);
        type.GetMethod("CleanupSession", Flags)!.Invoke(party, [oldRevision]);
        check("stale leave cleanup cannot reset a newer session",
            !newOperation.IsCompleted && pending.Count == 1);
        var started = false;
        var staleStep = (Task)type.GetMethod("TryTeardownAsync", Flags)!.Invoke(party,
            [new Func<PartyOperationId?>(() => { started = true; return default(PartyOperationId); }),
                oldRevision, CancellationToken.None])!;
        check("stale leave cannot start native teardown in a newer session",
            staleStep.IsCompletedSuccessfully && !started);

        var disposeAssignment = new TaskCompletionSource<int>();
        type.GetField("_peerAssignment", Flags)!.SetValue(party, disposeAssignment);
        type.GetField("_peerAssignmentCancellation", Flags)!.SetValue(party, new CancellationToken(true));
        var dispose = disposeAssignment.Task.ContinueWith(_ =>
        {
            party.Dispose();
            check("reentrant Party disposal waits for native enumeration to exit",
                (bool)type.GetField("_disposeAfterPump", Flags)!.GetValue(party)! && !newOperation.IsCompleted);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        party.Pump();
        dispose.GetAwaiter().GetResult();
        check("pump exit finishes deferred disposal and cancellation",
            !(bool)type.GetField("_disposeAfterPump", Flags)!.GetValue(party)!
            && newOperation.IsCanceled && pending.Count == 0);
    }

    private static async Task PartyAttemptCancellationChecks(Action<string, bool> check, bool nativeManager = false)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var party = new GameCorePartyService(new GameCoreIdentityService(new GameCoreRuntime()));
        var type = typeof(GameCorePartyService);
        var revision = (int)type.GetField("_sessionRevision", Flags)!.GetValue(party)!;
        var oldWait = (Task)type.GetMethod("TryAwait", Flags)!.Invoke(party,
            [default(PartyOperationId), CancellationToken.None])!;
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<PlatformResult> CancelOldAttemptAsync()
        {
            try
            {
                await oldWait;
            }
            catch (OperationCanceledException)
            {
                await resume.Task;
                return await (Task<PlatformResult>)type.GetMethod("CancelAttemptAsync", Flags)!
                    .Invoke(party, [revision])!;
            }
            throw new InvalidOperationException("The old attempt was not canceled.");
        }
        var canceledAttempt = CancelOldAttemptAsync();
        var assignment = new TaskCompletionSource<int>();
        type.GetField("_peerAssignment", Flags)!.SetValue(party, assignment);
        type.GetField("_peerAssignmentCancellation", Flags)!.SetValue(party, new CancellationToken(true));
        var reset = assignment.Task.ContinueWith(_ =>
            type.GetMethod("ResetSessionState", Flags)!.Invoke(party, null),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        party.Pump();
        reset.GetAwaiter().GetResult();
        var replacement = nativeManager ? PartyManager.Initialize("A1B2C") : null;
        type.GetField("_party", Flags)!.SetValue(party, replacement);
        type.GetProperty(nameof(party.HasNetwork))!.SetValue(party, true);
        type.GetProperty(nameof(party.ConnectionString))!.SetValue(party, "replacement-session");
        var replacementRevision = (int)type.GetField("_sessionRevision", Flags)!.GetValue(party)!;
        var newWait = (Task)type.GetMethod("AwaitOperationAsync", Flags)!
            .MakeGenericMethod(typeof(PartyOperationCompleted))
            .Invoke(party, [default(PartyOperationId), CancellationToken.None])!;
        resume.SetResult();
        var canceled = await canceledAttempt.WaitAsync(TimeSpan.FromSeconds(3));
        check("host/join cancellation handler retains the canceled result", canceled.WasCanceled);
        check("old host/join cancellation cannot leave a replacement session",
            party.HasNetwork && party.ConnectionString == "replacement-session"
            && !newWait.IsCompleted
            && ReferenceEquals(type.GetField("_party", Flags)!.GetValue(party), replacement)
            && replacementRevision == (int)type.GetField("_sessionRevision", Flags)!.GetValue(party)!);
    }

    private static async Task PartyEarlyAuthenticationChecks(Action<string, bool> check)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var party = new GameCorePartyService(new GameCoreIdentityService(new GameCoreRuntime()));
        var type = typeof(GameCorePartyService);
        PartyOperationId Operation(long id) => (PartyOperationId)Activator.CreateInstance(
            typeof(PartyOperationId), Flags, null, [id], null)!;
        var connectId = Operation(1);
        var authenticateId = Operation(2);
        var (connection, authentication) = ((Task<PartyConnectToNetworkCompleted>,
            Task<PartyAuthenticateLocalUserCompleted>))type.GetMethod("AwaitConnectionOperations", Flags)!
            .Invoke(party, [connectId, authenticateId, CancellationToken.None])!;
        var pending = (System.Collections.IDictionary)type.GetField("_pendingOperations", Flags)!.GetValue(party)!;
        check("connection and authentication waits register before pumping", pending.Count == 2);
        var completedAuthentication = new PartyAuthenticateLocalUserCompleted(authenticateId,
            PartyStateChangeResult.CanceledByTitle, 0, null, null, null);
        var completedConnection = new PartyConnectToNetworkCompleted(connectId,
            PartyStateChangeResult.CanceledByTitle, 0, default!, null);
        var complete = type.GetMethod("CompleteOperation", Flags)!;
        complete.Invoke(party, [completedAuthentication]);
        complete.Invoke(party, [completedConnection]);
        check("early authentication completion is retained before the connection await",
            connection.IsCompletedSuccessfully && authentication.IsCompletedSuccessfully && pending.Count == 0);
        check("connection and authentication retain their own completion",
            ReferenceEquals(await connection.WaitAsync(TimeSpan.FromSeconds(2)), completedConnection)
            && ReferenceEquals(await authentication.WaitAsync(TimeSpan.FromSeconds(2)), completedAuthentication));
    }

    private static async Task PartyLateLobbyChecks(Action<string, bool> check)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var party = new GameCorePartyService(new GameCoreIdentityService(new GameCoreRuntime()));
        var type = typeof(GameCorePartyService);
        var revision = (int)type.GetField("_sessionRevision", Flags)!.GetValue(party)!;
        type.GetMethod("ResetSessionState", Flags)!.Invoke(party, null);
        type.GetProperty(nameof(party.HasNetwork))!.SetValue(party, true);
        type.GetProperty(nameof(party.JoinCode))!.SetValue(party, "NEWER");
        type.GetField("_lobbyId", Flags)!.SetValue(party, "replacement-lobby");
        var newWait = (Task)type.GetMethod("AwaitOperationAsync", Flags)!
            .MakeGenericMethod(typeof(PartyOperationCompleted))
            .Invoke(party, [default(PartyOperationId), CancellationToken.None])!;
        var lobby = new PlayFabLobby
        {
            LobbyId = "late-lobby",
            ConnectionString = "retired-network",
            JoinCode = "OLDER",
        };
        var closedIds = new List<string>();
        var adopt = type.GetMethod("AdoptHostedLobbyAsync", Flags)!;
        var result = await (Task<PlatformResult<string>>)adopt.Invoke(party,
            [lobby, revision, new Func<string, Task<PlatformResult>>(id =>
            {
                closedIds.Add(id);
                return Task.FromResult(PlatformResult.Ok());
            })])!;
        check("late hosted lobby adoption returns cancellation", result.WasCanceled);
        check("late hosted lobby cleanup closes only the returned lobby",
            closedIds.SequenceEqual(["late-lobby"]));
        check("late hosted lobby cleanup preserves replacement state",
            party.HasNetwork && party.JoinCode == "NEWER" && !newWait.IsCompleted
            && (string)type.GetField("_lobbyId", Flags)!.GetValue(party)! == "replacement-lobby");
        var failure = await (Task<PlatformResult<string>>)adopt.Invoke(party,
            [lobby, revision, new Func<string, Task<PlatformResult>>(_ =>
                Task.FromResult(PlatformResult.Fail(PlatformStatus.NetworkFailure, "close failed", "close-diagnostics")))])!;
        check("late hosted lobby cleanup surfaces close failures",
            failure.Status == PlatformStatus.NetworkFailure && failure.Result.Diagnostics == "close-diagnostics");
        check("failed late lobby cleanup still preserves the replacement",
            party.HasNetwork && party.JoinCode == "NEWER" && !newWait.IsCompleted);
    }

    private static void NativeLobbyResetChecks(Action<string, bool> check)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var lobbies = new PlayFabLobbyClient(new PlayFabConfiguration { TitleId = "A1B2C" }, () => null);
        var managerField = typeof(PlayFabLobbyClient).GetField("_multiplayer", Flags)!;
        managerField.SetValue(lobbies, PlayFabMultiplayer.Initialize("A1B2C"));
        var pending = (System.Collections.IDictionary)typeof(PlayFabLobbyClient)
            .GetField("_pending", Flags)!.GetValue(lobbies)!;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(PlayFabLobbyClient).GetMethod("Register", Flags)!.MakeGenericMethod(typeof(bool))
            .Invoke(lobbies, [OperationId.None, CancellationToken.None, completion,
                new Func<LobbyOperationCompleted, bool>(_ => true)]);
        lobbies.Reset();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        lobbies.Pump();
        elapsed.Stop();
        check("account-change pump does not run the one-second shutdown drain",
            elapsed.Elapsed < TimeSpan.FromMilliseconds(500));
        check("account-change reset cancels outstanding callers", completion.Task.IsCanceled);
        check("account-change reset preserves native completions for subsequent ticks", pending.Count == 1);
        lobbies.Pump();
        check("retirement continues across pump ticks", pending.Count == 1);
        var deadline = typeof(PlayFabLobbyClient).GetField("_resetDeadline", Flags)!;
        deadline.SetValue(lobbies, Environment.TickCount64 - 1);
        lobbies.Pump();
        check("retirement timeout releases the old manager and operation registry",
            managerField.GetValue(lobbies) is null && pending.Count == 0);
    }

    internal static int NativeSmoke()
    {
        Console.WriteLine("PlayFab native startup smoke (no authentication or service writes)");
        if (!GameCorePlatformProvider.IsRuntimePresent)
        {
            Console.WriteLine($"    SKIP {GameCorePlatformProvider.UnavailableReason}");
            return 0;
        }
        try
        {
            if (!GameRuntimeHost.EnsureInitialized())
            {
                Console.WriteLine("    FAIL Gaming Runtime initialization");
                return 1;
            }
            PlayFabRuntime.Initialize();
            using (var config = new PlayFabServiceConfig("https://A1B2C.playfabapi.com", "A1B2C"))
            using (var multiplayer = PlayFabMultiplayer.Initialize("A1B2C"))
            {
                foreach (var _ in multiplayer.ProcessLobbyStateChanges())
                {
                }
                Console.WriteLine("    PASS Gaming Runtime, PlayFab Core/Services, service config and Lobby manager initialization/pump");
            }
            Console.WriteLine("    PASS Lobby manager disposal");
            var failures = 0;
            void Check(string label, bool passed)
            {
                Console.WriteLine($"    {(passed ? "PASS" : "FAIL")} {label}");
                if (!passed)
                {
                    failures++;
                }
            }
            NativeLobbyResetChecks(Check);
            PartyPumpTeardownChecks(Check, nativeManager: true).GetAwaiter().GetResult();
            PartyReentrantResetChecks(Check, nativeManager: true);
            PartyAttemptCancellationChecks(Check, nativeManager: true).GetAwaiter().GetResult();
            return failures == 0 ? 0 : 1;
        }
        catch (Exception ex) when (PlayFabFailure.IsExpected(ex))
        {
            Console.WriteLine($"    FAIL {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            GameRuntimeHost.Shutdown();
        }
    }
}
