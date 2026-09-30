using System.Numerics;

namespace NetRumble.Core.Net;

/// <summary>
/// One entity's replicated state inside a world snapshot, replacing the per-object
/// <c>Dictionary</c> the GDScript built in <c>_snapshot_entry</c>.
/// </summary>
/// <remarks>
/// <see cref="Health"/> and <see cref="Shield"/> only carry meaning for ships. Damage is
/// resolved by the host for every ship, the local one included, so the receiver takes
/// them verbatim rather than predicting them.
/// </remarks>
public readonly record struct SnapshotEntry(
    int Id,
    Vector2 Position,
    Vector2 Velocity,
    float Rotation,
    float Health,
    float Shield);

/// <summary>
/// A periodic authoritative update of every ship and asteroid.
/// </summary>
/// <remarks>
/// <see cref="Frame"/> is a monotonically increasing sequence number. Snapshots travel on
/// an unreliable channel and can arrive out of order, so the receiver discards anything
/// not newer than the last one it applied - applying a stale snapshot would visibly drag
/// every object backwards.
/// </remarks>
public sealed record WorldSnapshot(int Frame, IReadOnlyList<SnapshotEntry> Objects);

/// <summary>An asteroid's construction data, sent once when the world is announced.</summary>
public readonly record struct AsteroidSpawn(
    int Id,
    AsteroidSize Size,
    int Variation,
    Vector2 Position,
    Vector2 Velocity,
    float Rotation);

/// <summary>A ship's construction data, sent once when the world is announced.</summary>
/// <remarks>
/// Carries no PlayFab entity id (XR-014). One used to sit between the peer id and the
/// colour, was copied onto <c>Ship</c>, and was read by nothing at all.
/// </remarks>
public readonly record struct ShipSpawn(
    int Id,
    int PeerId,
    int ColorId,
    int StyleId,
    Vector2 Position,
    float Rotation);

/// <summary>A power-up's construction data, sent once when the world is announced.</summary>
public readonly record struct PowerUpSpawn(int Id, PowerUpType PowerUpType);

/// <summary>
/// The complete world layout, broadcast exactly once so every peer builds the same
/// entities with the same ids.
/// </summary>
public sealed record MatchCreatedPayload(
    int Width,
    int Height,
    IReadOnlyList<AsteroidSpawn> Asteroids,
    IReadOnlyList<ShipSpawn> Ships,
    IReadOnlyList<PowerUpSpawn> PowerUps);

/// <summary>One object's position and velocity at the moment the match goes live.</summary>
public readonly record struct ResetEntry(
    int Id,
    Vector2 Position,
    Vector2 Velocity,
    float Rotation);

/// <summary>
/// The final pre-match layout. The host picks every spawn point once and hands the
/// finished arrangement down, so the countdown runs on a layout every peer agrees on.
/// </summary>
public sealed record MatchStartingPayload(IReadOnlyList<ResetEntry> Resets);

/// <summary>A projectile coming into existence on the authority.</summary>
/// <param name="Spec">
/// The weapon-derived modifiers the authority built this shot with. Sent per shot so a
/// client never has to consult its own weapon table, which keeps the two sides in step
/// even when their tables have been retuned independently.
/// </param>
public readonly record struct ProjectileSpawnedPayload(
    int Id,
    ProjectileType ProjectileType,
    int OwnerId,
    Vector2 Position,
    Vector2 Velocity,
    float Rotation,
    Tuning.ShotSpec Spec);

/// <summary>
/// A projectile detonating, carrying the authoritative post-damage state of everything
/// caught in the blast.
/// </summary>
/// <remarks>
/// The <em>results</em> are replicated rather than the inputs, because chained mine
/// detonations are order-dependent and re-running the damage per peer would drift.
/// </remarks>
public sealed record ProjectileDetonatedPayload(
    int Id,
    Vector2 Position,
    IReadOnlyList<Objects.DetonationHit> Hits);

/// <summary>A power-up appearing at a position chosen by the host.</summary>
/// <remarks>
/// The type travels with the spawn because the bodies are pooled and generic: the same
/// body is a different pickup each time it is used, so a client that only learned the
/// type at world-build time would draw and describe the wrong one.
/// </remarks>
public readonly record struct PowerUpSpawnedPayload(int Id, PowerUpType PowerUpType, Vector2 Position);

/// <summary>
/// A power-up being picked up, and what it granted.
/// </summary>
/// <param name="PowerUp">
/// Which pickup was taken. The <i>pickup</i> rather than the weapon, because a pickup can
/// now grant a timed buff or an instant restore instead, and the collector's resulting
/// state is derived from this by every peer.
/// </param>
public readonly record struct PowerUpCollectedPayload(
    int Id,
    int CollectorId,
    PowerUpType PowerUp);

/// <summary>A ship respawning at a host-chosen point.</summary>
public readonly record struct ShipSpawnedPayload(int ShipId, int PeerId, Vector2 Position);

/// <summary>A ship being destroyed.</summary>
public readonly record struct ShipDestroyedPayload(int ShipId, int PeerId);

/// <summary>
/// One fragment thrown off by a splitting asteroid, rolled entirely on the authority.
/// </summary>
/// <remarks>
/// Everything random about a split - the id, the tier's texture variation, the launch
/// angle and the speed - is decided once by the host and shipped verbatim rather than
/// re-rolled per peer, so a split looks identical on every machine.
/// </remarks>
public readonly record struct AsteroidFragment(
    int Id,
    AsteroidSize Size,
    int Variation,
    Vector2 Position,
    Vector2 Velocity,
    float Rotation);

/// <summary>An asteroid breaking apart, and everything it broke into.</summary>
/// <param name="PeerId">
/// Whose shot broke the rock, or -1 for nobody. Only the authority can work this out,
/// and the player it credits is usually on another machine, so the answer travels with
/// the split rather than being recomputed from state clients do not have.
/// </param>
public sealed record AsteroidSplitPayload(
    int Id,
    Vector2 Position,
    int PeerId,
    IReadOnlyList<AsteroidFragment> Fragments);
