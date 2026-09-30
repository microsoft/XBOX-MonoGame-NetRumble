namespace NetRumble.Core;

/// <summary>
/// Shared enumerations, ported 1:1 from <c>scripts/gameplay/nr_types.gd</c>.
/// </summary>
/// <remarks>
/// <para>
/// Values are kept identical to the GDScript (and therefore to the original C++
/// sample) because several of them travel over the wire. Changing a member's ordinal
/// is a protocol break.
/// </para>
/// <para>
/// Original C++ source mapping, preserved from the Godot port so the three code bases
/// stay greppable against each other:
/// </para>
/// <code>
/// Game/Gameplay/GameObject/GameObject.h  -> GameObjectType
/// Game/Gameplay/WeaponType.h             -> WeaponType
/// Game/Gameplay/GameObject/PowerUp.h     -> PowerUpType
/// Game/Gameplay/GameObject/Projectile.h  -> ProjectileType
/// Game/Gameplay/GameObject/Asteroid.h    -> AsteroidSize
/// Game/Gameplay/MatchState.h             -> MatchState
/// Game/Gameplay/GameplayEvent.h          -> GameplayEventType
/// Game/Assets/ShipType.h                 -> ShipType
/// </code>
/// </remarks>
public enum GameObjectType
{
    Asteroid = 0,
    PowerUp = 1,
    Projectile = 2,
    Ship = 3,
    Unknown = 4,
}

/// <summary>
/// Every weapon a ship can carry.
/// </summary>
/// <remarks>
/// The list is deliberately flat rather than a "family plus modifier" scheme: every
/// weapon is one entry in <see cref="Tuning.WeaponLibrary"/> describing how many shots
/// it fires, how they are spread, and what each one does. Adding a weapon means adding
/// an enumerator and a table row, and nothing else in the simulation has to know about
/// it. The first four establish the baseline feel and are pinned at the head because
/// their ordinals ride the wire.
/// </remarks>
public enum WeaponType
{
    Laser = 0,
    DoubleLaser = 1,
    TripleLaser = 2,
    Rocket = 3,
    QuadLaser = 4,
    SpreadShot = 5,
    ScatterGun = 6,
    BeamLance = 7,
    PulseBeam = 8,
    HomingMissile = 9,
    SwarmMissiles = 10,
    PlasmaCannon = 11,
    Railgun = 12,
    ShotgunBlast = 13,
    FlakBurst = 14,
    RicochetGun = 15,
    ChainLightning = 16,
    Vulcan = 17,
    NovaBurst = 18,
    SeekerMines = 19,
}

/// <summary>
/// Everything a pickup can be. Weapon pickups grant a <see cref="WeaponType"/>, buff
/// pickups grant a timed <see cref="BuffType"/>, and restore pickups top the collector
/// back up on the spot.
/// </summary>
/// <remarks>
/// The first three enumerators - <see cref="DoubleLaser"/>, <see cref="TripleLaser"/>,
/// <see cref="Rocket"/> - are pinned at the head so their ordinals, which ride the
/// network as ints, remain stable. All other pickup types follow in order of addition.
/// </remarks>
public enum PowerUpType
{
    DoubleLaser = 0,
    TripleLaser = 1,
    Rocket = 2,
    QuadLaser = 3,
    SpreadShot = 4,
    ScatterGun = 5,
    BeamLance = 6,
    PulseBeam = 7,
    HomingMissile = 8,
    SwarmMissiles = 9,
    PlasmaCannon = 10,
    Railgun = 11,
    ShotgunBlast = 12,
    FlakBurst = 13,
    RicochetGun = 14,
    ChainLightning = 15,
    Vulcan = 16,
    NovaBurst = 17,
    SeekerMines = 18,
    LaserRefit = 19,
    BuffRapidFire = 20,
    BuffAfterburner = 21,
    BuffCloak = 22,
    BuffDoubleDamage = 23,
    BuffOvershield = 24,
    BuffRegeneration = 25,
    BuffQuickCharge = 26,
    BuffMultiShot = 27,
    BuffRicochet = 28,
    BuffVampiric = 29,
    RestoreShield = 30,
    RestoreHull = 31,
}

/// <summary>
/// What collecting a pickup actually does. Stored on the definition rather than
/// inferred from the enumerator, so the three groups can be reordered freely.
/// </summary>
public enum PickupKind
{
    Weapon = 0,
    Buff = 1,
    Restore = 2,
}

/// <summary>
/// Timed ship modifiers. Values are used as keys on <c>Ship</c> and ride the network
/// as ints, so they are ordered and contiguous.
/// </summary>
public enum BuffType
{
    RapidFire = 0,
    Afterburner = 1,
    Cloak = 2,
    DoubleDamage = 3,
    Overshield = 4,
    Regeneration = 5,
    QuickCharge = 6,
    MultiShot = 7,
    Ricochet = 8,
    Vampiric = 9,
}

public enum ProjectileType
{
    Laser = 0,
    Mine = 1,
    Rocket = 2,
}

/// <summary>
/// Asteroid size tiers, smallest first.
/// </summary>
/// <remarks>
/// The C++ original had three (<c>Asteroid.h</c>: Small/Medium/Large); the Godot port
/// has five so that a large rock breaks down through several visibly different
/// generations before the last fragment is destroyed outright. Values are ordered and
/// contiguous, and code relies on that: a split spawns the tier at <c>size - 1</c>, and
/// <see cref="Tiny"/> is the terminal tier.
/// </remarks>
public enum AsteroidSize
{
    Tiny = 0,
    Small = 1,
    Medium = 2,
    Large = 3,
    Huge = 4,
}

public enum GameplayEventType
{
    LaserFired = 0,
    LaserImpact = 1,
    ShipSpawned = 2,
    ShipDestroyed = 3,
    MineDetonated = 4,
    RocketFired = 5,
    RocketTrail = 6,
    RocketDetonated = 7,
    PowerUpSpawned = 8,
    PowerUpCollected = 9,
    AsteroidImpact = 10,
}

public enum ShipType
{
    Ship0 = 0,
    Ship1 = 1,
    Ship2 = 2,
    Ship3 = 3,
}

/// <summary>
/// Match phase, as bit flags matching <c>MatchState.h</c> exactly.
/// </summary>
/// <remarks>
/// <see cref="Waiting"/> and <see cref="Playable"/> are <b>composite masks</b>, not
/// states. Always test membership with <see cref="MatchStateExtensions.HasMatchState"/>
/// rather than <c>==</c>.
/// </remarks>
[Flags]
public enum MatchState
{
    /// <summary>Zero, so it cannot be tested with a bitwise AND. See the extension.</summary>
    Loading = 0,

    PlayersJoining = 1,

    /// <summary>
    /// Kept for the <see cref="Waiting"/> mask and for protocol parity with
    /// <c>MatchState.h</c>. The match flow goes straight from
    /// <see cref="PlayersJoining"/> to <see cref="Starting"/> so players are placed
    /// only once.
    /// </summary>
    WarmingUp = 2,

    Starting = 8,
    Running = 16,
    MatchComplete = 32,

    /// <summary>Mask: <see cref="PlayersJoining"/> | <see cref="WarmingUp"/>.</summary>
    Waiting = 3,

    /// <summary>
    /// Mask: <see cref="PlayersJoining"/> | <see cref="WarmingUp"/> | <see cref="Running"/>.
    /// </summary>
    /// <remarks>
    /// Note that <see cref="Starting"/> (8) is deliberately <b>not</b> part of this
    /// mask - 19 is 16|2|1. The countdown phase is not "playable", which is what stops
    /// input being accepted while players are still being placed. Verified against
    /// <c>nr_types.gd</c>; do not "fix" this to include Starting.
    /// </remarks>
    Playable = 19,
}

public static class MatchStateExtensions
{
    /// <summary>
    /// Tests membership, handling the fact that <see cref="MatchState.Loading"/> is
    /// zero and therefore matches nothing under a bitwise AND.
    /// </summary>
    /// <remarks>Ported from <c>NRTypes.has_match_state</c>.</remarks>
    public static bool HasMatchState(this MatchState value, MatchState mask)
        => mask == MatchState.Loading
            ? value == MatchState.Loading
            : (value & mask) != 0;
}
