using System.Numerics;
using NetRumble.Core.Ai;
using NetRumble.Core.Net;
using NetRumble.Core.Objects;
using NetRumble.Core.Simulation;
using NetRumble.Core.Tuning;
using NetRumble.Platform.Diagnostics;

namespace NetRumble.Core;

/// <summary>
/// The simulation, ported from <c>scripts/gameplay/world.gd</c> (originally
/// <c>Game/Gameplay/World.cpp</c>).
/// </summary>
/// <remarks>
/// <para>
/// Owns every entity, the object pools, spawn selection, explosion damage and power-up
/// rotation. Match flow (scoring, respawn queues, phase transitions) belongs to the match
/// director, and the world only reports what happened.
/// </para>
/// <para>
/// <b>Scope.</b> The replication half - snapshot broadcast, the <c>_on_*_received</c>
/// handlers and local-ship reconciliation - lives in the <c>World.Replication.cs</c>
/// partial, so this file stays readable as pure simulation.
/// </para>
/// </remarks>
public sealed partial class World
{
    /// <summary>
    /// Dead zone around the local ship inside which the cursor produces no aim direction.
    /// </summary>
    /// <remarks>
    /// Without it, a cursor resting on top of the ship normalises a near-zero vector and
    /// the shots spray in whatever direction the last sub-pixel of jitter pointed.
    /// </remarks>
    public const float MouseAimDeadZone = 12.0f;

    private readonly Dictionary<int, GameObject> _gameObjects = [];
    private readonly Dictionary<int, Ship> _ships = [];
    private readonly Dictionary<int, Asteroid> _asteroids = [];
    /// <summary>
    /// The pickup pool, keyed by body id. The bodies are generic - which of the
    /// thirty-two pickups each represents is decided at spawn time - so the pool is sized
    /// by how many may be live at once, not by how many kinds there are.
    /// </summary>
    private readonly Dictionary<int, PowerUp> _powerUps = [];

    /// <summary>Pool bodies currently on the field, keyed by id.</summary>
    private readonly Dictionary<int, PowerUp> _activePowerUps = [];

    /// <summary>Pool bodies available to be spawned.</summary>
    private readonly List<PowerUp> _freePowerUps = [];
    private readonly Dictionary<int, Projectile> _activeProjectiles = [];
    private readonly Dictionary<int, Ship> _shipsByPeer = [];
    private readonly Dictionary<ProjectileType, Queue<Projectile>> _projectileCaches = new()
    {
        [ProjectileType.Laser] = new Queue<Projectile>(),
        [ProjectileType.Mine] = new Queue<Projectile>(),
        [ProjectileType.Rocket] = new Queue<Projectile>(),
    };

    private readonly List<MineProjectile> _pendingMineDetonations = [];
    private readonly HashSet<int> _announcedDeaths = [];
    private readonly List<GameObject> _stepBuffer = [];
    private readonly List<Asteroid> _splitBuffer = [];

    /// <summary>
    /// Ship unique id -> AI driver, for every NPC opponent.
    /// </summary>
    /// <remarks>
    /// Authority-side only, and in practice only ever populated offline: bots are a
    /// practice-mode feature and are never introduced into a networked session.
    /// </remarks>
    private readonly Dictionary<int, BotController> _botControllers = [];

    private readonly Random _random;

    private List<PlayerState> _players = [];
    private CircleCollisionWorld? _collision;
    private float _powerUpSpawnTimer;
    private bool _processingMineDetonations;
    private bool _simulationRunning;
    private int _snapshotFrame;
    private int _localInputSequence;

    /// <summary>
    /// Transport, attached by the match director. Null until then, and null forever in a
    /// test that drives the world directly, so every use is guarded.
    /// </summary>
    private IMatchNetwork? _network;

    /// <summary>
    /// Highest snapshot frame applied on a client. Snapshots ride an unreliable channel
    /// and can arrive out of order; applying a stale one drags every object backwards.
    /// </summary>
    private int _lastWorldDataFrame;

    public World(Random? random = null) => _random = random ?? new Random();

    /// <summary>Raised for effects that should play locally and, on the authority, replicate.</summary>
    public event Action<GameplayEventType, Vector2>? GameplayEvent;

    /// <summary>Raised when the local player's ship changes.</summary>
    public event Action<Ship?>? LocalShipChanged;

    /// <summary>
    /// Raised when a ship's health reaches zero. The match director owns the response;
    /// the world only reports it.
    /// </summary>
    public event Action<Ship, int>? ShipDestroyed;

    /// <summary>Raised when a projectile spawns, for the replication layer.</summary>
    public event Action<Projectile>? ProjectileSpawned;

    /// <summary>Raised when a projectile detonates, for the replication layer.</summary>
    public event Action<Projectile, Vector2, IReadOnlyList<DetonationHit>>? ProjectileDetonated;

    /// <summary>Raised when a power-up spawns, for the replication layer.</summary>
    public event Action<PowerUp>? PowerUpSpawned;

    /// <summary>Raised when a power-up is collected, for the replication layer.</summary>
    public event Action<PowerUp, Ship, PowerUpType>? PowerUpCollected;

    /// <summary>
    /// Raised on <i>every</i> peer when the local player's ship fires its primary weapon.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from the replication events above, and deliberately scoped
    /// to the local ship. Projectile creation is authority-only, so a client never sees
    /// its own shot spawn locally - but it does run <c>Ship.ProcessControlsWeapon</c> for
    /// its predicted ship, which is the one moment every peer agrees "I just fired this
    /// weapon". The achievement layer needs exactly that and nothing else.
    /// </remarks>
    public event Action<WeaponType>? LocalWeaponFired;

    /// <summary>
    /// Raised on <i>every</i> peer when the local player's ship collects a power-up,
    /// carrying the pickup that was taken.
    /// </summary>
    /// <remarks>
    /// <see cref="PowerUpCollected"/> fires only on the authority, because that is where
    /// collection is decided; a client learns about its own pickup from the host's
    /// <c>PowerUpCollected</c> message instead. This event is raised down both paths, so
    /// the game layer has one subscription that works whether or not it is hosting.
    /// It carries the pickup rather than a weapon because a pickup may instead grant a
    /// timed buff or an instant restore (see <see cref="Tuning.PowerUpDefinition"/>).
    /// </remarks>
    public event Action<PowerUpType>? LocalPowerUpCollected;

    /// <summary>
    /// Raised on <i>every</i> peer when an asteroid the local player shot breaks apart.
    /// </summary>
    /// <remarks>
    /// Scoped to the local player for the same reason as the two events above: only the
    /// authority can attribute a broken rock, so the credited peer id travels with the
    /// split message and both the authority and the client raise this from the one place
    /// that applies it.
    /// </remarks>
    public event Action? LocalAsteroidDestroyed;

    public bool IsAuthority { get; private set; }

    public Ship? LocalShip { get; private set; }

    public int WorldWidth { get; private set; }

    public int WorldHeight { get; private set; }

    /// <summary>Every simulated entity, keyed by replication id.</summary>
    public IReadOnlyDictionary<int, GameObject> GameObjects => _gameObjects;

    public IReadOnlyDictionary<int, Ship> Ships => _ships;

    public IReadOnlyDictionary<int, Asteroid> Asteroids => _asteroids;

    public IReadOnlyDictionary<int, Projectile> ActiveProjectiles => _activeProjectiles;

    /// <summary>True once the world has been laid out and is safe to simulate.</summary>
    public bool IsBuilt => _collision is not null;

    public void Initialize(bool authority, IEnumerable<PlayerState> playerStates)
    {
        IsAuthority = authority;
        _players = [.. playerStates];

        CrashLog.Mark($"world: initialize authority={authority} players={_players.Count}");

        if (!IsAuthority)
        {
            return;
        }

        ServerInitialize();
        ResetPowerUpSpawnTimer();

        CrashLog.Mark($"world: built objects={_gameObjects.Count} ships={_ships.Count} asteroids={_asteroids.Count}");
    }

    /// <summary>
    /// Lays the world out once and activates the ships.
    /// </summary>
    /// <remarks>
    /// Called a single time per match, right before the start countdown, so nothing gets
    /// repositioned under the players again.
    /// </remarks>
    public void StartMatch()
    {
        if (!IsAuthority)
        {
            return;
        }

        Reset();

        foreach (var obj in _gameObjects.Values)
        {
            obj.Start();
        }

        CrashLog.Mark("world: start match, objects started");

        // The layout is announced exactly once, after Reset has chosen every spawn point,
        // so clients build their entities from a settled arrangement rather than one that
        // is about to move.
        _network?.BroadcastMatchCreated(BuildMatchCreatedPayload());
        _network?.BroadcastMatchStarting(BuildMatchStartingPayload());

        CrashLog.Mark("world: start match announced");

        if (LocalShip is not null)
        {
            LocalShipChanged?.Invoke(LocalShip);
        }
    }

    /// <summary>
    /// Repositions asteroids and ships to fresh spawn points, respawns the ships and
    /// recycles projectiles and power-ups.
    /// </summary>
    public void Reset()
    {
        if (!IsAuthority)
        {
            return;
        }

        // Everything is parked at the origin first so that spawn point selection, which
        // tests against every active body, cannot pick a point that is free only because
        // the body about to move there has not been relocated yet.
        foreach (var asteroid in _asteroids.Values)
        {
            asteroid.Teleport(Vector2.Zero);
        }

        foreach (var ship in _ships.Values)
        {
            ship.Teleport(Vector2.Zero);
        }

        foreach (var asteroid in _asteroids.Values)
        {
            asteroid.Teleport(FindSpawnPoint(asteroid.Radius));
        }

        foreach (var ship in _ships.Values)
        {
            ship.Teleport(FindSpawnPoint(ship.Radius));
            ship.Start();
            ship.IsActive = true;
        }

        ResetProjectileCache();
        ResetPowerUps();
        _announcedDeaths.Clear();
        _collision?.Reset();
    }

    /// <summary>
    /// Advances the whole simulation by one step. The match director calls this only while
    /// the simulation should be running, leaving it frozen otherwise.
    /// </summary>
    public void Tick(float delta)
    {
        // Before Simulate, so a bot's input is applied on the same step it was decided -
        // exactly like the local player's, which the game layer has just pushed through
        // SetLocalInput.
        TickBots(delta);

        Simulate(delta);

        if (!IsAuthority)
        {
            return;
        }

        TickPowerUps(delta);
        DetectDestroyedShips();
        DetectSplitAsteroids();
    }

    /// <summary>Applies this frame's local device state to the local ship.</summary>
    /// <remarks>
    /// The local ship's input object is driven directly, which is what gives local
    /// prediction; the network send only mirrors it to the host, which owns the ship and
    /// decides what actually happened.
    /// </remarks>
    public void SetLocalInput(Vector2 movement, Vector2 fire, bool deployMineHeld)
        => SetLocalInput(movement, fire, deployMineHeld, null);

    /// <summary>Applies this frame's local device state to the local ship.</summary>
    /// <param name="mouseWorldPosition">
    /// Where the cursor is in world space while the player is holding the mouse to aim,
    /// or <c>null</c> when they are not. The game layer resolves the cursor because only
    /// it can see the camera; the dead zone and normalisation stay here, with the rest of
    /// the input model.
    /// </param>
    public void SetLocalInput(
        Vector2 movement,
        Vector2 fire,
        bool deployMineHeld,
        Vector2? mouseWorldPosition)
    {
        if (LocalShip is null)
        {
            return;
        }

        LocalShip.ShipInput.ProcessLocalInput(
            movement, fire, deployMineHeld, MouseAimDirection(mouseWorldPosition));

        if (IsAuthority || _network is null)
        {
            return;
        }

        // The press edge, not the held state: ProcessLocalInput above resolves the edge,
        // and the host ORs what arrives into its own DeployMinePressed, so sending "held"
        // would lay a mine every frame the key was down.
        _network.SendShipInput(
            LocalShip.ShipInput.MovementDirection,
            LocalShip.ShipInput.FireDirection,
            LocalShip.ShipInput.DeployMinePressed,
            ++_localInputSequence);
    }

    /// <summary>
    /// Aim direction from the local ship to the cursor, or zero when not mouse-aiming.
    /// </summary>
    /// <remarks>
    /// Returned as a unit vector on purpose: <c>Ship.ProcessControlsWeapon</c> compares
    /// the squared magnitude against its fire threshold to decide whether to shoot, so a
    /// raw ship-to-cursor vector would work at range but silently refuse to fire whenever
    /// the cursor sat close to the ship.
    /// </remarks>
    private Vector2 MouseAimDirection(Vector2? mouseWorldPosition)
    {
        if (LocalShip is null || mouseWorldPosition is not { } cursor)
        {
            return Vector2.Zero;
        }

        var toCursor = cursor - LocalShip.Position;

        return toCursor.LengthSquared() < MouseAimDeadZone * MouseAimDeadZone
            ? Vector2.Zero
            : Vector2.Normalize(toCursor);
    }

    public GameObject? GetGameObject(int uniqueId)
        => _gameObjects.TryGetValue(uniqueId, out var obj) ? obj : null;

    public Ship? GetShipById(int shipId) => _ships.TryGetValue(shipId, out var ship) ? ship : null;

    public Ship? GetShipFor(int peerId) => _shipsByPeer.TryGetValue(peerId, out var ship) ? ship : null;

    public void RemoveShipFor(int peerId)
    {
        if (!_shipsByPeer.TryGetValue(peerId, out var ship))
        {
            return;
        }

        var owned = _activeProjectiles
            .Where(pair => pair.Value.OwnerId == ship.UniqueId)
            .Select(pair => pair.Key)
            .ToList();

        foreach (var id in owned)
        {
            DestroyGameObjectById(id);
        }

        ship.IsActive = false;
        ship.ShipInput.Reset();
        ship.ClearCollisionExceptions();

        if (ReferenceEquals(LocalShip, ship))
        {
            LocalShip = null;
            LocalShipChanged?.Invoke(null);
        }

        _shipsByPeer.Remove(peerId);
        _ships.Remove(ship.UniqueId);
        _gameObjects.Remove(ship.UniqueId);
        _botControllers.Remove(ship.UniqueId);
    }

    public void ResetProjectileCache()
    {
        foreach (var projectile in _activeProjectiles.Values)
        {
            projectile.IsActive = false;
            projectile.ClearCollisionExceptions();
            _projectileCaches[projectile.ProjectileType].Enqueue(projectile);
        }

        _activeProjectiles.Clear();
        _pendingMineDetonations.Clear();
        _processingMineDetonations = false;
    }

    /// <summary>Freezes or thaws every body, without deactivating them.</summary>
    public void SetSimulationRunning(bool running)
    {
        // Remembered so that an asteroid fragment spawned mid-match starts in the same
        // state as the rock it came from, rather than defaulting to frozen.
        _simulationRunning = running;

        foreach (var obj in _gameObjects.Values)
        {
            obj.SimulationRunning = running;
            obj.OnSimulationRunningChanged(running);
        }
    }

    /// <summary>
    /// Picks a random unoccupied point inside the barrier.
    /// </summary>
    /// <remarks>
    /// Deliberately a manual overlap test rather than a physics query. It runs during
    /// world construction, before the solver has ever seen these bodies, and it is
    /// deterministic given the same seed, which matters for host and client agreement.
    /// </remarks>
    public Vector2 FindSpawnPoint(float radius)
    {
        if (_collision is null)
        {
            return new Vector2(WorldWidth, WorldHeight) * 0.5f;
        }

        var paddedRadius = radius + NRConst.SpawnPointPadding;
        var spawnPoint = RandomPoint(radius);

        for (var attempt = 1; attempt <= NRConst.FindSpawnPointAttempts; attempt++)
        {
            var valid = true;

            foreach (var other in _gameObjects.Values)
            {
                if (!other.IsActive)
                {
                    continue;
                }

                if (CircleIntersect(spawnPoint, paddedRadius, other.Position, other.Radius))
                {
                    valid = false;
                    break;
                }
            }

            if (valid)
            {
                break;
            }

            spawnPoint = RandomPoint(radius);
        }

        return spawnPoint;
    }

    // --- Projectiles --------------------------------------------------------

    /// <summary>
    /// Fires one volley of <paramref name="weapon"/> from the given ship.
    /// </summary>
    /// <remarks>
    /// One generic routine for all twenty weapons. The arrangement of each volley is
    /// described by the weapon's <see cref="WeaponLibrary"/> entry and executed once here,
    /// rather than duplicating the offset and rotation logic per weapon type.
    /// </remarks>
    public void CreateProjectiles(WeaponType weapon, int shipOwnerId, Vector2 direction)
    {
        if (!IsAuthority)
        {
            return;
        }

        var owner = GetShipById(shipOwnerId);

        if (owner is null)
        {
            return;
        }

        var definition = WeaponLibrary.Get(weapon);
        var spec = definition.ToShotSpec();

        // Buffs are folded into the spec rather than applied to the projectile afterwards,
        // so they ride the spawn message and clients build identical shots.
        spec = spec with
        {
            DamageScale = spec.DamageScale * owner.DamageMultiplier(),
            Bounces = spec.Bounces + owner.ExtraBounces(),
        };

        var shotCount = Math.Max(definition.ShotCount + owner.ExtraShots(), 1);
        var spread = definition.Spread;

        if (shotCount > 1)
        {
            spread += owner.ExtraSpread();
        }

        var fullCircle = spread >= MathF.Tau - 0.001f;
        var perpendicular = new Vector2(-direction.Y, direction.X);
        var volley = new List<Projectile>(shotCount);

        for (var i = 0; i < shotCount; i++)
        {
            // `fraction` runs from -0.5 to +0.5 across the volley, so it drives both the
            // angular fan and the lateral spacing from one number and a single shot
            // naturally lands dead centre. A full-circle weapon divides by the shot count
            // instead, or its first and last shots would land on top of each other.
            var fraction = 0.0f;

            if (shotCount > 1)
            {
                fraction = fullCircle
                    ? ((float)i / shotCount) - 0.5f
                    : ((float)i / (shotCount - 1)) - 0.5f;
            }

            var angle = fraction * spread;

            if (definition.SpreadJitter > 0.0f)
            {
                angle += ((float)_random.NextDouble() * 2.0f - 1.0f) * definition.SpreadJitter;
            }

            var shotDirection = Rotate(direction, angle);
            var shot = ConsumeProjectile(definition.ProjectileType, owner, shotDirection, spec);

            if (shot is null)
            {
                continue;
            }

            var offset = direction * definition.MuzzleOffset;

            if (definition.LateralSpacing > 0.0f)
            {
                offset += perpendicular * (fraction * 2.0f * definition.LateralSpacing);
            }

            if (offset != Vector2.Zero)
            {
                shot.Teleport(shot.Position + offset);
            }

            volley.Add(shot);
        }

        if (volley.Count == 0)
        {
            return;
        }

        SpawnVolley(volley);
        owner.ConsumeWeaponAmmo();

        EmitGameplayEvent(
            definition.ProjectileType == ProjectileType.Rocket
                ? GameplayEventType.RocketFired
                : GameplayEventType.LaserFired,
            owner.Position);
    }

    /// <summary>
    /// Nearest ship other than the one that fired, ignoring the untargetable.
    /// </summary>
    /// <remarks>
    /// Used by the guided weapons; kept on the world because it is the only thing holding
    /// the ship table, and a linear scan over at most four ships is cheaper than any
    /// structure that would have to be maintained.
    /// </remarks>
    public Ship? FindNearestEnemyShip(Vector2 from, int excludeShipId)
    {
        Ship? best = null;
        var bestDistance = float.PositiveInfinity;

        foreach (var ship in Ships.Values)
        {
            if (!ship.IsActive || ship.UniqueId == excludeShipId || ship.IsUntargetable())
            {
                continue;
            }

            var distance = Vector2.DistanceSquared(from, ship.Position);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = ship;
            }
        }

        return best;
    }

    /// <summary>Credits a vampiric shooter with a share of the damage one of its shots dealt.</summary>
    public void CreditDamageDealt(int shooterId, float damage)
    {
        if (!IsAuthority)
        {
            return;
        }

        GetShipById(shooterId)?.CreditDamageDealt(damage);
    }

    private static Vector2 Rotate(Vector2 value, float radians)
    {
        var cosine = MathF.Cos(radians);
        var sine = MathF.Sin(radians);

        return new Vector2(
            (value.X * cosine) - (value.Y * sine),
            (value.X * sine) + (value.Y * cosine));
    }

    public bool CreateMine(int shipOwnerId, Vector2 direction)
    {
        if (!IsAuthority)
        {
            return false;
        }

        var owner = GetShipById(shipOwnerId);

        if (owner is null)
        {
            return false;
        }

        var mine = ConsumeProjectile(ProjectileType.Mine, owner, direction);

        if (mine is null)
        {
            return false;
        }

        var offset = owner.Radius + mine.Radius + NRConst.MineSpawnDistance;
        mine.Teleport(owner.Position + (direction * offset));
        NotifyProjectileSpawned(mine);
        return true;
    }

    public bool CreateRocket(int shipOwnerId, Vector2 direction)
    {
        if (!IsAuthority)
        {
            return false;
        }

        var owner = GetShipById(shipOwnerId);

        if (owner is null)
        {
            return false;
        }

        var rocket = ConsumeProjectile(ProjectileType.Rocket, owner, direction);

        if (rocket is null)
        {
            return false;
        }

        NotifyProjectileSpawned(rocket);
        EmitGameplayEvent(GameplayEventType.RocketFired, rocket.Position);
        return true;
    }

    /// <summary>
    /// Draws a projectile from its pool and applies the shot spec that makes it this
    /// weapon's shot rather than a plain body.
    /// </summary>
    public Projectile? ConsumeProjectile(
        ProjectileType projectileType,
        Ship? ship,
        Vector2 direction,
        ShotSpec? spec = null)
    {
        if (ship is null || !_projectileCaches.TryGetValue(projectileType, out var cache) || cache.Count == 0)
        {
            return null;
        }

        var projectile = cache.Dequeue();
        projectile.ClearCollisionExceptions();

        // Before SetOwnerAndDirection: the spec's speed scale decides the muzzle velocity
        // that call applies, so applying it afterwards would launch every shot at the
        // body's unscaled speed for one frame.
        projectile.ApplyShotSpec(spec ?? ShotSpec.Default);

        projectile.SetOwnerAndDirection(ship, direction);
        projectile.IsActive = true;
        projectile.Start();
        _activeProjectiles[projectile.UniqueId] = projectile;
        return projectile;
    }

    public void NotifyProjectileSpawned(Projectile? projectile)
    {
        if (!IsAuthority || projectile is null)
        {
            return;
        }

        ProjectileSpawned?.Invoke(projectile);

        _network?.BroadcastProjectileSpawned(new ProjectileSpawnedPayload(
            projectile.UniqueId,
            projectile.ProjectileType,
            projectile.OwnerId,
            projectile.Position,
            projectile.Velocity,
            projectile.Rotation,
            projectile.Spec));
    }

    public void NotifyProjectileDetonated(
        Projectile? projectile,
        Vector2 position,
        IReadOnlyList<DetonationHit> hits)
    {
        if (!IsAuthority || projectile is null)
        {
            return;
        }

        ProjectileDetonated?.Invoke(projectile, position, hits);

        _network?.BroadcastProjectileDetonated(
            new ProjectileDetonatedPayload(projectile.UniqueId, position, hits));

        var effect = projectile.ProjectileType switch
        {
            ProjectileType.Laser => GameplayEventType.LaserImpact,
            ProjectileType.Rocket => GameplayEventType.RocketDetonated,
            ProjectileType.Mine => GameplayEventType.MineDetonated,
            _ => (GameplayEventType?)null,
        };

        if (effect.HasValue)
        {
            EmitGameplayEvent(effect.Value, position);
        }
    }

    public void QueueMineDetonation(MineProjectile? mine)
    {
        if (IsAuthority && mine is not null && mine.TryQueueDetonation())
        {
            _pendingMineDetonations.Add(mine);
        }
    }

    /// <summary>
    /// Applies area damage around a detonation.
    /// </summary>
    /// <remarks>
    /// Targets are collected, then sorted by id, then damaged. The sort is what keeps the
    /// host and every client in agreement: a chain of mine detonations is order-dependent,
    /// and dictionary iteration order is not a contract.
    /// </remarks>
    public List<DetonationHit> ApplyExplosionDamage(
        Projectile? source,
        Vector2 position,
        float damageAmount,
        float damageRadius,
        bool canDamageOwner,
        GameObject? excludedTarget)
    {
        var results = new List<DetonationHit>();

        if (source is null || damageAmount <= 0.0f || damageRadius <= 0.0f)
        {
            return results;
        }

        var damageRadiusSquared = damageRadius * damageRadius;
        var hits = new List<(GameObject Target, float Damage, Vector2 Impulse)>();

        foreach (var gameObject in _gameObjects.Values)
        {
            if (!gameObject.IsActive
                || ReferenceEquals(gameObject, excludedTarget)
                || gameObject.ObjectType == GameObjectType.PowerUp
                || gameObject.Health <= 0.0f)
            {
                continue;
            }

            if (!canDamageOwner && ReferenceEquals(gameObject, source))
            {
                continue;
            }

            var direction = gameObject.Position - position;
            var distanceSquared = direction.LengthSquared();

            if (distanceSquared > damageRadiusSquared)
            {
                continue;
            }

            var distance = MathF.Sqrt(distanceSquared);
            var adjustedDamage = damageAmount * (damageRadius - distance) / damageRadius;

            if (adjustedDamage <= 0.0f)
            {
                continue;
            }

            var impulse = distanceSquared > 0.0f
                ? Vector2.Normalize(direction) * adjustedDamage * NRConst.SpeedDamageRatio
                : Vector2.Zero;

            hits.Add((gameObject, adjustedDamage, impulse));
        }

        hits.Sort(static (a, b) => a.Target.UniqueId.CompareTo(b.Target.UniqueId));

        foreach (var (target, damage, impulse) in hits)
        {
            // Re-checked because an earlier hit in this same loop may have destroyed this
            // target through a chained mine detonation.
            if (!target.IsActive || target.Health <= 0.0f)
            {
                continue;
            }

            target.TakeDamage(source, damage);

            if (target.IsActive)
            {
                target.Velocity += impulse;
            }
        }

        foreach (var (target, _, _) in hits)
        {
            results.Add(CreateDetonationHit(target));
        }

        return results;
    }

    public DetonationHit CreateDetonationHit(GameObject obj) => new()
    {
        Id = obj.UniqueId,
        Health = obj.Health,

        // -1 marks "not a ship", so a client applying this cannot mistake a default of
        // zero for a fully depleted shield.
        Shield = obj is Ship ship ? ship.Shield : -1.0f,
        Position = obj.Position,
        Velocity = obj.Velocity,
        IsActive = obj.IsActive,
    };

    public void ApplyProjectileDetonationResults(IReadOnlyList<DetonationHit> hits)
    {
        foreach (var hit in hits)
        {
            var obj = GetGameObject(hit.Id);

            if (obj is null)
            {
                continue;
            }

            obj.Teleport(hit.Position);
            obj.Velocity = hit.Velocity;

            if (obj is Ship ship && hit.Shield >= 0.0f)
            {
                ship.ApplyAuthoritativeDamageState(hit.Health, hit.Shield);
            }
            else
            {
                obj.Health = hit.Health;
            }

            if (!hit.IsActive && obj.IsActive)
            {
                DestroyGameObjectById(hit.Id);
            }
            else
            {
                obj.IsActive = hit.IsActive;
            }
        }
    }

    // --- Power-ups ----------------------------------------------------------

    /// <summary>
    /// Normalised (0..1) Power-Up Frequency, driving both the drop interval and how many
    /// pickups may be live at once.
    /// </summary>
    /// <remarks>
    /// A host-side setting: the authority is the only peer that spawns, so a client
    /// disagreeing about it changes nothing. Clients learn each drop from the wire.
    /// </remarks>
    public float PowerUpFrequency { get; set; } = NRConst.DefaultPowerUpFrequency;

    /// <summary>Pickups currently uncollected on the field.</summary>
    public int ActivePowerUpCount => _activePowerUps.Count;

    /// <summary>The pickups currently on the field, for the renderer and for tests.</summary>
    public IReadOnlyCollection<PowerUp> ActivePowerUps => _activePowerUps.Values;

    /// <summary>
    /// Tops the field back up. Several drops sit out there simultaneously: with
    /// thirty-two pickup types to find and ammo-limited weapons that run out in seconds,
    /// a single-slot world would mean most of a match spent shooting the default laser.
    /// </summary>
    public void TickPowerUps(float delta)
    {
        if (!IsAuthority || _activePowerUps.Count >= NRConst.MaxActivePowerUps(PowerUpFrequency))
        {
            return;
        }

        _powerUpSpawnTimer = MathF.Max(0.0f, _powerUpSpawnTimer - delta);

        if (_powerUpSpawnTimer > 0.0f)
        {
            return;
        }

        // Weighted across all thirty-two pickups, so the drop table is balanced in one
        // place rather than by a roll open-coded here.
        SpawnPowerUp(PickupLibrary.RandomType(_random));
        ResetPowerUpSpawnTimer();
    }

    /// <summary>
    /// Applies a pickup to the ship that took it.
    /// </summary>
    /// <remarks>
    /// Shared by the authority's collection path and the client's inbound one, so the two
    /// cannot disagree about what a pickup does. Weapon grants go through the networked
    /// setter on clients, which will not refill a count that is mid-burst.
    /// </remarks>
    internal static void ApplyPickup(PowerUpDefinition definition, Ship collector, bool isAuthority)
    {
        switch (definition.Kind)
        {
            case PickupKind.Weapon when isAuthority:
                collector.SetPrimaryWeapon(definition.WeaponGranted);
                break;

            case PickupKind.Weapon:
                collector.SetPrimaryWeaponNetworked(definition.WeaponGranted);
                break;

            case PickupKind.Buff:
                collector.GrantBuff(definition.BuffGranted, definition.BuffDuration);
                break;

            case PickupKind.Restore:
                collector.Health = MathF.Min(
                    collector.Tuning.HealthMax,
                    collector.Health + definition.RestoreHealth);

                collector.Shield = MathF.Min(
                    collector.ShieldMaximum,
                    collector.Shield + definition.RestoreShield);
                break;
        }
    }

    public void CollectPowerUp(PowerUp? powerUp, Ship? collector)
    {
        if (!IsAuthority
            || powerUp is null
            || collector is null
            || !_activePowerUps.ContainsKey(powerUp.UniqueId))
        {
            return;
        }

        var definition = powerUp.Definition;
        var position = powerUp.Position;
        ApplyPickup(definition, collector, isAuthority: true);
        RetirePowerUp(powerUp);

        PowerUpCollected?.Invoke(powerUp, collector, definition.PowerUpType);
        RaiseLocalPowerUpCollected(collector, definition.PowerUpType);
        _network?.BroadcastPowerUpCollected(
            new PowerUpCollectedPayload(powerUp.UniqueId, collector.UniqueId, definition.PowerUpType));

        EmitGameplayEvent(GameplayEventType.PowerUpCollected, position);
    }

    /// <summary>
    /// Returns a collected or cleared pickup body to the pool.
    /// </summary>
    /// <remarks>
    /// Guarded against double-return: a body listed twice in the free pool would later be
    /// handed out to two spawns, leaving two active entries pointing at one body and one
    /// of them permanently uncollectable.
    /// </remarks>
    private void RetirePowerUp(PowerUp powerUp)
    {
        powerUp.IsActive = false;
        _activePowerUps.Remove(powerUp.UniqueId);

        if (!_freePowerUps.Contains(powerUp))
        {
            _freePowerUps.Add(powerUp);
        }
    }

    // --- Gameplay events ----------------------------------------------------

    /// <summary>
    /// Raises <see cref="LocalWeaponFired"/> if <paramref name="ship"/> is the local
    /// player's. Called by <see cref="Ship"/> as the shot leaves, on every peer.
    /// </summary>
    internal void RaiseLocalWeaponFired(Ship ship, WeaponType weapon)
    {
        if (ReferenceEquals(ship, LocalShip))
        {
            LocalWeaponFired?.Invoke(weapon);
        }
    }

    /// <summary>
    /// Raises <see cref="LocalPowerUpCollected"/> if <paramref name="collector"/> is the
    /// local player's ship. Called from both the authority's collection path and the
    /// client's inbound one.
    /// </summary>
    internal void RaiseLocalPowerUpCollected(Ship? collector, PowerUpType powerUp)
    {
        if (collector is not null && ReferenceEquals(collector, LocalShip))
        {
            LocalPowerUpCollected?.Invoke(powerUp);
        }
    }

    /// <summary>
    /// Authority-sourced events: played locally and replicated so every client fires the
    /// same effect exactly once.
    /// </summary>
    public void EmitGameplayEvent(GameplayEventType eventType, Vector2 position)
    {
        if (IsAuthority)
        {
            GameplayEvent?.Invoke(eventType, position);
            _network?.BroadcastGameplayEvent(eventType, position);
        }
    }

    /// <summary>
    /// Purely cosmetic, machine-local effects that each peer generates from its own
    /// simulation and never sends over the network.
    /// </summary>
    public void EmitLocalEffect(GameplayEventType eventType, Vector2 position)
        => GameplayEvent?.Invoke(eventType, position);

    // --- Object bookkeeping -------------------------------------------------

    public void DestroyGameObjectById(int uniqueId)
    {
        var obj = GetGameObject(uniqueId);

        if (obj is null)
        {
            return;
        }

        obj.IsActive = false;

        if (obj is Projectile projectile)
        {
            _activeProjectiles.Remove(projectile.UniqueId);
            projectile.ClearCollisionExceptions();
            _projectileCaches[projectile.ProjectileType].Enqueue(projectile);
        }
    }

    /// <summary>
    /// Client-side recycle for a shot the authority has stopped tracking.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="DestroyGameObjectById"/> only in intent: this is the path
    /// a client takes when a detonation message never arrived, so it deliberately emits no
    /// effect and tells nobody. The body simply goes back in its pool.
    /// </remarks>
    public void RetireOrphanedProjectile(Projectile? projectile)
    {
        if (projectile is null || IsAuthority)
        {
            return;
        }

        projectile.IsActive = false;
        _activeProjectiles.Remove(projectile.UniqueId);
        projectile.ClearCollisionExceptions();
        _projectileCaches[projectile.ProjectileType].Enqueue(projectile);
    }

    // --- Internals ----------------------------------------------------------

    private void Simulate(float delta)
    {
        // Clients tick the world before the host's match-created payload arrives, so
        // nothing may exist yet.
        if (_collision is null)
        {
            return;
        }

        _stepBuffer.Clear();

        foreach (var obj in _gameObjects.Values)
        {
            if (obj.IsActive)
            {
                obj.Tick(delta);
            }

            _stepBuffer.Add(obj);
        }

        _collision.Step(_stepBuffer, delta);
        ProcessPendingMineDetonations();
    }

    private void ProcessPendingMineDetonations()
    {
        if (_processingMineDetonations)
        {
            return;
        }

        _processingMineDetonations = true;

        // A detonation can queue further detonations, so the list is drained rather than
        // iterated; the guard above stops the recursion from re-entering.
        while (_pendingMineDetonations.Count > 0)
        {
            var mine = _pendingMineDetonations[0];
            _pendingMineDetonations.RemoveAt(0);

            if (mine.IsActive)
            {
                mine.DetonateQueued();
            }
        }

        _processingMineDetonations = false;
    }

    private void DetectDestroyedShips()
    {
        List<Ship>? destroyed = null;

        foreach (var ship in _ships.Values)
        {
            if (ship.IsActive && ship.Health > 0.0f)
            {
                _announcedDeaths.Remove(ship.UniqueId);
            }
            else if (ship.Health <= 0.0f && !_announcedDeaths.Contains(ship.UniqueId))
            {
                (destroyed ??= []).Add(ship);
            }
        }

        if (destroyed is null)
        {
            return;
        }

        destroyed.Sort(static (a, b) => a.UniqueId.CompareTo(b.UniqueId));

        foreach (var ship in destroyed)
        {
            _announcedDeaths.Add(ship.UniqueId);
            var killerPeerId = ResolveKillerPeerId(ship);
            EmitGameplayEvent(GameplayEventType.ShipDestroyed, ship.Position);
            ShipDestroyed?.Invoke(ship, killerPeerId);
        }
    }

    private int ResolveKillerPeerId(Ship ship)
    {
        if (GetGameObject(ship.LastDamagedById) is Projectile damager)
        {
            var killer = GetShipById(damager.OwnerId);

            if (killer is not null)
            {
                return killer.OwnerPeerId;
            }
        }

        return -1;
    }

    // --- Asteroid splitting -------------------------------------------------

    /// <summary>
    /// Authority-only: breaks apart every asteroid whose health reached zero this tick.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately a poll at the end of the tick rather than something
    /// <see cref="Asteroid.TakeDamage"/> does directly. Damage arrives from a contact
    /// callback and from <see cref="ApplyExplosionDamage"/>'s hit loop, both of which are
    /// already part-way through iterating <c>_gameObjects</c>; adding and removing
    /// asteroids from in there would invalidate the iteration.
    /// </para>
    /// <para>
    /// Processing in id order, like <see cref="DetectDestroyedShips"/>, keeps a
    /// multi-rock detonation resolving the same way every time instead of in whatever
    /// order the dictionary happens to enumerate.
    /// </para>
    /// </remarks>
    private void DetectSplitAsteroids()
    {
        _splitBuffer.Clear();

        foreach (var asteroid in _asteroids.Values)
        {
            if (asteroid.IsActive && asteroid.Health <= 0.0f)
            {
                _splitBuffer.Add(asteroid);
            }
        }

        if (_splitBuffer.Count == 0)
        {
            return;
        }

        _splitBuffer.Sort(static (a, b) => a.UniqueId.CompareTo(b.UniqueId));

        foreach (var asteroid in _splitBuffer)
        {
            SplitAsteroid(asteroid);
        }

        _splitBuffer.Clear();
    }

    private void SplitAsteroid(Asteroid parent)
    {
        var position = parent.Position;
        var creditedPeerId = ResolveAsteroidCredit(parent);
        var fragments = BuildAsteroidFragments(parent);

        // The parent goes first, before its fragments exist. Its collider still occupies
        // exactly the space they are about to spawn into, and leaving it there would have
        // the solver shove them apart far harder than the split speed intends.
        RemoveObject(parent);

        foreach (var fragment in fragments)
        {
            SpawnAsteroidFragment(fragment);
        }

        EmitGameplayEvent(GameplayEventType.AsteroidImpact, position);
        NoteAsteroidDestroyed(creditedPeerId);

        _network?.BroadcastAsteroidSplit(
            new AsteroidSplitPayload(parent.UniqueId, position, creditedPeerId, fragments));
    }

    /// <summary>
    /// Rolls the fragments a rock breaks into. Authority-only - every value here is
    /// random, and all of them travel to the clients rather than being re-rolled.
    /// </summary>
    private List<AsteroidFragment> BuildAsteroidFragments(Asteroid parent)
    {
        var fragments = new List<AsteroidFragment>();
        var childSize = parent.SplitSize();

        if (childSize is null)
        {
            return fragments;
        }

        var tuning = parent.Tuning;

        // The parent is still counted here, deliberately: a field at the cap degrades by
        // dropping fragments rather than by refusing to break rocks at all.
        var budget = NRConst.MaxAsteroids - _asteroids.Count;
        var count = Math.Min(Math.Max(tuning.SplitCount, 0), Math.Max(budget, 0));

        if (count == 0)
        {
            return fragments;
        }

        var childRadius = tuning.RadiusFor(childSize.Value);
        var offsetDistance = childRadius * tuning.SplitSpawnSpread;
        var baseAngle = NextFloat(0.0f, MathF.Tau);

        for (var i = 0; i < count; i++)
        {
            var angle = baseAngle
                + (MathF.Tau * i / count)
                + NextFloat(-tuning.SplitAngleJitter, tuning.SplitAngleJitter);

            var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            var speed = NextFloat(tuning.SplitSpeedMin, tuning.SplitSpeedMax);

            fragments.Add(new AsteroidFragment(
                GameObject.AllocateId(),
                childSize.Value,
                _random.Next(0, 3),
                parent.Position + (direction * offsetDistance),

                // The parent's momentum is inherited, so a fragment field keeps drifting
                // the way the rock was going instead of stalling at the break point.
                parent.Velocity + (direction * speed),
                NextFloat(0.0f, MathF.Tau)));
        }

        return fragments;
    }

    /// <summary>
    /// Materialises one fragment. Shared by the authority and by the client applying a
    /// split message, so the two paths cannot drift apart.
    /// </summary>
    private void SpawnAsteroidFragment(AsteroidFragment fragment)
    {
        if (_asteroids.ContainsKey(fragment.Id))
        {
            return;
        }

        var asteroid = new Asteroid();
        asteroid.SetWorld(this);
        asteroid.SetupFragment(fragment.Id, fragment.Size, fragment.Variation, fragment.Rotation);
        asteroid.Teleport(fragment.Position);
        asteroid.Velocity = fragment.Velocity;
        asteroid.IsActive = true;
        asteroid.SimulationRunning = _simulationRunning;

        _asteroids[asteroid.UniqueId] = asteroid;
        _gameObjects[asteroid.UniqueId] = asteroid;
    }

    /// <summary>
    /// Works out whose shot broke a rock, or -1 for nobody.
    /// </summary>
    /// <remarks>
    /// Only projectile damage earns credit, mirroring <see cref="ResolveKillerPeerId"/>:
    /// a rock a ship simply barged into belongs to no one.
    /// </remarks>
    private int ResolveAsteroidCredit(Asteroid asteroid)
    {
        if (GetGameObject(asteroid.LastDamagedById) is Projectile damager)
        {
            return GetShipById(damager.OwnerId)?.OwnerPeerId ?? -1;
        }

        return -1;
    }

    /// <summary>
    /// Announces a destroyed rock to the local player, and only when it was theirs.
    /// </summary>
    /// <remarks>
    /// Runs on both the authority and on a client applying a split message, so the
    /// achievement layer needs one subscription either way - the same arrangement
    /// <see cref="LocalPowerUpCollected"/> uses.
    /// </remarks>
    private void NoteAsteroidDestroyed(int peerId)
    {
        if (peerId >= 0 && peerId == (_network?.LocalPeerId ?? NRConst.HostPeerId))
        {
            LocalAsteroidDestroyed?.Invoke();
        }
    }

    /// <summary>
    /// Removes an object from the world outright, as opposed to
    /// <see cref="DestroyGameObjectById"/>, which deactivates and recycles it.
    /// </summary>
    /// <remarks>
    /// Asteroid fragments are created on demand and cannot be pooled - their count is
    /// unbounded within the cap - so a broken rock really does have to leave the
    /// dictionaries. The collision world needs no deregistration: it rebuilds its body
    /// list from <c>_gameObjects</c> every step, and ids are never reused, so nothing it
    /// cached can be confused for a later object.
    /// </remarks>
    private void RemoveObject(GameObject obj)
    {
        obj.IsActive = false;
        _gameObjects.Remove(obj.UniqueId);

        if (obj is Asteroid)
        {
            _asteroids.Remove(obj.UniqueId);
        }
    }

    /// <summary>
    /// Exempts the shots in a volley from colliding with each other.
    /// </summary>
    /// <remarks>
    /// They leave the muzzle overlapping, and without this the solver depenetrates them
    /// and sprays the spread apart. Same-owner shots already ignore each other in
    /// gameplay.
    /// </remarks>
    private void SpawnVolley(List<Projectile> volley)
    {
        foreach (var shot in volley)
        {
            foreach (var other in volley)
            {
                shot.AddCollisionException(other);
            }

            NotifyProjectileSpawned(shot);
        }
    }

    private void SpawnPowerUp(PowerUpType type)
    {
        if (_freePowerUps.Count == 0)
        {
            return;
        }

        var powerUp = _freePowerUps[0];
        _freePowerUps.RemoveAt(0);

        // Pooled bodies are generic: which pickup they represent is decided here, at
        // spawn time, and replicated. That is what lets two dozen bodies stand in for a
        // thirty-two-entry table.
        powerUp.AssignType(type);
        powerUp.Teleport(FindSpawnPoint(powerUp.Radius));
        powerUp.IsActive = true;
        powerUp.Start();
        _activePowerUps[powerUp.UniqueId] = powerUp;

        PowerUpSpawned?.Invoke(powerUp);
        _network?.BroadcastPowerUpSpawned(
            new PowerUpSpawnedPayload(powerUp.UniqueId, type, powerUp.Position));

        EmitGameplayEvent(GameplayEventType.PowerUpSpawned, powerUp.Position);
    }

    private void ResetPowerUps()
    {
        _activePowerUps.Clear();
        _freePowerUps.Clear();

        foreach (var powerUp in _powerUps.Values)
        {
            powerUp.IsActive = false;
            _freePowerUps.Add(powerUp);
        }

        ResetPowerUpSpawnTimer();
    }

    private void ResetPowerUpSpawnTimer()
        => _powerUpSpawnTimer = NRConst.PowerUpSpawnInterval(PowerUpFrequency);

    private Vector2 RandomPoint(float radius) => new(
        radius + NextFloat(0.0f, WorldWidth - radius),
        radius + NextFloat(0.0f, WorldHeight - radius));

    private float NextFloat(float min, float max) => min + ((float)_random.NextDouble() * (max - min));

    private static bool CircleIntersect(Vector2 centre1, float radius1, Vector2 centre2, float radius2)
        => (centre2 - centre1).LengthSquared() <= (radius1 + radius2) * (radius1 + radius2);

    private void ServerInitialize()
    {
        WorldWidth = NRConst.WorldWidth;
        WorldHeight = NRConst.WorldHeight;
        _collision = new CircleCollisionWorld(WorldWidth, WorldHeight);

        InitAsteroids(NRConst.AsteroidCount);
        InitShips(_players);
        InitProjectiles(_players.Count);
        InitPowerUps();
        BindLocalShip();
        InitBots();
    }

    /// <remarks>
    /// Seeds from <see cref="AsteroidSize.Medium"/> up only, so that every rock on the
    /// field has at least two generations of breaking ahead of it. Starting a match with
    /// rocks that vanish on the first hit makes the field feel like it is evaporating.
    /// </remarks>
    private void InitAsteroids(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var size = (AsteroidSize)_random.Next((int)AsteroidSize.Medium, (int)AsteroidSize.Huge + 1);
            var asteroid = new Asteroid();
            asteroid.SetWorld(this);
            asteroid.Setup(size, _random);
            asteroid.Teleport(FindSpawnPoint(asteroid.Radius));
            _asteroids[asteroid.UniqueId] = asteroid;
            _gameObjects[asteroid.UniqueId] = asteroid;
        }
    }

    private void InitShips(List<PlayerState> playerStates)
    {
        foreach (var playerState in playerStates)
        {
            var ship = new Ship();
            ship.SetWorld(this);
            ship.Setup(playerState);
            ship.Teleport(FindSpawnPoint(ship.Radius));
            _ships[ship.UniqueId] = ship;
            _shipsByPeer[ship.OwnerPeerId] = ship;
            _gameObjects[ship.UniqueId] = ship;
        }
    }

    /// <summary>
    /// Attaches an AI driver to every ship whose player row is flagged as a bot.
    /// </summary>
    /// <remarks>
    /// Bots are plain ships: the only difference is where their <see cref="ShipInput"/>
    /// comes from, so nothing downstream of here has to know they exist.
    /// </remarks>
    private void InitBots()
    {
        _botControllers.Clear();

        if (!IsAuthority)
        {
            return;
        }

        foreach (var playerState in _players)
        {
            if (!playerState.IsBot
                || !_shipsByPeer.TryGetValue(playerState.PeerId, out var ship)
                || ship == LocalShip)
            {
                continue;
            }

            _botControllers[ship.UniqueId] = new BotController(this, ship, _random);
        }
    }

    /// <summary>Runs every bot's AI for one step.</summary>
    private void TickBots(float delta)
    {
        if (!IsAuthority || _botControllers.Count == 0)
        {
            return;
        }

        foreach (var controller in _botControllers.Values)
        {
            controller.Tick(delta);
        }
    }

    /// <remarks>
    /// Pooled projectiles are created up front, so each needs its own id the moment it
    /// enters the pool. Without one every pooled projectile shares id 0 and collapses onto
    /// a single dictionary entry, which stops all but one from ever being simulated.
    /// </remarks>
    private void InitProjectiles(int playerCount)
    {
        var counts = new (ProjectileType Type, int Count)[]
        {
            (ProjectileType.Laser, NRConst.MaxLasersPerPlayer * playerCount),
            (ProjectileType.Mine, NRConst.MaxMinesPerPlayer * playerCount),
            (ProjectileType.Rocket, NRConst.MaxRocketsPerPlayer * playerCount),
        };

        foreach (var (type, count) in counts)
        {
            for (var i = 0; i < count; i++)
            {
                Projectile projectile = type switch
                {
                    ProjectileType.Mine => new MineProjectile(),
                    ProjectileType.Rocket => new RocketProjectile(),
                    _ => new Projectile(ProjectileType.Laser),
                };

                projectile.SetWorld(this);
                projectile.UniqueId = GameObject.AllocateId();
                projectile.IsActive = false;
                _gameObjects[projectile.UniqueId] = projectile;
                _projectileCaches[projectile.ProjectileType].Enqueue(projectile);
            }
        }
    }

    private void InitPowerUps()
    {
        // A pool of interchangeable bodies rather than one per pickup type. Keying by
        // type capped the field at a single instance of each and - because the spawn
        // looked its roll up in that dictionary - silently dropped every roll for a type
        // with no body, which made twenty-nine of the thirty-two pickups unreachable.
        for (var i = 0; i < NRConst.PowerUpPoolSize; i++)
        {
            var powerUp = new PowerUp();
            powerUp.SetWorld(this);
            powerUp.Setup(PowerUpType.DoubleLaser);
            powerUp.IsActive = false;
            _powerUps[powerUp.UniqueId] = powerUp;
            _gameObjects[powerUp.UniqueId] = powerUp;
            _freePowerUps.Add(powerUp);
        }
    }

    /// <summary>Binds the local player's ship, for prediction and camera follow.</summary>
    public void BindLocalShip(int localPeerId = NRConst.HostPeerId)
        => LocalShip = GetShipFor(localPeerId);

    /// <summary>The current snapshot sequence number, for the replication layer.</summary>
    public int NextSnapshotFrame() => ++_snapshotFrame;
}
