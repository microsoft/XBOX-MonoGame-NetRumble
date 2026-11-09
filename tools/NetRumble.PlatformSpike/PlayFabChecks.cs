using System.Reflection;
using GDK.Net;
using GDK.Net.PlayFab;
using NetRumble.Platform;
using NetRumble.Platform.Networking;
using NetRumble.Platform.Offline;
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
            var login = await placeholder.SignInWithCustomIdAsync("developer");
            Check("placeholder refuses login before any native call", login.Status == PlatformStatus.Unavailable);
            Check("a placeholder cannot publish an online identity", !placeholder.IsSignedIn && placeholder.EntityId.Length == 0);
            Check("placeholder refuses cloud saves without native calls",
                (await placeholder.GameSaves.SaveCloudAsync("settings", new byte[1])).Status == PlatformStatus.Unavailable);
            Check("placeholder refuses cloud reads without native calls",
                (await placeholder.GameSaves.LoadCloudAsync("settings")).Status == PlatformStatus.Unavailable);
        }
        using (var services = new PlayFabOnlineServices(config))
        {
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            var login = await services.SignInWithCustomIdAsync("developer", canceled.Token);
#if DEBUG
            Check("canceled login never initializes native PlayFab", login.WasCanceled);
#else
            Check("Release forbids custom-ID login", login.Status == PlatformStatus.Unavailable);
#endif
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

        var acquired = 0;
        var saves = new PlayFabGameSaveService(config, () => { acquired++; return null; });
        var oversized = await saves.SaveCloudAsync("settings", new byte[PlayFabGameSaveService.MaxCloudBytes + 1]);
        Check("oversized cloud writes are rejected before acquiring a user", oversized.Failed && acquired == 0);
        var emptyKey = await saves.LoadCloudAsync("");
        Check("empty save keys fail before acquiring a user", emptyKey.Failed && acquired == 0);
        var edge = await saves.SaveCloudAsync("settings", new byte[PlayFabGameSaveService.MaxCloudBytes]);
        Check("exactly 750 bytes pass size validation", edge.Status == PlatformStatus.NotSignedIn && acquired == 1);
        Check("750 bytes encode to the 1000-character service limit",
            Convert.ToBase64String(new byte[PlayFabGameSaveService.MaxCloudBytes]).Length == 1000);
        var signedOutLoad = await saves.LoadCloudAsync("settings");
        Check("signed-out reads require a native user", signedOutLoad.Status == PlatformStatus.NotSignedIn);
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            Check("canceled cloud writes are canceled", (await saves.SaveCloudAsync("settings", new byte[1], cancel.Token)).WasCanceled);
            Check("canceled cloud reads are canceled", (await saves.LoadCloudAsync("settings", cancel.Token)).WasCanceled);
        }

        var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        var data = new Dictionary<string, UserDataRecord>
        {
            ["settings"] = new() { Value = Convert.ToBase64String(bytes) },
            ["empty"] = new() { Value = "" },
            ["corrupt"] = new() { Value = "not valid Base64!" },
        };
        var read = PlayFabGameSaveService.ReadUserDataValue(data, "settings");
        Check("existing Base64 cloud data round-trips every byte", read.Succeeded && read.Value!.SequenceEqual(bytes));
        Check("a missing save is a successful empty payload",
            PlayFabGameSaveService.ReadUserDataValue(data, "missing").Value is { Length: 0 });
        Check("an empty record is a successful empty payload",
            PlayFabGameSaveService.ReadUserDataValue(data, "empty").Value is { Length: 0 });
        Check("an absent data dictionary is a successful empty payload",
            PlayFabGameSaveService.ReadUserDataValue(null, "missing").Value is { Length: 0 });
        var corrupt = PlayFabGameSaveService.ReadUserDataValue(data, "corrupt");
        Check("corrupt saves fail explicitly", corrupt.Failed && corrupt.Result.Diagnostics?.Contains("Base64") == true);

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

        await using (var provider = new PlayFabPlatformProvider(new OfflinePlatformProvider(),
            placeholderConfig, () => throw new InvalidOperationException("Must not start"), _ => { }))
        {
            Check("placeholder composition does not start the native runtime", (await provider.Runtime.InitializeAsync()).Succeeded);
            Check("placeholder composition does not advertise cloud save",
                !provider.Capabilities.HasFlag(PlatformCapabilities.CloudSave));
        }
        await using (var provider = new PlayFabPlatformProvider(new OfflinePlatformProvider(), config,
            () => throw new PlatformNotSupportedException("No Gaming Runtime"), _ => { }))
        {
            var started = await provider.Runtime.InitializeAsync();
            Check("developer PlayFab reports a missing native runtime", started.Status == PlatformStatus.Unavailable);
            Check("missing native runtime does not advertise cloud save",
                !provider.Capabilities.HasFlag(PlatformCapabilities.CloudSave));
            var user = await provider.Identity.SignInAsync();
            Check("local identity still works without PlayFab credentials", user.Succeeded && user.Value?.EntityId == "");
        }

        Console.WriteLine("    SKIP live authentication, user data and connected lobbies: require a configured native runtime and test accounts.");
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
            NativeLobbyResetChecks((label, passed) =>
            {
                Console.WriteLine($"    {(passed ? "PASS" : "FAIL")} {label}");
                if (!passed)
                {
                    failures++;
                }
            });
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
