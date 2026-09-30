namespace NetRumble.Platform.PlayFab;

/// <summary>
/// The credentials a successful PlayFab login hands back, and the only state the
/// REST services need in order to make authenticated calls.
/// </summary>
/// <remarks>
/// <para>
/// PlayFab issues two different credentials from one login and they are not
/// interchangeable, which is the single most common source of 401s against this API:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     <see cref="SessionTicket"/> authenticates the <c>/Client/*</c> endpoints - the
///     player-scoped legacy surface, which is where user data lives.
///     It travels in the <c>X-Authorization</c> header.
///     </description>
///   </item>
///   <item>
///     <description>
///     <see cref="EntityToken"/> authenticates the entity endpoints - <c>/Lobby/*</c>,
///     <c>/Profile/*</c>, <c>/Event/*</c> - which is the newer surface everything
///     multiplayer is built on. It travels in the <c>X-EntityToken</c> header.
///     </description>
///   </item>
/// </list>
/// <para>
/// <see cref="EntityId"/> is the value the rest of the game has been waiting for:
/// <see cref="PlatformUser.EntityId"/>, the id Party and Lobby key players by. It is a
/// durable account identifier, so it stays inside the platform layer - nothing the title
/// publishes to peers or to the shell carries it (XR-014).
/// </para>
/// <para>
/// This is a record so a refreshed session replaces the old one wholesale. Tokens are
/// never mutated in place, because a half-updated session - new token, stale expiry -
/// is a bug that only shows up an hour later under load.
/// </para>
/// </remarks>
public sealed record PlayFabSession
{
    /// <summary>The entity type for a player account. PlayFab's own constant.</summary>
    public const string TitlePlayerEntityType = "title_player_account";

    /// <summary>
    /// How close to expiry a token is still considered usable.
    /// </summary>
    /// <remarks>
    /// A token that expires in four minutes will pass a naive check and then fail the
    /// request it was checked for, because the round trip is not instant and clocks
    /// are not identical. Treating the last five minutes as already expired trades a
    /// slightly early refresh for never serving a request with a dead token.
    /// </remarks>
    public static readonly TimeSpan ExpiryGuardBand = TimeSpan.FromMinutes(5);

    /// <summary>Authenticates <c>/Client/*</c> calls. Sent as <c>X-Authorization</c>.</summary>
    public required string SessionTicket { get; init; }

    /// <summary>Authenticates entity calls. Sent as <c>X-EntityToken</c>.</summary>
    public required string EntityToken { get; init; }

    /// <summary>
    /// The entity id - the PlayFab identity of this player, as Lobby and Party use it.
    /// </summary>
    public required string EntityId { get; init; }

    /// <summary>Entity type; <see cref="TitlePlayerEntityType"/> for a normal login.</summary>
    public string EntityType { get; init; } = TitlePlayerEntityType;

    /// <summary>
    /// The legacy per-title player id. Distinct from <see cref="EntityId"/>, and still
    /// what the <c>/Client/*</c> endpoints return in user-data rows, so it is kept
    /// in order to recognise the local player's own row.
    /// </summary>
    public required string PlayFabId { get; init; }

    /// <summary>When <see cref="EntityToken"/> stops being accepted.</summary>
    public required DateTimeOffset EntityTokenExpires { get; init; }

    /// <summary>True when this login created the account rather than resuming one.</summary>
    public bool AccountWasCreated { get; init; }

    /// <summary>
    /// True when the entity token is still safely usable, allowing for
    /// <see cref="ExpiryGuardBand"/>.
    /// </summary>
    public bool IsEntityTokenValid(DateTimeOffset now)
        => EntityToken.Length > 0 && now + ExpiryGuardBand < EntityTokenExpires;

    /// <summary>
    /// A description safe to write to a log.
    /// </summary>
    /// <remarks>
    /// Deliberately excludes both tokens. They are bearer credentials: anything that
    /// logs one has handed an attacker the account for as long as it lives. The entity
    /// id is not a secret - it is broadcast to every player in the match - so it is
    /// fine to include, and it is the only part anyone debugging actually needs.
    /// </remarks>
    public override string ToString()
        => $"{EntityType}:{EntityId} (PlayFabId {PlayFabId}, token expires {EntityTokenExpires:u})";
}
