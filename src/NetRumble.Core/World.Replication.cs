using System.Numerics;
using NetRumble.Core.Net;
using NetRumble.Core.Objects;
using NetRumble.Core.Simulation;
using NetRumble.Core.Tuning;

namespace NetRumble.Core;

/// <summary>
/// The replication half of <see cref="World"/>: snapshot production on the host, and the
/// inbound handlers plus local-ship reconciliation on a client. Ported from the second
/// half of <c>scripts/gameplay/world.gd</c>.
/// </summary>
/// <remarks>
/// <para>
/// Split into its own file because the simulation half stands alone and is worth reading
/// without the netcode interleaved - the GDScript original mixed them and it is the
/// hardest part of that file to follow.
/// </para>
/// <para>
/// <b>Every handler bails on the authority.</b> The host is the source of truth; a host
/// that applied its own broadcasts would fight its own simulation. This mirrors the
/// <c>if is_authority: return</c> guard at the top of each <c>_on_*_received</c>.
/// </para>
/// </remarks>
public sealed partial class World
{
    /// <summary>
    /// Attaches the transport and subscribes the client-side handlers.
    /// </summary>
    /// <remarks>
    /// Subscribing on both host and client is deliberate: the guards live inside the
    /// handlers rather than at the subscription, so a peer that is promoted or demoted
    /// mid-session does not need its wiring rebuilt.
    /// </remarks>
    public void AttachNetwork(IMatchNetwork network)
    {
        ArgumentNullException.ThrowIfNull(network);

        _network = network;

        network.MatchCreated += ApplyMatchCreated;
        network.MatchStarting += ApplyMatchStarting;
        network.WorldSnapshotReceived += OnWorldSnapshotReceived;
        network.ProjectileSpawnedReceived += OnProjectileSpawnedReceived;
        network.ProjectileDetonatedReceived += OnProjectileDetonatedReceived;
        network.PowerUpSpawnedReceived += OnPowerUpSpawnedReceived;
        network.PowerUpCollectedReceived += OnPowerUpCollectedReceived;
        network.ShipSpawnedReceived += OnShipSpawnedReceived;
        network.ShipDestroyedReceived += OnShipDestroyedReceived;
        network.AsteroidSplitReceived += OnAsteroidSplitReceived;
        network.GameplayEventReceived += OnGameplayEventReceived;
    }

    /// <summary>Removes every transport subscription installed by <see cref="AttachNetwork"/>.</summary>
    public void DetachNetwork()
    {
        if (_network is null)
        {
            return;
        }

        _network.MatchCreated -= ApplyMatchCreated;
        _network.MatchStarting -= ApplyMatchStarting;
        _network.WorldSnapshotReceived -= OnWorldSnapshotReceived;
        _network.ProjectileSpawnedReceived -= OnProjectileSpawnedReceived;
        _network.ProjectileDetonatedReceived -= OnProjectileDetonatedReceived;
        _network.PowerUpSpawnedReceived -= OnPowerUpSpawnedReceived;
        _network.PowerUpCollectedReceived -= OnPowerUpCollectedReceived;
        _network.ShipSpawnedReceived -= OnShipSpawnedReceived;
        _network.ShipDestroyedReceived -= OnShipDestroyedReceived;
        _network.AsteroidSplitReceived -= OnAsteroidSplitReceived;
        _network.GameplayEventReceived -= OnGameplayEventReceived;
        _network = null;
    }

    // --- Host: outbound -----------------------------------------------------

    /// <summary>
    /// Builds and sends a snapshot of every ship and asteroid. Called by the match
    /// director at <see cref="NRConst.WorldSnapshotHz"/>.
    /// </summary>
    /// <remarks>
    /// Projectiles are deliberately absent. They are short-lived, spawned and detonated by
    /// explicit messages, and dead-reckon accurately from a constant velocity, so
    /// streaming them would multiply snapshot size for no visible gain.
    /// </remarks>
    public void BroadcastSnapshot()
    {
        if (!IsAuthority || _network is null)
        {
            return;
        }

        var objects = new List<SnapshotEntry>(_ships.Count + _asteroids.Count);

        foreach (var ship in _ships.Values)
        {
            objects.Add(new SnapshotEntry(
                ship.UniqueId,
                ship.Position,
                ship.Velocity,
                ship.Rotation,
                ship.Health,
                ship.Shield));
        }

        foreach (var asteroid in _asteroids.Values)
        {
            objects.Add(new SnapshotEntry(
                asteroid.UniqueId,
                asteroid.Position,
                asteroid.Velocity,
                asteroid.Rotation,
                asteroid.Health,
                -1.0f));
        }

        _network.BroadcastWorldSnapshot(new WorldSnapshot(NextSnapshotFrame(), objects));
    }

    /// <summary>
    /// Ceiling on projectiles a client will hold on the host's word. See
    /// <see cref="OnProjectileSpawnedReceived"/>.
    /// </summary>
    private const int MaxNetworkedProjectiles = 2048;

    /// <summary>
    /// Whether a world dimension from the wire is one a real build could have sent.
    /// </summary>
    /// <remarks>
    /// Every peer in a match shares <see cref="NRConst"/>, so the only legitimate answer
    /// is the constant itself. The check is written as a range rather than an equality so
    /// a future build that makes the arena configurable does not have to remember to
    /// loosen it in two places - but the range is still tight enough that nothing it
    /// admits can break the collision grid or a source rectangle.
    /// </remarks>
    private static bool IsPlausibleWorldSize(int value)
        => value >= NRConst.WorldWidth / 4 && value <= NRConst.WorldWidth * 4;

    private MatchCreatedPayload BuildMatchCreatedPayload()
    {
        var asteroids = new List<AsteroidSpawn>(_asteroids.Count);

        foreach (var a in _asteroids.Values)
        {
            asteroids.Add(new AsteroidSpawn(
                a.UniqueId,
                a.AsteroidSize,
                a.Variation,
                a.Position,
                a.Velocity,
                a.Rotation));
        }

        var ships = new List<ShipSpawn>(_ships.Count);

        foreach (var s in _ships.Values)
        {
            ships.Add(new ShipSpawn(
                s.UniqueId,
                s.OwnerPeerId,
                s.ShipColorId,
                s.ShipStyleId,
                s.Position,
                s.Rotation));
        }

        var powerUps = new List<PowerUpSpawn>(_powerUps.Count);

        foreach (var p in _powerUps.Values)
        {
            // The type here is the body's current one and is a placeholder: pool bodies
            // are retyped on every spawn, and the client is told which pickup it is by
            // the PowerUpSpawned message. What the announce actually establishes is the
            // set of body ids, so both sides address the same bodies.
            powerUps.Add(new PowerUpSpawn(p.UniqueId, p.PowerUpType));
        }

        return new MatchCreatedPayload(WorldWidth, WorldHeight, asteroids, ships, powerUps);
    }

    private MatchStartingPayload BuildMatchStartingPayload()
    {
        var resets = new List<ResetEntry>(_asteroids.Count + _ships.Count);

        foreach (var a in _asteroids.Values)
        {
            resets.Add(new ResetEntry(a.UniqueId, a.Position, a.Velocity, a.Rotation));
        }

        foreach (var s in _ships.Values)
        {
            resets.Add(new ResetEntry(s.UniqueId, s.Position, s.Velocity, s.Rotation));
        }

        return new MatchStartingPayload(resets);
    }

    // --- Client: world construction -----------------------------------------

    /// <summary>
    /// Builds the client's entities from the host's layout.
    /// </summary>
    /// <remarks>
    /// Guarded against running twice. The host announces the world exactly once, but a
    /// duplicate - or a resend to a late joiner that already built it - would otherwise
    /// stack a second set of ships and asteroids on top of the first.
    /// </remarks>
    private void ApplyMatchCreated(MatchCreatedPayload payload)
    {
        if (IsAuthority || IsBuilt)
        {
            return;
        }

        // The dimensions come off the wire and size the collision grid, the camera clamp
        // and the barrier source rectangles. A hostile or corrupt value produces a
        // nonsense play area at best and garbage draw rectangles at worst, so anything
        // outside the range a real build could have sent is refused rather than adapted
        // to - every peer in a match runs the same NRConst.
        if (!IsPlausibleWorldSize(payload.Width) || !IsPlausibleWorldSize(payload.Height))
        {
            return;
        }

        WorldWidth = payload.Width;
        WorldHeight = payload.Height;
        _collision = new CircleCollisionWorld(WorldWidth, WorldHeight);

        foreach (var spawn in payload.Asteroids)
        {
            var asteroid = new Asteroid();
            asteroid.SetWorld(this);
            asteroid.SetupNetworked(spawn.Id, spawn.Size, spawn.Variation);
            asteroid.Teleport(spawn.Position, spawn.Rotation);
            asteroid.Velocity = spawn.Velocity;

            _asteroids[asteroid.UniqueId] = asteroid;
            _gameObjects[asteroid.UniqueId] = asteroid;
        }

        foreach (var spawn in payload.Ships)
        {
            var ship = new Ship();
            ship.SetWorld(this);
            ship.SetupNetworked(spawn.Id, spawn.PeerId, spawn.ColorId, spawn.StyleId);
            ship.Teleport(spawn.Position, spawn.Rotation);

            _ships[ship.UniqueId] = ship;
            _shipsByPeer[ship.OwnerPeerId] = ship;
            _gameObjects[ship.UniqueId] = ship;
        }

        foreach (var spawn in payload.PowerUps)
        {
            var powerUp = new PowerUp();
            powerUp.SetWorld(this);
            powerUp.SetupNetworked(spawn.Id, spawn.PowerUpType);
            powerUp.IsActive = false;

            _powerUps[powerUp.UniqueId] = powerUp;
            _gameObjects[powerUp.UniqueId] = powerUp;
        }

        BindLocalShip(_network?.LocalPeerId ?? NRConst.HostPeerId);

        foreach (var obj in _gameObjects.Values)
        {
            obj.Start();
        }

        if (LocalShip is not null)
        {
            LocalShipChanged?.Invoke(LocalShip);
        }
    }

    private void ApplyMatchStarting(MatchStartingPayload payload)
    {
        if (IsAuthority)
        {
            return;
        }

        foreach (var reset in payload.Resets)
        {
            if (!_gameObjects.TryGetValue(reset.Id, out var obj))
            {
                continue;
            }

            obj.IsActive = true;
            obj.Teleport(reset.Position, reset.Rotation);
            obj.Velocity = reset.Velocity;
        }
    }

    // --- Client: snapshots --------------------------------------------------

    private void OnWorldSnapshotReceived(WorldSnapshot snapshot)
    {
        if (IsAuthority || snapshot.Frame <= _lastWorldDataFrame)
        {
            return;
        }

        _lastWorldDataFrame = snapshot.Frame;

        var localId = LocalShip?.UniqueId ?? -1;

        foreach (var entry in snapshot.Objects)
        {
            if (!_gameObjects.TryGetValue(entry.Id, out var obj))
            {
                continue;
            }

            if (entry.Id == localId)
            {
                ReconcileLocalShip(entry.Position, entry.Velocity, entry.Rotation);
            }
            else
            {
                // Remote objects are eased toward the host's copy rather than snapped onto
                // it; the remainder of the error is closed by local dead reckoning between
                // snapshots, which is what keeps them smooth at 30 Hz.
                obj.Velocity = entry.Velocity;
                obj.Teleport(
                    Vector2.Lerp(obj.Position, entry.Position, NRConst.SnapshotLerp),
                    LerpAngle(obj.Rotation, entry.Rotation, NRConst.SnapshotLerp));
            }

            // Damage is resolved by the host for every ship, the local one included, so
            // health and shield are taken verbatim rather than predicted. Assigned
            // directly rather than through ApplyAuthoritativeDamageState: that method also
            // restarts the shield recharge delay, which at 30 Hz would hold the delay open
            // forever and stop a client's shield from ever recharging locally.
            if (obj is Ship ship)
            {
                ship.Health = entry.Health;
                ship.Shield = entry.Shield;
            }
        }
    }

    /// <summary>
    /// Nudges the locally predicted ship toward the host's copy.
    /// </summary>
    /// <remarks>
    /// The local ship is predicted rather than interpolated, so it is corrected far more
    /// gently than remote objects - the player is already looking at their own prediction
    /// and anything stronger reads as rubber-banding. Without any correction the two
    /// simulations drift apart for the rest of the match: the player sees themselves where
    /// they predicted, and everyone else sees the host's version somewhere else entirely.
    /// </remarks>
    private void ReconcileLocalShip(Vector2 targetPosition, Vector2 targetVelocity, float targetRotation)
    {
        if (LocalShip is null)
        {
            return;
        }

        if (Vector2.Distance(LocalShip.Position, targetPosition) > NRConst.LocalSnapshotSnapDistance)
        {
            // Prediction is too far gone to close smoothly - a missed collision, a respawn
            // or a stall - so take the host's state outright rather than sliding across the
            // map for several seconds.
            LocalShip.Teleport(targetPosition, targetRotation);
            LocalShip.Velocity = targetVelocity;
            return;
        }

        LocalShip.Velocity = Vector2.Lerp(
            LocalShip.Velocity,
            targetVelocity,
            NRConst.LocalSnapshotLerp);

        // Facing is driven directly by the stick, so it is left alone until it has drifted
        // far enough to matter; pulling on it every snapshot makes turning feel laggy.
        var newRotation = LocalShip.Rotation;

        if (MathF.Abs(AngleDifference(LocalShip.Rotation, targetRotation))
            > NRConst.LocalSnapshotRotationTolerance)
        {
            newRotation = LerpAngle(LocalShip.Rotation, targetRotation, NRConst.LocalSnapshotLerp);
        }

        LocalShip.Teleport(
            Vector2.Lerp(LocalShip.Position, targetPosition, NRConst.LocalSnapshotLerp),
            newRotation);
    }

    // --- Client: discrete events --------------------------------------------

    private void OnProjectileSpawnedReceived(ProjectileSpawnedPayload payload)
    {
        if (IsAuthority)
        {
            return;
        }

        // Clients do not pool: the host decides which projectiles exist and when, so a
        // client that recycled its own would have to keep a parallel pool in step with the
        // host's for no benefit.
        if (_gameObjects.TryGetValue(payload.Id, out var existing) && existing is not Projectile)
        {
            return;
        }

        if (existing is not Projectile projectile)
        {
            // Bounded, because the host chooses both the id and how many. Nothing is
            // removed from _gameObjects until a matching detonation arrives, so a hostile
            // or looping host would otherwise grow a client's object dictionary - and its
            // per-step simulation set - without limit. The cap is far above what a full
            // lobby firing continuously produces, so it never trips in a real match.
            if (_activeProjectiles.Count >= MaxNetworkedProjectiles)
            {
                return;
            }

            projectile = payload.ProjectileType switch
            {
                ProjectileType.Mine => new MineProjectile(),
                ProjectileType.Rocket => new RocketProjectile(),
                _ => new Projectile(ProjectileType.Laser),
            };

            projectile.SetWorld(this);
            projectile.UniqueId = payload.Id;
            _gameObjects[payload.Id] = projectile;
        }

        projectile.ActivateFromAuthority(
            payload.OwnerId,
            payload.Position,
            payload.Velocity,
            payload.Rotation,
            payload.Spec);

        _activeProjectiles[payload.Id] = projectile;
    }

    private void OnProjectileDetonatedReceived(ProjectileDetonatedPayload payload)
    {
        if (IsAuthority)
        {
            return;
        }

        if (_gameObjects.TryGetValue(payload.Id, out var obj) && obj is Projectile projectile)
        {
            // Moved to the host's detonation point first so the effect plays where the host
            // saw the blast, not where this client's dead reckoning had drifted to.
            projectile.DeactivateFromAuthority(payload.Position);
            _activeProjectiles.Remove(payload.Id);
        }

        ApplyProjectileDetonationResults(payload.Hits);
    }

    private void OnPowerUpSpawnedReceived(PowerUpSpawnedPayload payload)
    {
        if (IsAuthority || !_gameObjects.TryGetValue(payload.Id, out var obj) || obj is not PowerUp powerUp)
        {
            return;
        }

        // Pool bodies are reused, so the host's choice of type for this use arrives with
        // the spawn. Applying it here is what keeps the client drawing and describing the
        // same pickup the host will grant when someone takes it.
        powerUp.AssignType(payload.PowerUpType);
        powerUp.Teleport(payload.Position);
        powerUp.IsActive = true;
        powerUp.Start();
        _activePowerUps[powerUp.UniqueId] = powerUp;
    }

    private void OnPowerUpCollectedReceived(PowerUpCollectedPayload payload)
    {
        if (IsAuthority)
        {
            return;
        }

        if (_gameObjects.TryGetValue(payload.Id, out var obj) && obj is PowerUp powerUp)
        {
            powerUp.IsActive = false;
            _activePowerUps.Remove(powerUp.UniqueId);
        }

        var collector = GetShipById(payload.CollectorId);

        if (collector is not null)
        {
            // The message's type is used rather than the body's, because a client whose
            // spawn message was lost still has the body typed from its last use. The host
            // says what was granted; that is the only account that can be wrong-free.
            ApplyPickup(PickupLibrary.Get(payload.PowerUp), collector, isAuthority: false);
        }

        RaiseLocalPowerUpCollected(collector, payload.PowerUp);
    }

    /// <summary>
    /// Client: replays a split exactly as the host rolled it.
    /// </summary>
    /// <remarks>
    /// A split is not derivable from a snapshot - every fragment's id, angle and speed is
    /// a random roll - so the message carries all of them and this applies them verbatim.
    /// An unknown parent id is ignored rather than treated as an error: it means this peer
    /// joined after that rock already broke, and the fragments it lists are spawned
    /// regardless so the field still ends up correct.
    /// </remarks>
    private void OnAsteroidSplitReceived(AsteroidSplitPayload payload)
    {
        if (IsAuthority)
        {
            return;
        }

        if (_asteroids.TryGetValue(payload.Id, out var parent))
        {
            RemoveObject(parent);
        }

        foreach (var fragment in payload.Fragments)
        {
            SpawnAsteroidFragment(fragment);
        }

        GameplayEvent?.Invoke(GameplayEventType.AsteroidImpact, payload.Position);
        NoteAsteroidDestroyed(payload.PeerId);
    }

    private void OnShipSpawnedReceived(ShipSpawnedPayload payload)
    {
        if (IsAuthority)
        {
            return;
        }

        var ship = GetShipById(payload.ShipId);

        if (ship is null)
        {
            return;
        }

        ship.Start();
        ship.Teleport(payload.Position);
        ship.IsActive = true;
    }

    private void OnShipDestroyedReceived(ShipDestroyedPayload payload)
    {
        if (!IsAuthority)
        {
            GetShipById(payload.ShipId)?.Die();
        }
    }

    private void OnGameplayEventReceived(GameplayEventType eventType, Vector2 position)
    {
        if (!IsAuthority)
        {
            EmitLocalEffect(eventType, position);
        }
    }

    // --- Angle helpers ------------------------------------------------------

    /// <summary>
    /// Shortest signed angular difference from <paramref name="from"/> to
    /// <paramref name="to"/>, in the range (-pi, pi]. Replaces Godot's
    /// <c>angle_difference</c>.
    /// </summary>
    /// <remarks>
    /// Naively subtracting would report a ship that has wrapped past pi as being almost a
    /// full turn away, which makes the reconciler yank it the long way round.
    /// </remarks>
    internal static float AngleDifference(float from, float to)
    {
        var difference = (to - from) % MathF.Tau;
        return ((2.0f * difference) % MathF.Tau) - difference;
    }

    /// <summary>
    /// Interpolates between two angles the short way round. Replaces Godot's
    /// <c>lerp_angle</c>.
    /// </summary>
    internal static float LerpAngle(float from, float to, float weight)
        => from + (AngleDifference(from, to) * weight);
}
