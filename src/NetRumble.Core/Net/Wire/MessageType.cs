namespace NetRumble.Core.Net.Wire;

/// <summary>
/// The first byte of every packet, identifying which message follows.
/// </summary>
/// <remarks>
/// <para>
/// One entry per <c>@rpc</c> function in <c>scripts/autoload/net_manager.gd</c>. Godot's
/// high-level <c>MultiplayerAPI</c> assigned these ids itself from the method names; with
/// raw Party messages the title owns the mapping, so it is written down explicitly here.
/// </para>
/// <para>
/// <b>Values are a wire contract.</b> Never renumber an existing entry - two builds with
/// different numbering will silently misinterpret each other's traffic rather than fail
/// cleanly. Add new messages at the end of a block.
/// </para>
/// </remarks>
public enum MessageType : byte
{
    /// <summary>Not a real message. Guards against a zero-filled or truncated packet.</summary>
    None = 0,

    // --- Roster (reliable) --------------------------------------------------

    /// <summary>Host to client: identify yourself.</summary>
    RequestIdentity = 1,

    /// <summary>Client to host: name, entity id and chosen appearance.</summary>
    SubmitIdentity = 2,

    /// <summary>Host to client: one player's roster row.</summary>
    RosterEntry = 3,

    /// <summary>Host to client: a player left.</summary>
    PlayerLeft = 4,

    /// <summary>Client to host: lobby ready state changed.</summary>
    SubmitReady = 5,

    /// <summary>Client to host: appearance changed.</summary>
    SubmitAppearance = 6,

    /// <summary>Client to host: finished loading into the match.</summary>
    SubmitLoaded = 7,

    // 8 was GameMode: the host's selected GameModeType, published on a change and again
    // to each joining peer. Retired with the mode picker - Deathmatch is the only mode -
    // and left as a gap rather than reused, because reusing an id is the renumbering this
    // enum's remarks forbid. See NRProtocol.WireVersion 4.

    /// <summary>
    /// Host to client, sent only to the peer being refused: this session cannot accept
    /// new players right now (XR-003). Used when a peer connects after the match has
    /// already left the lobby's joining/warm-up phase - the host does not add it to the
    /// roster and sends this instead, so the refused peer can show why rather than
    /// sitting in a lobby that will never call it forward.
    /// </summary>
    JoinRefused = 9,

    /// <summary>
    /// Host to client: the complete authoritative roster. Reconciles missed joins,
    /// departures and row updates instead of relying on every incremental message having
    /// arrived before a client's lobby handler was attached.
    /// </summary>
    RosterSnapshot = 10,

    // --- Match flow (host to client) ----------------------------------------

    /// <summary>Reliable. The authoritative match state.</summary>
    MatchState = 20,

    /// <summary>Reliable. Pre-match countdown, in whole seconds.</summary>
    Countdown = 21,

    /// <summary>Unreliable sequenced. The authoritative match clock.</summary>
    MatchClock = 22,

    /// <summary>Reliable. A score changed.</summary>
    ScoreUpdated = 23,

    /// <summary>Reliable. Final standings.</summary>
    MatchCompleted = 24,

    // --- World replication (host to client) ---------------------------------

    /// <summary>Reliable. The world layout, sent once per match.</summary>
    MatchCreated = 40,

    /// <summary>Reliable. Final pre-match positions.</summary>
    MatchStarting = 41,

    /// <summary>Unreliable sequenced. Periodic authoritative state.</summary>
    WorldSnapshot = 42,

    /// <summary>Reliable. A projectile entered the world.</summary>
    ProjectileSpawned = 43,

    /// <summary>Reliable. A projectile detonated, with resolved damage.</summary>
    ProjectileDetonated = 44,

    /// <summary>Reliable. A power-up appeared.</summary>
    PowerUpSpawned = 45,

    /// <summary>Reliable. A power-up was collected.</summary>
    PowerUpCollected = 46,

    /// <summary>Reliable. A ship respawned.</summary>
    ShipSpawned = 47,

    /// <summary>Reliable. A ship was destroyed.</summary>
    ShipDestroyed = 48,

    /// <summary>Unreliable. A one-shot cosmetic effect.</summary>
    GameplayEvent = 49,

    /// <summary>
    /// Reliable. An asteroid broke apart, carrying every fragment it threw off.
    /// </summary>
    /// <remarks>
    /// Reliable because, unlike a snapshot, this cannot be re-derived: a client that
    /// misses it keeps a rock nobody else has and never learns about the fragments,
    /// which the snapshot stream then silently skips.
    /// </remarks>
    AsteroidSplit = 50,

    // --- Input (client to host) ---------------------------------------------

    /// <summary>Unreliable sequenced. One frame of a client's ship input.</summary>
    ShipInput = 60,
}
