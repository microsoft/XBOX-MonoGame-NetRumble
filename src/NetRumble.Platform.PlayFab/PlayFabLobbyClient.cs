using NetRumble.Platform.Networking;
using System.Text.Json;

namespace NetRumble.Platform.PlayFab;

/// <summary>
/// One lobby as PlayFab describes it, reduced to what the game needs.
/// </summary>
public sealed record PlayFabLobby
{
    public required string LobbyId { get; init; }

    /// <summary>
    /// The opaque string a client passes to <c>JoinLobby</c>. This is what an invite
    /// carries, and it is why an invite does not need to know the host's address.
    /// </summary>
    public required string ConnectionString { get; init; }

    /// <summary>The human-typable code that resolves to this lobby, if it has one.</summary>
    public string JoinCode { get; init; } = string.Empty;

    public int MaxPlayers { get; init; }
    public int CurrentPlayers { get; init; }
    public string OwnerEntityId { get; init; } = string.Empty;

    /// <summary>
    /// The wire protocol version the host advertised, or empty when it published none.
    /// </summary>
    /// <remarks>
    /// Empty is not "unknown, assume fine" - it is what a build from before the version
    /// check looks like, and <see cref="Core.Net.Wire.NRProtocol.IsCompatible"/> refuses
    /// it for exactly that reason.
    /// </remarks>
    public string ProtocolVersion { get; init; } = string.Empty;

    /// <summary>
    /// Arbitrary non-indexed lobby properties. The GDK Party provider uses this for the
    /// serialized Party network descriptor because the descriptor is too opaque and too
    /// long to be a search key; the five-character join code remains the indexed search
    /// value, while this data is read after that lookup succeeds.
    /// </summary>
    public IReadOnlyDictionary<string, string> LobbyData { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// PlayFab Lobby over REST: create, find by join code, join, leave.
/// </summary>
/// <remarks>
/// <para>
/// This closes the last of the netcode gaps in <c>docs/design-notes.md</c>: with no
/// lobby service there was nothing that could turn a five-character code, or an invite's
/// connection string, into a joinable session, so <c>IPartyService.JoinCode</c> had
/// nothing to resolve against on the GDK provider.
/// </para>
/// <para>
/// <b>Join codes are a search key, not a PlayFab feature.</b> PlayFab has no notion of
/// a short code; what it has is up to thirty indexed string keys per lobby and a query
/// language over them. So the host generates a code, publishes it as
/// <see cref="JoinCodeSearchKey"/>, and a client finds the lobby by filtering on that
/// key. The consequence is that codes are unique only by luck - see
/// <see cref="NewJoinCode"/> - so a host must verify a code is unused before claiming
/// it, which is what <see cref="CreateLobbyWithJoinCodeAsync"/> does.
/// </para>
/// <para>
/// <b>This is the directory, not the transport.</b> A lobby carries the connection
/// string and the roster; the actual game traffic still flows over
/// <see cref="IPartyService"/>. Keeping the two apart is what lets the LAN transport
/// and a PlayFab-backed one coexist behind the same match code.
/// </para>
/// </remarks>
internal sealed class PlayFabLobbyClient
{
    /// <summary>
    /// The indexed lobby search key that carries the join code.
    /// </summary>
    /// <remarks>
    /// PlayFab names these positionally - <c>string_key1</c> through
    /// <c>string_key30</c> - so the number is arbitrary but must never change without a
    /// migration: an old client publishing to key 1 and a new one searching key 2 would
    /// simply never find each other, with no error anywhere to explain it.
    /// </remarks>
    public const string JoinCodeSearchKey = "string_key1";

    /// <summary>
    /// Characters a join code may contain.
    /// </summary>
    /// <remarks>
    /// Deliberately identical to the LAN transport's alphabet so a code looks the same
    /// wherever it came from. I, O, 0, 1, S and 5 are absent because these codes get
    /// read aloud over voice chat, and those six are the pairs people mishear.
    /// 30 characters over 5 positions is about 24 million codes.
    /// </remarks>
    public const string JoinCodeAlphabet = "ABCDEFGHJKLMNPQRTUVWXYZ2346789";

    /// <summary>Length of a join code.</summary>
    public const int JoinCodeLength = 5;

    /// <summary>
    /// How many times to retry when a generated code is already taken.
    /// </summary>
    /// <remarks>
    /// With 24 million codes and a realistic concurrent lobby count, one collision is
    /// unlikely and two in a row is remote. A bounded retry is still needed, because the
    /// alternative - looping until a code is free - hangs the host forever if the search
    /// endpoint starts returning everything.
    /// </remarks>
    private const int JoinCodeAttempts = 5;

    private readonly PlayFabRestClient _client;
    private readonly Func<PlayFabSession?> _session;

    public PlayFabLobbyClient(PlayFabRestClient client, Func<PlayFabSession?> session)
    {
        _client = client;
        _session = session;
    }

    /// <summary>Generates a random join code. Not guaranteed unique; the caller checks.</summary>
    public static string NewJoinCode()
    {
        return string.Create(JoinCodeLength, 0, static (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = JoinCodeAlphabet[Random.Shared.Next(JoinCodeAlphabet.Length)];
            }
        });
    }

    /// <summary>True when <paramref name="code"/> could be a join code.</summary>
    /// <remarks>
    /// Case-insensitive, because a player typing a code will not use the shift key and
    /// should not be told their correct code is wrong.
    /// </remarks>
    public static bool IsJoinCode(string? code)
    {
        if (code is null || code.Length != JoinCodeLength)
        {
            return false;
        }

        foreach (var c in code)
        {
            if (!JoinCodeAlphabet.Contains(char.ToUpperInvariant(c), StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Builds the PlayFab search filter that finds the lobby holding a join code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Internal and separate so the spike can prove the filter's exact text without a
    /// title. PlayFab's filter grammar is OData-like and unforgiving; a malformed filter
    /// is a 400, and a filter that is merely <i>wrong</i> silently returns nothing,
    /// which is far harder to notice.
    /// </para>
    /// <para>
    /// The code is normalised to upper case and validated by the caller before reaching
    /// here. Validation matters for more than tidiness: the value is interpolated into a
    /// quoted string in a query expression, so an unvalidated code containing a quote
    /// could change the meaning of the filter. <see cref="IsJoinCode"/> restricts the
    /// input to letters and digits, which removes that possibility entirely.
    /// </para>
    /// </remarks>
    internal static string BuildJoinCodeFilter(string joinCode)
        => $"{JoinCodeSearchKey} eq '{joinCode.ToUpperInvariant()}'";

    /// <summary>
    /// Creates a lobby, claiming an unused join code for it.
    /// </summary>
    /// <param name="maxPlayers">Lobby capacity, including the host.</param>
    /// <param name="isPrivate">
    /// When true the lobby uses the <c>Private</c> access policy, which removes it from
    /// <c>FindLobbies</c> entirely - <b>including the join-code lookup</b>, because that
    /// lookup is just a <c>FindLobbies</c> filter over <see cref="JoinCodeSearchKey"/>
    /// and not a PlayFab feature. A private lobby is reachable only by an invite that
    /// already carries its connection string, so a lobby that is meant to be joinable by
    /// code must be created public and gated by the secrecy of the code itself.
    /// </param>
    public async Task<PlatformResult<PlayFabLobby>> CreateLobbyWithJoinCodeAsync(
        int maxPlayers,
        bool isPrivate = false,
        IReadOnlyDictionary<string, string>? lobbyData = null,
        CancellationToken cancellationToken = default)
    {
        var session = _session();
        if (session is null)
        {
            return PlatformResult<PlayFabLobby>.Fail(
                PlatformStatus.NotSignedIn,
                "You need to be signed in to host a match.",
                "CreateLobbyWithJoinCodeAsync called with no PlayFab session.");
        }

        for (var attempt = 0; attempt < JoinCodeAttempts; attempt++)
        {
            var code = NewJoinCode();

            // Check before claiming. Two hosts that generate the same code would other-
            // wise both publish it, and every subsequent player typing that code would
            // land in whichever lobby the search happened to return first.
            var existing = await FindLobbyByJoinCodeAsync(code, cancellationToken)
                .ConfigureAwait(false);

            if (existing.Failed && existing.Status != PlatformStatus.Failed)
            {
                // A network or auth failure during the check says nothing about whether
                // the code is free, so do not proceed as though it were.
                return PlatformResult<PlayFabLobby>.Fail(
                    existing.Status,
                    "Could not create the match.",
                    existing.Result.Diagnostics);
            }

            if (existing.Succeeded && existing.Value is not null)
            {
                continue;
            }

            var owner = new EntityKey { Id = session.EntityId, Type = session.EntityType };

            var result = await _client.PostAsync(
                "/Lobby/CreateLobby",
                new CreateLobbyRequest
                {
                    Owner = owner,
                    MaxPlayers = (uint)Math.Clamp(maxPlayers, 2, 128),
                    AccessPolicy = isPrivate ? "Private" : "Public",

                    // Connectionless, and therefore no owner migration.
                    //
                    // UseConnections=true means "this lobby is only searchable while at
                    // least one member holds a live PubSub connection handle". That
                    // handle is established by the PlayFab Lobby SDK, which this client
                    // is not - it is a REST client by design, so its members' handles are
                    // permanently empty and the lobby is permanently unsearchable. The
                    // host would create a lobby, publish a join code and sit in it while
                    // every FindLobbies for that code returned nothing.
                    //
                    // PlayFab rejects Automatic/Manual migration without connections, and
                    // the policy was never worth anything here anyway: PartyMatchNetwork
                    // has a single authority and does not migrate, so a lobby outliving
                    // its host only preserves a roster for a match that has already
                    // ended.
                    OwnerMigrationPolicy = "None",
                    UseConnections = false,
                    Members = [new LobbyMember { MemberEntity = owner }],
                    SearchData = new Dictionary<string, string>
                    {
                        [JoinCodeSearchKey] = code,

                        // Published on the lobby rather than exchanged during the
                        // handshake because the lobby is the last point at which two
                        // peers still agree on how to talk: everything after it runs over
                        // the codec that a mismatch breaks.
                        [NRProtocol.LobbyKey] = NRProtocol.VersionString(),
                    },
                    LobbyData = lobbyData,
                },
                PlayFabJsonContext.Default.CreateLobbyRequest,
                PlayFabAuth.EntityToken,
                session,
                cancellationToken).ConfigureAwait(false);

            if (result.Failed)
            {
                return PlatformResult<PlayFabLobby>.Fail(
                    result.Status,
                    "Could not create the match.",
                    result.Result.Diagnostics);
            }

            return PlatformResult<PlayFabLobby>.Ok(new PlayFabLobby
            {
                LobbyId = PlayFabRestClient.ReadString(result.Value, "LobbyId"),
                ConnectionString = PlayFabRestClient.ReadString(result.Value, "ConnectionString"),
                JoinCode = code,
                MaxPlayers = maxPlayers,
                CurrentPlayers = 1,
                OwnerEntityId = session.EntityId,
                LobbyData = lobbyData is null
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : new Dictionary<string, string>(lobbyData, StringComparer.Ordinal),
            });
        }

        return PlatformResult<PlayFabLobby>.Fail(
            PlatformStatus.Failed,
            "Could not create the match. Please try again.",
            $"Failed to find an unused join code in {JoinCodeAttempts} attempts.");
    }

    /// <summary>
    /// Finds the lobby publishing <paramref name="joinCode"/>, or a null value when
    /// no lobby has it.
    /// </summary>
    /// <remarks>
    /// A code that matches nothing succeeds with a null value rather than failing.
    /// "Nobody is hosting that code" is a normal answer to a player's typo and needs a
    /// different message from "the service is down"; conflating them would show a
    /// network error for a mistyped letter.
    /// </remarks>
    public async Task<PlatformResult<PlayFabLobby?>> FindLobbyByJoinCodeAsync(
        string joinCode,
        CancellationToken cancellationToken = default)
    {
        if (!IsJoinCode(joinCode))
        {
            return PlatformResult<PlayFabLobby?>.Fail(
                PlatformStatus.Failed,
                $"That code doesn't look right. Codes are {JoinCodeLength} letters and numbers.",
                $"Rejected join code '{joinCode}' before searching.");
        }

        var result = await _client.PostAsync(
            "/Lobby/FindLobbies",
            new FindLobbiesRequest
            {
                Filter = BuildJoinCodeFilter(joinCode),

                // One is all that should exist. Asking for a couple more makes a
                // collision visible in a log rather than invisible behind a LIMIT 1.
                Pagination = new PaginationRequest { PageSizeRequested = 4 },
            },
            PlayFabJsonContext.Default.FindLobbiesRequest,
            PlayFabAuth.EntityToken,
            _session(),
            cancellationToken).ConfigureAwait(false);

        if (result.Failed)
        {
            return PlatformResult<PlayFabLobby?>.Fail(
                result.Status,
                "Could not look up that match.",
                result.Result.Diagnostics);
        }

        var lobbies = ReadLobbies(result.Value);

        return PlatformResult<PlayFabLobby?>.Ok(lobbies.Count > 0 ? lobbies[0] : null);
    }

    /// <summary>Joins a lobby by the connection string a code lookup or invite produced.</summary>
    public async Task<PlatformResult<string>> JoinLobbyAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        var session = _session();
        if (session is null)
        {
            return PlatformResult<string>.Fail(
                PlatformStatus.NotSignedIn,
                "You need to be signed in to join a match.",
                "JoinLobbyAsync called with no PlayFab session.");
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return PlatformResult<string>.Fail(
                PlatformStatus.Failed,
                "That invite is no longer valid.",
                "JoinLobbyAsync called with an empty connection string.");
        }

        var result = await _client.PostAsync(
            "/Lobby/JoinLobby",
            new JoinLobbyRequest
            {
                MemberEntity = new EntityKey { Id = session.EntityId, Type = session.EntityType },
                ConnectionString = connectionString,
            },
            PlayFabJsonContext.Default.JoinLobbyRequest,
            PlayFabAuth.EntityToken,
            session,
            cancellationToken).ConfigureAwait(false);

        if (result.Failed)
        {
            return PlatformResult<string>.Fail(
                result.Status,
                "Could not join that match.",
                result.Result.Diagnostics);
        }

        return PlatformResult<string>.Ok(PlayFabRestClient.ReadString(result.Value, "LobbyId"));
    }

    /// <summary>Reads one lobby by id, including its non-indexed lobby data.</summary>
    /// <remarks>
    /// Join-code lookup returns enough data for the common path, but an invite joins by
    /// PlayFab connection string and learns the lobby id only after <c>JoinLobby</c>.
    /// Keeping this as a general read rather than a Party-specific helper lets future
    /// lobby metadata share the same endpoint without expanding the transport API.
    /// </remarks>
    public async Task<PlatformResult<PlayFabLobby?>> GetLobbyAsync(
        string lobbyId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(lobbyId))
        {
            return PlatformResult<PlayFabLobby?>.Fail(
                PlatformStatus.Failed,
                "That match could not be read.",
                "GetLobbyAsync called with an empty lobby id.");
        }

        var result = await _client.PostAsync(
            "/Lobby/GetLobby",
            new GetLobbyRequest { LobbyId = lobbyId },
            PlayFabJsonContext.Default.GetLobbyRequest,
            PlayFabAuth.EntityToken,
            _session(),
            cancellationToken).ConfigureAwait(false);

        if (result.Failed)
        {
            return PlatformResult<PlayFabLobby?>.Fail(
                result.Status,
                "Could not read the match.",
                result.Result.Diagnostics);
        }

        if (result.Value.TryGetProperty("Lobby", out var lobby)
            && lobby.ValueKind == JsonValueKind.Object)
        {
            return PlatformResult<PlayFabLobby?>.Ok(ReadLobby(lobby));
        }

        return PlatformResult<PlayFabLobby?>.Ok(ReadLobby(result.Value));
    }

    /// <summary>Leaves a lobby. Best-effort: a failure here must not block leaving a match.</summary>
    public async Task<PlatformResult> LeaveLobbyAsync(
        string lobbyId,
        CancellationToken cancellationToken = default)
    {
        var session = _session();
        if (session is null || string.IsNullOrWhiteSpace(lobbyId))
        {
            // Nothing to leave. Reporting success keeps teardown paths simple, and
            // teardown that can fail is teardown that gets skipped.
            return PlatformResult.Ok();
        }

        var result = await _client.PostAsync(
            "/Lobby/LeaveLobby",
            new LeaveLobbyRequest
            {
                MemberEntity = new EntityKey { Id = session.EntityId, Type = session.EntityType },
                LobbyId = lobbyId,
            },
            PlayFabJsonContext.Default.LeaveLobbyRequest,
            PlayFabAuth.EntityToken,
            session,
            cancellationToken).ConfigureAwait(false);

        return result.Succeeded
            ? PlatformResult.Ok()
            : PlatformResult.Fail(
                result.Status,
                "Could not leave the match cleanly.",
                result.Result.Diagnostics);
    }

    /// <summary>
    /// Deletes a lobby outright, for the owner leaving a session nothing can inherit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lobby is created with <c>OwnerMigrationPolicy = "None"</c> because
    /// <c>PartyMatchNetwork</c> has a single authority and does not migrate, so an owner
    /// who leaves takes the session with them. <c>LeaveLobby</c> alone does not say
    /// that: the row survives, still publishing the join code in its indexed search
    /// data, so <c>FindLobbies</c> keeps handing that code out and every client who
    /// types it joins a lobby whose host is long gone - the "dead lobby that no one can
    /// join" from the bug bash.
    /// </para>
    /// <para>
    /// Best-effort like <see cref="LeaveLobbyAsync"/>, and for the same reason: teardown
    /// that can fail is teardown that gets skipped. A delete that the service refuses
    /// falls back to leaving, so the departing owner is at least no longer a member.
    /// </para>
    /// </remarks>
    public async Task<PlatformResult> DeleteLobbyAsync(
        string lobbyId,
        CancellationToken cancellationToken = default)
    {
        var session = _session();
        if (session is null || string.IsNullOrWhiteSpace(lobbyId))
        {
            return PlatformResult.Ok();
        }

        var result = await _client.PostAsync(
            "/Lobby/DeleteLobby",
            new DeleteLobbyRequest { LobbyId = lobbyId },
            PlayFabJsonContext.Default.DeleteLobbyRequest,
            PlayFabAuth.EntityToken,
            session,
            cancellationToken).ConfigureAwait(false);

        if (result.Succeeded)
        {
            return PlatformResult.Ok();
        }

        // A title that has not granted the caller delete rights, or a service that has
        // already reaped the lobby, still leaves the owner holding membership. Leaving
        // is strictly better than doing nothing.
        return await LeaveLobbyAsync(lobbyId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads lobbies out of a <c>FindLobbies</c> response.
    /// </summary>
    /// <remarks>
    /// Internal so the spike can prove the shape - in particular that the join code is
    /// read back out of <c>SearchData</c> under the same key it was published to, which
    /// is the half of the round trip that a create-only test would not catch.
    /// </remarks>
    internal static IReadOnlyList<PlayFabLobby> ReadLobbies(JsonElement data)
    {
        if (!data.TryGetProperty("Lobbies", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<PlayFabLobby>();
        }

        var lobbies = new List<PlayFabLobby>(rows.GetArrayLength());

        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            lobbies.Add(ReadLobby(row));
        }

        return lobbies;
    }

    private static PlayFabLobby ReadLobby(JsonElement row)
    {
        var joinCode = string.Empty;
        var protocolVersion = string.Empty;
        if (row.TryGetProperty("SearchData", out var searchData)
            && searchData.ValueKind == JsonValueKind.Object)
        {
            joinCode = PlayFabRestClient.ReadString(searchData, JoinCodeSearchKey);
            protocolVersion = PlayFabRestClient.ReadString(searchData, NRProtocol.LobbyKey);
        }

        var ownerEntityId = string.Empty;
        if (row.TryGetProperty("Owner", out var owner) && owner.ValueKind == JsonValueKind.Object)
        {
            ownerEntityId = PlayFabRestClient.ReadString(owner, "Id");
        }

        return new PlayFabLobby
        {
            LobbyId = PlayFabRestClient.ReadString(row, "LobbyId"),
            ConnectionString = PlayFabRestClient.ReadString(row, "ConnectionString"),
            JoinCode = joinCode,
            MaxPlayers = ReadInt(row, "MaxPlayers"),
            CurrentPlayers = ReadInt(row, "CurrentPlayers"),
            OwnerEntityId = ownerEntityId,
            ProtocolVersion = protocolVersion,
            LobbyData = ReadStringMap(row, "LobbyData"),
        };
    }

    private static IReadOnlyDictionary<string, string> ReadStringMap(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var data)
            || data.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in data.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                result[property.Name] = property.Value.GetString() ?? string.Empty;
            }
        }

        return result;
    }

    private static int ReadInt(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed)
                ? parsed
                : 0;

    internal sealed class EntityKey
    {
        public required string Id { get; init; }
        public required string Type { get; init; }
    }

    internal sealed class LobbyMember
    {
        public required EntityKey MemberEntity { get; init; }
    }

    internal sealed class CreateLobbyRequest
    {
        public required EntityKey Owner { get; init; }
        public required uint MaxPlayers { get; init; }
        public required string AccessPolicy { get; init; }
        public required string OwnerMigrationPolicy { get; init; }
        public required bool UseConnections { get; init; }
        public required IReadOnlyList<LobbyMember> Members { get; init; }
        public required IReadOnlyDictionary<string, string> SearchData { get; init; }
        public IReadOnlyDictionary<string, string>? LobbyData { get; init; }
    }

    internal sealed class FindLobbiesRequest
    {
        public required string Filter { get; init; }
        public required PaginationRequest Pagination { get; init; }
    }

    internal sealed class PaginationRequest
    {
        public required uint PageSizeRequested { get; init; }
    }

    internal sealed class JoinLobbyRequest
    {
        public required EntityKey MemberEntity { get; init; }
        public required string ConnectionString { get; init; }
    }

    internal sealed class LeaveLobbyRequest
    {
        public required EntityKey MemberEntity { get; init; }
        public required string LobbyId { get; init; }
    }

    internal sealed class GetLobbyRequest
    {
        public required string LobbyId { get; init; }
    }

    internal sealed class DeleteLobbyRequest
    {
        public required string LobbyId { get; init; }
    }
}
