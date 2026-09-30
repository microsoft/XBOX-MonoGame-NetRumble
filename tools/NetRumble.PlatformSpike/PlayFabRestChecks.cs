using System.Net;
using System.Text;
using System.Text.Json;
using NetRumble.Platform;
using NetRumble.Platform.Composition;
using NetRumble.Platform.PlayFab;

namespace NetRumble.PlatformSpike;

/// <summary>
/// Checks for the PlayFab REST layer: auth, cloud save and lobby.
/// </summary>
/// <remarks>
/// <para>
/// None of these touch the network. That is the point of them rather than a limitation:
/// a real PlayFab title id is deployment configuration nobody has on a clean dev box, so
/// a check that needed one would be skipped everywhere and would therefore protect
/// nothing. Instead the HTTP handler is stubbed, which lets every part that can be wrong
/// without a server be proved exactly - the URL, which token goes in which header, the
/// envelope, the error mapping, and the response parsing - against real captured
/// PlayFab payload shapes.
/// </para>
/// <para>
/// What this deliberately does <b>not</b> prove: that a live PlayFab title accepts these
/// requests. That needs a title id and, for the Xbox path, a signed-in Xbox account.
/// The honest summary is in <c>docs/design-notes.md</c>; nothing here should be read as
/// evidence that online play works end to end.
/// </para>
/// </remarks>
internal static class PlayFabRestChecks
{
    /// <summary>A title id that is not the placeholder, so requests are actually attempted.</summary>
    private const string TestTitleId = "A1B2C";

    public static async Task Run()
    {
        Console.WriteLine("[15] PlayFab REST");

        Requests();
        await ErrorMapping().ConfigureAwait(false);
        SessionState();
        LoginParsing();
        await AuthRoundTrip().ConfigureAwait(false);
        await XboxAgeGroupGate().ConfigureAwait(false);
        await CloudSave().ConfigureAwait(false);
        await Lobbies().ConfigureAwait(false);
        await Provider().ConfigureAwait(false);

        Console.WriteLine();
    }

    // --- Request shaping ---------------------------------------------------

    private static void Requests()
    {
        using var handler = new StubHandler();
        using var client = new PlayFabRestClient(Config(TestTitleId), handler);

        // Uri lowercases the host, which is harmless - DNS is case-insensitive - but
        // makes an ordinal comparison against the title id as typed fail misleadingly.
        var uri = client.BuildUri("/Client/LoginWithCustomID");

        Check(
            "title id becomes the host subdomain",
            string.Equals(uri.Host, TestTitleId + ".playfabapi.com", StringComparison.OrdinalIgnoreCase));

        Check("the API path is preserved exactly", uri.AbsolutePath == "/Client/LoginWithCustomID");

        Check("requests go over HTTPS", client.BuildUri("/Lobby/CreateLobby").Scheme == "https");
    }

    private static async Task ErrorMapping()
    {
        // A placeholder title must be refused before a socket is opened. The stub
        // counting requests is what proves "before", which a status check alone would
        // not: an Unavailable result after a failed DNS lookup looks identical.
        using (var handler = new StubHandler())
        using (var placeholder = new PlayFabRestClient(Config(PlayFabConfiguration.PlaceholderTitleId), handler))
        {
            var result = await placeholder
                .PostAsync("/Client/LoginWithCustomID", new PlayFabRestClient.EmptyRequest(), PlayFabJsonContext.Default.EmptyRequest, PlayFabAuth.None, null)
                .ConfigureAwait(false);

            Check("placeholder title id is Unavailable", result.Status == PlatformStatus.Unavailable);
            Check("placeholder title id sends no request", handler.RequestCount == 0);
        }

        using var stub = new StubHandler();
        using var client = new PlayFabRestClient(Config(TestTitleId), stub);

        // A missing credential is caught locally too. Sending a request that is certain
        // to come back 401 wastes a round trip and reports a service problem for what is
        // really a caller bug.
        var noSession = await client
            .PostAsync("/Client/GetUserData", new PlayFabRestClient.EmptyRequest(), PlayFabJsonContext.Default.EmptyRequest, PlayFabAuth.SessionTicket, null)
            .ConfigureAwait(false);

        Check("missing session is NotSignedIn", noSession.Status == PlatformStatus.NotSignedIn);
        Check("missing session sends no request", stub.RequestCount == 0);

        // Header selection. Getting this wrong is a 401 indistinguishable from an
        // expired token, so both headers are asserted by name.
        var session = TestSession();

        stub.Respond("/a", HttpStatusCode.OK, """{"code":200,"status":"OK","data":{}}""");
        await client.PostAsync("/a", new PlayFabRestClient.EmptyRequest(), PlayFabJsonContext.Default.EmptyRequest, PlayFabAuth.SessionTicket, session).ConfigureAwait(false);
        Check(
            "session ticket goes in X-Authorization",
            stub.LastRequestHeader("X-Authorization") == session.SessionTicket
                && stub.LastRequestHeader("X-EntityToken") is null);

        await client.PostAsync("/a", new PlayFabRestClient.EmptyRequest(), PlayFabJsonContext.Default.EmptyRequest, PlayFabAuth.EntityToken, session).ConfigureAwait(false);
        Check(
            "entity token goes in X-EntityToken",
            stub.LastRequestHeader("X-EntityToken") == session.EntityToken
                && stub.LastRequestHeader("X-Authorization") is null);

        await client.PostAsync("/a", new PlayFabRestClient.EmptyRequest(), PlayFabJsonContext.Default.EmptyRequest, PlayFabAuth.None, session).ConfigureAwait(false);
        Check(
            "unauthenticated calls carry no credential",
            stub.LastRequestHeader("X-Authorization") is null
                && stub.LastRequestHeader("X-EntityToken") is null);

        // Envelope reading, straight over the parser with captured body shapes.
        var ok = PlayFabRestClient.ReadEnvelope(
            "/x", HttpStatusCode.OK, """{"code":200,"status":"OK","data":{"PlayFabId":"ABC"}}""");

        Check("success unwraps the data object", ok.Succeeded);

        // The returned element must outlive the JsonDocument it was parsed from. This
        // reads it after ReadEnvelope has returned and disposed that document; without
        // the clone it throws ObjectDisposedException here.
        Check(
            "returned data survives the parsed document",
            ok.Succeeded && PlayFabRestClient.ReadString(ok.Value, "PlayFabId") == "ABC");

        Check(
            "a non-JSON body is a network failure",
            PlayFabRestClient.ReadEnvelope("/x", HttpStatusCode.OK, "<html>proxy login</html>")
                .Status == PlatformStatus.NetworkFailure);

        Check(
            "401 maps to NotSignedIn",
            PlayFabRestClient.ReadEnvelope(
                "/x", HttpStatusCode.Unauthorized,
                """{"code":401,"status":"Unauthorized","error":"NotAuthenticated","errorCode":1074}""")
                .Status == PlatformStatus.NotSignedIn);

        Check(
            "AccountNotFound (1001) maps to NotSignedIn despite a 400",
            PlayFabRestClient.ReadEnvelope(
                "/x", HttpStatusCode.BadRequest,
                """{"code":400,"status":"BadRequest","error":"AccountNotFound","errorCode":1001}""")
                .Status == PlatformStatus.NotSignedIn);

        Check(
            "429 is transient, not fatal",
            PlayFabRestClient.ReadEnvelope("/x", HttpStatusCode.TooManyRequests, "{}")
                .Status == PlatformStatus.NetworkFailure);

        Check(
            "504 maps to TimedOut",
            PlayFabRestClient.ReadEnvelope("/x", HttpStatusCode.GatewayTimeout, "{}")
                .Status == PlatformStatus.TimedOut);

        var generic = PlayFabRestClient.ReadEnvelope(
            "/x", HttpStatusCode.BadRequest,
            """{"code":400,"status":"BadRequest","error":"InvalidParams","errorCode":1000,"errorMessage":"bad"}""");

        Check("an unrecognised error is Failed", generic.Status == PlatformStatus.Failed);

        Check(
            "diagnostics keep the PlayFab error name and code",
            generic.Result.Diagnostics?.Contains("InvalidParams", StringComparison.Ordinal) == true
                && generic.Result.Diagnostics?.Contains("1000", StringComparison.Ordinal) == true);

        Check(
            "the player-facing message never leaks the error code",
            generic.Message?.Contains("1000", StringComparison.Ordinal) == false);
    }

    // --- Session -----------------------------------------------------------

    private static void SessionState()
    {
        var now = DateTimeOffset.UtcNow;

        Check(
            "a fresh token is valid",
            TestSession(now.AddHours(24)).IsEntityTokenValid(now));

        Check(
            "an expired token is not valid",
            !TestSession(now.AddMinutes(-1)).IsEntityTokenValid(now));

        // The guard band is the interesting case: a token with four minutes left would
        // pass a naive comparison and then fail the request it was checked for.
        Check(
            "a token inside the guard band is treated as expired",
            !TestSession(now.AddMinutes(4)).IsEntityTokenValid(now));

        Check(
            "a token just outside the guard band is still valid",
            TestSession(now.AddMinutes(6)).IsEntityTokenValid(now));

        // Tokens are bearer credentials. Anything that logs one has given the account
        // away for as long as it lives, and ToString is what ends up in logs.
        var text = TestSession().ToString();
        Check(
            "ToString does not leak either token",
            !text.Contains("TICKET", StringComparison.Ordinal)
                && !text.Contains("ENTITYTOKEN", StringComparison.Ordinal));

        Check("ToString names the entity id", text.Contains("E-1", StringComparison.Ordinal));
    }

    private static void LoginParsing()
    {
        // The shape of a real /Client/LoginWithCustomID response. The entity key is two
        // levels deep inside EntityToken, which is an object rather than the string its
        // name suggests - the single easiest thing to get wrong here.
        using var document = JsonDocument.Parse("""
        {
          "SessionTicket": "A1B2C-ticket",
          "PlayFabId": "9C1F2A3B4C5D6E7F",
          "NewlyCreated": true,
          "EntityToken": {
            "EntityToken": "entity-token-value",
            "TokenExpiration": "2030-01-02T03:04:05Z",
            "Entity": { "Id": "1A2B3C4D5E6F", "Type": "title_player_account" }
          }
        }
        """);

        var parsed = PlayFabAuthClient.ReadSession(document.RootElement);

        Check("a login response parses", parsed.Succeeded);
        Check("session ticket is read", parsed.Value?.SessionTicket == "A1B2C-ticket");
        Check("entity id is read", parsed.Value?.EntityId == "1A2B3C4D5E6F");
        Check("entity type is read", parsed.Value?.EntityType == PlayFabSession.TitlePlayerEntityType);
        Check("legacy PlayFabId is kept", parsed.Value?.PlayFabId == "9C1F2A3B4C5D6E7F");
        Check("NewlyCreated is surfaced", parsed.Value?.AccountWasCreated == true);
        Check(
            "token expiry is parsed as UTC",
            parsed.Value?.EntityTokenExpires == new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));

        using var noTicket = JsonDocument.Parse("""
        {"PlayFabId":"p","EntityToken":{"EntityToken":"x","Entity":{"Id":"E","Type":"title_player_account"}}}
        """);
        Check(
            "a login with no session ticket fails",
            PlayFabAuthClient.ReadSession(noTicket.RootElement).Failed);

        using var noToken = JsonDocument.Parse("""{"SessionTicket":"t","PlayFabId":"p"}""");
        Check(
            "a login with no EntityToken fails rather than half-succeeding",
            PlayFabAuthClient.ReadSession(noToken.RootElement).Failed);

        using var noEntity = JsonDocument.Parse("""
        {"SessionTicket":"t","PlayFabId":"p","EntityToken":{"EntityToken":"x"}}
        """);
        Check(
            "a login with no entity id fails",
            PlayFabAuthClient.ReadSession(noEntity.RootElement).Failed);

        // An unparseable expiry must not become "never expires". Treating it as already
        // spent costs one refresh; treating it as immortal costs a failed match join.
        using var badExpiry = JsonDocument.Parse("""
        {"SessionTicket":"t","PlayFabId":"p","EntityToken":{"EntityToken":"x","TokenExpiration":"soon","Entity":{"Id":"E","Type":"title_player_account"}}}
        """);
        var lenient = PlayFabAuthClient.ReadSession(badExpiry.RootElement);
        Check(
            "an unreadable expiry is treated as already expired",
            lenient.Succeeded && !lenient.Value!.IsEntityTokenValid(DateTimeOffset.UtcNow));
    }

    private static async Task AuthRoundTrip()
    {
        using var handler = new StubHandler();
        handler.Respond("/Client/LoginWithCustomID", HttpStatusCode.OK, LoginPayload());

        using var services = new PlayFabOnlineServices(Config(TestTitleId), handler);

        PlayFabSession? observed = null;
        var raised = 0;
        services.SessionChanged += s => { observed = s; raised++; };

        Check("no entity id before sign-in", services.EntityId.Length == 0);
        Check("not signed in before sign-in", !services.IsSignedIn);

        var result = await services.SignInWithCustomIdAsync("spike-user").ConfigureAwait(false);

        Check("custom-id sign-in succeeds", result.Succeeded);

        // This is the value the whole online feature set was blocked on.
        Check("sign-in populates the entity id", services.EntityId == "1A2B3C4D5E6F");
        Check("sign-in reports signed in", services.IsSignedIn);
        Check("SessionChanged is raised once", raised == 1 && observed is not null);

        Check(
            "the request carries the title id and asks to create the account",
            handler.LastRequestBody?.Contains("\"TitleId\":\"A1B2C\"", StringComparison.Ordinal) == true
                && handler.LastRequestBody?.Contains("\"CreateAccount\":true", StringComparison.Ordinal) == true);

        Check(
            "an empty custom id is refused without a request",
            (await services.SignInWithCustomIdAsync("  ").ConfigureAwait(false)).Failed);

        // XR-013 age-group gating is checked in its own round trip so that this one's
        // SessionChanged count stays meaningful.

        Check(
            "an empty Xbox token is refused",
            (await services.SignInWithXboxAsync(string.Empty, createAccount: true).ConfigureAwait(false)).Status
                == PlatformStatus.NotSignedIn);

        // A failed sign-in must not destroy a session that is still working.
        Check("a failed sign-in leaves the existing session intact", services.IsSignedIn);

        services.SignOut();
        Check("sign-out clears the entity id", services.EntityId.Length == 0);
        Check("sign-out raises SessionChanged with null", raised == 2 && observed is null);
    }

    /// <summary>
    /// XR-013: the age-group decision has to reach the wire, not just the API surface.
    /// </summary>
    private static async Task XboxAgeGroupGate()
    {
        using var handler = new StubHandler();
        handler.Respond("/Client/LoginWithXbox", HttpStatusCode.OK, LoginPayload());

        using var services = new PlayFabOnlineServices(Config(TestTitleId), handler);

        await services.SignInWithXboxAsync("XBL3.0 x=1;token", createAccount: false)
            .ConfigureAwait(false);

        Check(
            "a child or unknown age group sends CreateAccount false",
            handler.LastRequestBody?.Contains("\"CreateAccount\":false", StringComparison.Ordinal) == true);

        await services.SignInWithXboxAsync("XBL3.0 x=1;token", createAccount: true)
            .ConfigureAwait(false);

        Check(
            "an adult or teen age group sends CreateAccount true",
            handler.LastRequestBody?.Contains("\"CreateAccount\":true", StringComparison.Ordinal) == true);
    }

    // --- Cloud save --------------------------------------------------------

    private static async Task CloudSave()
    {
        using var handler = new StubHandler();
        handler.Respond("/Client/LoginWithCustomID", HttpStatusCode.OK, LoginPayload());
        handler.Respond("/Client/UpdateUserData", HttpStatusCode.OK, Envelope("{}"));

        var root = Path.Combine(Path.GetTempPath(), "netrumble-spike-pf-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            using var services = new PlayFabOnlineServices(Config(TestTitleId), handler, root);
            await services.SignInWithCustomIdAsync("spike-user").ConfigureAwait(false);

            // The local tier is inherited, not reimplemented, and must keep working
            // exactly as before - it backs settings, which load before sign-in.
            var payload = "settings-blob"u8.ToArray();
            Check(
                "the inherited local tier still round-trips",
                (await services.GameSaves.SaveLocalAsync("settings", payload).ConfigureAwait(false)).Succeeded
                    && (await services.GameSaves.LoadLocalAsync("settings").ConfigureAwait(false))
                        .Value?.AsSpan().SequenceEqual(payload) == true);

            var cloud = await services.GameSaves
                .SaveCloudAsync("settings", payload).ConfigureAwait(false);
            Check("a cloud save succeeds", cloud.Succeeded);

            Check(
                "the blob is Base64-encoded into the request",
                handler.LastRequestBody?.Contains(
                    Convert.ToBase64String(payload), StringComparison.Ordinal) == true);

            // PlayFab caps a user-data value at 1,000 characters, which is 750 bytes
            // once Base64-encoded. Refusing here beats a generic 400 from the service.
            var oversized = new byte[PlayFabGameSaveService.MaxCloudBytes + 1];
            var refused = await services.GameSaves
                .SaveCloudAsync("big", oversized).ConfigureAwait(false);

            Check("an oversized cloud save is refused", refused.Failed);
            Check(
                "the refusal explains the limit",
                refused.Diagnostics?.Contains(
                    PlayFabGameSaveService.MaxCloudBytes.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    StringComparison.Ordinal) == true);

            Check(
                "a blob exactly at the limit is allowed",
                (await services.GameSaves
                    .SaveCloudAsync("edge", new byte[PlayFabGameSaveService.MaxCloudBytes])
                    .ConfigureAwait(false)).Succeeded);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }

        // GetUserData nests the payload one level deeper than it looks: Data.key.Value,
        // not Data.key.
        using var document = JsonDocument.Parse("""
        {"Data":{"settings":{"Value":"c2V0dGluZ3MtYmxvYg==","LastUpdated":"2024-01-01T00:00:00Z"}}}
        """);

        var read = PlayFabGameSaveService.ReadUserDataValue(document.RootElement, "settings");
        Check(
            "cloud load decodes the nested value",
            read.Succeeded && Encoding.UTF8.GetString(read.Value!) == "settings-blob");

        Check(
            "an absent key is empty, not an error",
            PlayFabGameSaveService.ReadUserDataValue(document.RootElement, "missing").Succeeded);

        // Convert.FromBase64String throws on malformed input, and this runs while
        // loading a player's settings. It must report, not throw, and must not
        // silently pretend the data was never there.
        using var corrupt = JsonDocument.Parse("""{"Data":{"settings":{"Value":"not base64!!"}}}""");
        Check(
            "a corrupt value is reported rather than thrown or discarded",
            PlayFabGameSaveService.ReadUserDataValue(corrupt.RootElement, "settings").Failed);
    }

    // --- Lobby -------------------------------------------------------------

    private static async Task Lobbies()
    {
        // Join codes.
        var code = PlayFabLobbyClient.NewJoinCode();
        Check("a generated join code is the right length", code.Length == PlayFabLobbyClient.JoinCodeLength);
        Check("a generated join code validates", PlayFabLobbyClient.IsJoinCode(code));

        Check(
            "join codes avoid characters people mishear",
            !code.Contains('I') && !code.Contains('O') && !code.Contains('0')
                && !code.Contains('1') && !code.Contains('S') && !code.Contains('5'));

        Check("a code is matched case-insensitively", PlayFabLobbyClient.IsJoinCode("abcde"));
        Check("a short code is rejected", !PlayFabLobbyClient.IsJoinCode("ABC"));
        Check("a code with an excluded letter is rejected", !PlayFabLobbyClient.IsJoinCode("ABCDO"));
        Check("null is not a code", !PlayFabLobbyClient.IsJoinCode(null));

        // The filter is interpolated into a quoted expression, so validation is what
        // stops a code from changing the query's meaning. Codes are letters and digits
        // only, which removes the possibility entirely.
        Check(
            "a code containing a quote is rejected before it reaches a filter",
            !PlayFabLobbyClient.IsJoinCode("AB'DE"));

        Check(
            "the search filter matches PlayFab's grammar",
            PlayFabLobbyClient.BuildJoinCodeFilter("abcde")
                == PlayFabLobbyClient.JoinCodeSearchKey + " eq 'ABCDE'");

        var spread = new HashSet<string>();
        for (var i = 0; i < 200; i++)
        {
            spread.Add(PlayFabLobbyClient.NewJoinCode());
        }

        // Not a uniformity test - just enough to catch a generator stuck on one value,
        // which would otherwise show up as every host claiming the same code.
        Check("codes are not constant", spread.Count > 190);

        using var handler = new StubHandler();
        handler.Respond("/Client/LoginWithCustomID", HttpStatusCode.OK, LoginPayload());

        using var services = new PlayFabOnlineServices(Config(TestTitleId), handler);
        var lobbies = services.Lobbies;

        Check(
            "hosting without a session is NotSignedIn",
            (await lobbies.CreateLobbyWithJoinCodeAsync(8).ConfigureAwait(false)).Status
                == PlatformStatus.NotSignedIn);

        await services.SignInWithCustomIdAsync("spike-user").ConfigureAwait(false);

        // First find returns a hit (code taken), second returns nothing, so create must
        // discard its first code and try again rather than claiming a code in use.
        handler.RespondInSequence(
            "/Lobby/FindLobbies",
            Envelope("""{"Lobbies":[{"LobbyId":"L0","ConnectionString":"CS0","SearchData":{"string_key1":"AAAAA"}}]}"""),
            Envelope("""{"Lobbies":[]}"""));

        handler.Respond(
            "/Lobby/CreateLobby",
            HttpStatusCode.OK,
            Envelope("""{"LobbyId":"L1","ConnectionString":"CS1"}"""));

        var created = await lobbies.CreateLobbyWithJoinCodeAsync(8).ConfigureAwait(false);

        Check("a lobby is created", created.Succeeded);
        Check("the lobby carries a connection string", created.Value?.ConnectionString == "CS1");
        Check("the lobby carries a join code", PlayFabLobbyClient.IsJoinCode(created.Value?.JoinCode));
        Check("a taken join code is retried", handler.CountFor("/Lobby/FindLobbies") == 2);

        Check(
            "the claimed code is published to the indexed search key",
            handler.BodyFor("/Lobby/CreateLobby")?.Contains(
                $"\"{PlayFabLobbyClient.JoinCodeSearchKey}\":\"{created.Value!.JoinCode}\"",
                StringComparison.Ordinal) == true);

        Check(
            "the owner entity is the signed-in player",
            handler.BodyFor("/Lobby/CreateLobby")?.Contains(
                "\"Id\":\"1A2B3C4D5E6F\"", StringComparison.Ordinal) == true);

        // This lobby was created without a network descriptor, so LobbyData is null. It
        // must be absent from the body rather than serialized as an explicit null:
        // PlayFab distinguishes the two on several endpoints, and that behaviour comes
        // from a JsonIgnoreCondition that now lives on PlayFabJsonContext instead of the
        // options object the transport used to build. Asserted so a future edit to that
        // attribute cannot quietly change what goes over the wire.
        Check(
            "a null optional field is omitted rather than sent as null",
            handler.BodyFor("/Lobby/CreateLobby")?.Contains(
                "LobbyData", StringComparison.Ordinal) == false);

        // A code nobody is hosting is a normal answer to a typo, not a service failure,
        // and must read differently in the UI.
        handler.Respond("/Lobby/FindLobbies", HttpStatusCode.OK, Envelope("""{"Lobbies":[]}"""));
        var miss = await lobbies.FindLobbyByJoinCodeAsync("ABCDE").ConfigureAwait(false);
        Check("an unused code succeeds with no lobby", miss.Succeeded && miss.Value is null);

        Check(
            "a malformed code is rejected without a search",
            (await lobbies.FindLobbyByJoinCodeAsync("!!").ConfigureAwait(false)).Failed);

        handler.Respond(
            "/Lobby/FindLobbies",
            HttpStatusCode.OK,
            Envelope("""
            {"Lobbies":[{"LobbyId":"L9","ConnectionString":"CS9","MaxPlayers":8,"CurrentPlayers":3,
             "Owner":{"Id":"OWNER","Type":"title_player_account"},
             "SearchData":{"string_key1":"ABCDE"}}]}
            """));

        var found = await lobbies.FindLobbyByJoinCodeAsync("abcde").ConfigureAwait(false);

        Check("a code resolves to a lobby", found.Succeeded && found.Value is not null);
        Check("the connection string comes back", found.Value?.ConnectionString == "CS9");

        // The half a create-only test would miss: the code must read back out of the
        // same indexed key it was written to.
        Check("the join code round-trips through SearchData", found.Value?.JoinCode == "ABCDE");
        Check("capacity comes back", found.Value?.MaxPlayers == 8 && found.Value?.CurrentPlayers == 3);
        Check("the owner entity comes back", found.Value?.OwnerEntityId == "OWNER");

        handler.Respond("/Lobby/JoinLobby", HttpStatusCode.OK, Envelope("""{"LobbyId":"L9"}"""));
        var joined = await lobbies.JoinLobbyAsync("CS9").ConfigureAwait(false);
        Check("joining by connection string returns the lobby id", joined.Succeeded && joined.Value == "L9");

        Check(
            "joining with an empty connection string is refused",
            (await lobbies.JoinLobbyAsync(" ").ConfigureAwait(false)).Failed);

        handler.Respond("/Lobby/LeaveLobby", HttpStatusCode.OK, Envelope("{}"));
        Check(
            "leaving succeeds",
            (await lobbies.LeaveLobbyAsync("L9").ConfigureAwait(false)).Succeeded);

        // Teardown that can fail is teardown that gets skipped.
        Check(
            "leaving nothing is not an error",
            (await lobbies.LeaveLobbyAsync(string.Empty).ConfigureAwait(false)).Succeeded);
    }

    // --- Provider composition ----------------------------------------------

    private static async Task Provider()
    {
        var root = Path.Combine(Path.GetTempPath(), "netrumble-spike-pfp-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            // A placeholder title must leave the provider alone entirely. An unwrapped
            // provider reports its own capabilities honestly for free, whereas a wrapper
            // that exists only to disable itself is a layer to reason about for nothing.
            await using (var unwrapped = PlatformProviderFactory.Create(
                PlatformProviderMode.Offline,
                root,
                new PlayFabConfiguration { TitleId = PlayFabConfiguration.PlaceholderTitleId }))
            {
                Check(
                    "a placeholder title id does not wrap the provider",
                    !unwrapped.Name.Contains("PlayFab", StringComparison.Ordinal));

                Check(
                    "an unwrapped provider claims no cloud save",
                    !unwrapped.Capabilities.HasFlag(PlatformCapabilities.CloudSave));
            }

            await using (var wrapped = PlatformProviderFactory.Create(
                PlatformProviderMode.Offline, root, Config(TestTitleId)))
            {
                Check("a real title id wraps the provider", wrapped.Name.EndsWith("+PlayFab", StringComparison.Ordinal));

                // The capability flags exist so the UI can hide what will not work.
                // This one now genuinely will, so it is claimed.
                Check(
                    "a configured provider claims cloud save",
                    wrapped.Capabilities.HasFlag(PlatformCapabilities.CloudSave));
            }

            // The whole point of the exercise: a signed-in user carrying a real entity
            // id, which every online feature has been gated on and which nothing in this
            // port could produce until now.
            using var handler = new StubHandler();
            handler.Respond("/Client/LoginWithCustomID", HttpStatusCode.OK, LoginPayload());

            await using var provider = new PlayFabPlatformProvider(
                PlatformProviderFactory.Create(PlatformProviderMode.Offline, root),
                Config(TestTitleId),
                handler);

            var developer = await provider.Identity
                .SignInAsync(new SignInOptions { DeveloperCustomId = "spike-user" })
                .ConfigureAwait(false);

            Check("sign-in through the provider succeeds", developer.Succeeded);
            Check(
                "PlatformUser.EntityId is populated from PlayFab",
                developer.Value?.EntityId == "1A2B3C4D5E6F");

            // Applied on read, not snapshotted, so a later re-login is reflected at once.
            Check(
                "CurrentUser also carries the entity id",
                provider.Identity.CurrentUser?.EntityId == "1A2B3C4D5E6F");

            Check(
                "the inner platform identity is preserved",
                provider.Identity.CurrentUser?.DisplayName is { Length: > 0 });

            await provider.Identity.SignOutAsync().ConfigureAwait(false);
            Check("sign-out drops the entity id", provider.Online.EntityId.Length == 0);

            // Without a developer id the shipping path needs an XSTS token this build
            // cannot obtain. It must degrade to an empty entity id, not to a failed
            // sign-in: the front end, settings and practice match all work without one.
            var xboxless = await provider.Identity
                .SignInAsync(SignInOptions.Interactive)
                .ConfigureAwait(false);

            Check("sign-in still succeeds with no PlayFab credential", xboxless.Succeeded);
            Check(
                "and honestly reports no entity id rather than inventing one",
                xboxless.Value?.EntityId.Length == 0);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    // --- Helpers -----------------------------------------------------------
    private static PlayFabConfiguration Config(string titleId) => new() { TitleId = titleId };

    private static PlayFabSession TestSession(DateTimeOffset? expires = null) => new()
    {
        SessionTicket = "TICKET",
        EntityToken = "ENTITYTOKEN",
        EntityId = "E-1",
        PlayFabId = "P-1",
        EntityTokenExpires = expires ?? DateTimeOffset.UtcNow.AddHours(24),
    };

    private static string Envelope(string data)
        => $$"""{"code":200,"status":"OK","data":{{data}}}""";

    private static string LoginPayload() => Envelope("""
    {
      "SessionTicket": "A1B2C-ticket",
      "PlayFabId": "9C1F2A3B4C5D6E7F",
      "NewlyCreated": false,
      "EntityToken": {
        "EntityToken": "entity-token-value",
        "TokenExpiration": "2999-01-01T00:00:00Z",
        "Entity": { "Id": "1A2B3C4D5E6F", "Type": "title_player_account" }
      }
    }
    """);

    private static void Check(string label, bool condition)
        => Console.WriteLine($"    {(condition ? "PASS" : "FAIL")} {label}");

    /// <summary>
    /// Stands in for the network, recording what was sent and replaying canned bodies.
    /// </summary>
    /// <remarks>
    /// This is the seam that makes the whole REST layer verifiable without a title id.
    /// It records requests as well as answering them, because half of what can be wrong
    /// here is in the request - the URL, the header, the encoding - and a stub that only
    /// returned responses would prove none of it.
    /// </remarks>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _canned = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Queue<string>> _sequences = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _bodies = new(StringComparer.Ordinal);

        public int RequestCount { get; private set; }

        public string? LastRequestBody { get; private set; }

        private HttpRequestMessage? _lastRequest;

        /// <summary>Answers every call to <paramref name="path"/> with the same body.</summary>
        public void Respond(string path, HttpStatusCode status, string body)
        {
            _canned[path] = (status, body);
            _sequences.Remove(path);
        }

        /// <summary>
        /// Answers successive calls to <paramref name="path"/> with successive bodies,
        /// repeating the last one once exhausted. Needed to model a join-code collision,
        /// where the same endpoint must answer differently the second time.
        /// </summary>
        public void RespondInSequence(string path, params string[] bodies)
            => _sequences[path] = new Queue<string>(bodies);

        public int CountFor(string path) => _counts.TryGetValue(path, out var n) ? n : 0;

        public string? BodyFor(string path) => _bodies.TryGetValue(path, out var body) ? body : null;

        public string? LastRequestHeader(string name)
            => _lastRequest is not null
                && _lastRequest.Headers.TryGetValues(name, out var values)
                    ? string.Join(",", values)
                    : null;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            _lastRequest = request;

            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            _counts[path] = CountFor(path) + 1;

            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (LastRequestBody is not null)
            {
                _bodies[path] = LastRequestBody;
            }

            if (_sequences.TryGetValue(path, out var queue) && queue.Count > 0)
            {
                var next = queue.Count == 1 ? queue.Peek() : queue.Dequeue();
                return Json(HttpStatusCode.OK, next);
            }

            return _canned.TryGetValue(path, out var canned)
                ? Json(canned.Status, canned.Body)

                // An unstubbed path is a bug in the check, not a scenario. Answering
                // with a distinctive 404 makes that obvious in the failure message
                // instead of looking like a PlayFab error.
                : Json(HttpStatusCode.NotFound, """{"code":404,"status":"NotFound","error":"SpikeStubHasNoResponse"}""");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body)
            => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
