using System.Numerics;
using NetRumble.Platform;

namespace NetRumble.Core.Net;

/// <summary>
/// Everything the simulation and the match rules need from the networking layer: the
/// player roster, the broadcasts the host makes, and the inbound messages a client
/// mirrors.
/// </summary>
/// <remarks>
/// <para>
/// In the Godot project this was the <c>NetManager</c> autoload, reachable as a global
/// from anywhere. Making it an interface keeps <c>NetRumble.Core</c> free of any
/// transport dependency, so the simulation and the match rules stay unit-testable with no
/// sockets involved, and lets a real transport drop in without touching either.
/// </para>
/// <para>
/// It is deliberately one interface rather than a match half and a world half. The two
/// overlap - the director broadcasts ship spawn and destroy, the world consumes them - and
/// splitting it would mean threading two references through the same call sites for no
/// isolation benefit.
/// </para>
/// <para>
/// The broadcast methods are no-ops on the offline implementation rather than being
/// conditionally skipped by the caller: the GDScript always called them and let
/// <c>NetManager</c> decide, and keeping that shape means the host code path is identical
/// online and offline.
/// </para>
/// </remarks>
public interface IMatchNetwork
{
    /// <summary>True when this peer owns the authoritative simulation.</summary>
    bool IsHost { get; }

    /// <summary>
    /// True for a single-player session with no transport at all. Distinct from
    /// <see cref="IsHost"/>: an online host is authoritative but not offline, and the
    /// last-player-standing rule applies to it but not to an offline session.
    /// </summary>
    bool IsOffline { get; }

    /// <summary>Peer id of the player on this machine.</summary>
    int LocalPeerId { get; }

    /// <summary>Every player currently in the session, keyed by peer id.</summary>
    IReadOnlyDictionary<int, PlayerState> Players { get; }

    /// <summary>The player on this machine, or <c>null</c> if there is not one.</summary>
    PlayerState? LocalPlayer { get; }

    /// <summary>
    /// Players in a stable, peer-id order. Used when building the world so every peer
    /// assigns the same ship ids.
    /// </summary>
    IReadOnlyList<PlayerState> SortedPlayers();

    /// <summary>Players ordered best score first, for the final standings.</summary>
    IReadOnlyList<PlayerState> PlayersByScore();

    // --- Match flow ---------------------------------------------------------

    /// <summary>Host: publishes the authoritative match state to every client.</summary>
    void SetMatchState(MatchState state);

    /// <summary>
    /// Clears the state that belongs to one match so the same session can run another
    /// (XR-003).
    /// </summary>
    /// <remarks>
    /// Scores, ready flags, spawn ids and the in-game flag all describe a match rather
    /// than a player, and a session that survives to a second match carries every one of
    /// them forward if nothing clears them: the next lobby opens with the previous
    /// match's scores, everyone already readied, and an auto-start that fires before
    /// anyone has chosen a ship. The host is authoritative, so it clears the roster and
    /// republishes it; a client clears its own view and is corrected by that snapshot if
    /// the two ever disagree.
    /// </remarks>
    void ResetForNextMatch();

    /// <summary>Host: publishes the pre-match countdown, in whole seconds.</summary>
    void BroadcastCountdown(int secondsRemaining);

    /// <summary>Host: publishes the authoritative match clock.</summary>
    void BroadcastMatchClock(float elapsed);

    /// <summary>Host: announces a score change, including the delta that caused it.</summary>
    void BroadcastScoreUpdated(int peerId, int score, int delta);

    /// <summary>Host: announces the final result.</summary>
    void BroadcastMatchCompleted(MatchResult result);

    // --- World replication --------------------------------------------------

    /// <summary>Host: announces the world layout, exactly once per match.</summary>
    void BroadcastMatchCreated(MatchCreatedPayload payload);

    /// <summary>Host: announces the final pre-match positions.</summary>
    void BroadcastMatchStarting(MatchStartingPayload payload);

    /// <summary>Host: publishes a periodic authoritative snapshot.</summary>
    void BroadcastWorldSnapshot(WorldSnapshot snapshot);

    /// <summary>Host: announces a projectile entering the world.</summary>
    void BroadcastProjectileSpawned(ProjectileSpawnedPayload payload);

    /// <summary>Host: announces a detonation and its resolved damage.</summary>
    void BroadcastProjectileDetonated(ProjectileDetonatedPayload payload);

    /// <summary>Host: announces a power-up appearing.</summary>
    void BroadcastPowerUpSpawned(PowerUpSpawnedPayload payload);

    /// <summary>Host: announces a power-up being collected.</summary>
    void BroadcastPowerUpCollected(PowerUpCollectedPayload payload);

    /// <summary>Host: announces a ship respawning.</summary>
    void BroadcastShipSpawned(ShipSpawnedPayload payload);

    /// <summary>Host: announces a ship being destroyed.</summary>
    void BroadcastShipDestroyed(ShipDestroyedPayload payload);

    /// <summary>Host: announces an asteroid breaking apart, with every fragment.</summary>
    void BroadcastAsteroidSplit(AsteroidSplitPayload payload);

    /// <summary>Host: announces a one-shot effect so clients play it too.</summary>
    void BroadcastGameplayEvent(GameplayEventType eventType, Vector2 position);

    /// <summary>
    /// Client: mirrors this frame's local input to the host, which owns the ship.
    /// </summary>
    /// <remarks>
    /// The outbound half of <see cref="ShipInputReceived"/>. The interface carried only
    /// the inbound half for a long time, so the one implementation that could send input
    /// had no caller that could reach it: a client's ship was simulated from its own
    /// prediction locally and stood still on the host and on every other client. Ignored
    /// by an authority, which reads its devices directly.
    /// </remarks>
    /// <param name="sequence">
    /// Monotonic per-client counter. Input travels unreliably, so the host uses this to
    /// drop packets that arrive out of order rather than acting on a stale frame.
    /// </param>
    void SendShipInput(Vector2 movement, Vector2 fire, bool deployMine, int sequence);

    // --- Inbound ------------------------------------------------------------

    /// <summary>Client: the host changed the match state.</summary>
    event Action<MatchState>? MatchStateChanged;

    /// <summary>Client: the host published a new countdown value.</summary>
    event Action<int>? CountdownChanged;

    /// <summary>Client: the host published the match clock.</summary>
    event Action<float>? MatchClockReceived;

    /// <summary>Client: the host announced the final result.</summary>
    event Action<MatchResult>? MatchCompletedReceived;

    /// <summary>Client: the host announced the world layout.</summary>
    event Action<MatchCreatedPayload>? MatchCreated;

    /// <summary>Client: the host announced the final pre-match positions.</summary>
    event Action<MatchStartingPayload>? MatchStarting;

    /// <summary>Client: an authoritative snapshot arrived.</summary>
    event Action<WorldSnapshot>? WorldSnapshotReceived;

    /// <summary>Client: a projectile was spawned by the host.</summary>
    event Action<ProjectileSpawnedPayload>? ProjectileSpawnedReceived;

    /// <summary>Client: a projectile detonated on the host.</summary>
    event Action<ProjectileDetonatedPayload>? ProjectileDetonatedReceived;

    /// <summary>Client: a power-up appeared.</summary>
    event Action<PowerUpSpawnedPayload>? PowerUpSpawnedReceived;

    /// <summary>Client: a power-up was collected.</summary>
    event Action<PowerUpCollectedPayload>? PowerUpCollectedReceived;

    /// <summary>Client: a ship respawned.</summary>
    event Action<ShipSpawnedPayload>? ShipSpawnedReceived;

    /// <summary>Client: a ship was destroyed.</summary>
    event Action<ShipDestroyedPayload>? ShipDestroyedReceived;

    /// <summary>Client: an asteroid broke apart on the host.</summary>
    event Action<AsteroidSplitPayload>? AsteroidSplitReceived;

    /// <summary>Client: a one-shot effect fired on the host.</summary>
    event Action<GameplayEventType, Vector2>? GameplayEventReceived;

    /// <summary>
    /// Host: a client sent its input for this frame - peer id, movement, fire direction,
    /// mine-deploy flag and the client's input sequence number.
    /// </summary>
    event Action<int, Vector2, Vector2, bool, int>? ShipInputReceived;

    /// <summary>A peer disconnected.</summary>
    event Action<int>? PlayerLeft;

    /// <summary>
    /// Raised whenever the roster gains, loses or updates a row.
    /// </summary>
    /// <remarks>
    /// Was concrete-only on <see cref="PartyMatchNetwork"/> until this port's Phase 6,
    /// which forced <c>LobbyScreen</c> to keep a second, downcast field purely to reach
    /// it. Lifted here so any <see cref="IMatchNetwork"/> can report a roster change
    /// through the one reference a caller already holds; <see cref="OfflineMatchNetwork"/>
    /// declares it but never raises it; there is exactly one row and it never changes.
    /// </remarks>
    event Action? RosterChanged;

    /// <summary>
    /// Raised when the underlying connection is lost or fails outright - the transport
    /// going away underneath the match, not a graceful <see cref="MatchResult"/>.
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="NetRumble.Platform.IPartyService.NetworkDestroyed"/> onto the
    /// match layer for the same reason as <see cref="RosterChanged"/>: a caller holding
    /// only an <see cref="IMatchNetwork"/> should not need the concrete
    /// <see cref="PartyMatchNetwork"/> just to hear about a dropped connection. Never
    /// raised by <see cref="OfflineMatchNetwork"/>, which has no connection to lose.
    /// </remarks>
    event Action<PlatformResult>? ConnectionLost;

    /// <summary>
    /// Publishes this peer's own ready state: the host broadcasts its roster row to
    /// every client, a client sends <c>SubmitReady</c> to the host. Reproduces
    /// <c>NetManager</c>'s single ready-toggle entry point, which did not care whether
    /// the caller was hosting or joined.
    /// </summary>
    void PublishLocalReady(bool ready);

    /// <summary>
    /// Publishes this peer's own ship colour and style: the host broadcasts its roster
    /// row to every client, a client sends <c>SubmitAppearance</c> to the host.
    /// </summary>
    void PublishLocalAppearance(int colorId, int styleId);
}
