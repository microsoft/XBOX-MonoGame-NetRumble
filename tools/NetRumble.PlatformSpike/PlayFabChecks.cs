using GDK.Net;
using GDK.Net.PlayFab;
using NetRumble.Platform;
using NetRumble.Platform.Networking;
using NetRumble.Platform.Offline;
using NetRumble.Platform.PlayFab;
using GDK.Net.PlayFab.Multiplayer;
using NetRumble.Platform.GameCore;
using NetRumble.Platform.GameCore.Interop;

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
        Check("closed lobbies report a player-facing refusal",
            Map(PlayFabErrors.LobbyNotJoinable).Failed && Map(PlayFabErrors.LobbyNotJoinable).Message!.Contains("match"));
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
            return 0;
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
