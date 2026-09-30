using System.Numerics;
using NetRumble.Core.Simulation;
using NetRumble.Core.Tuning;

namespace NetRumble.Core.Objects;

/// <summary>
/// The base laser projectile; <see cref="RocketProjectile"/> and
/// <see cref="MineProjectile"/> extend it. Ported from
/// <c>scripts/gameplay/objects/projectile.gd</c>.
/// </summary>
/// <remarks>
/// <para>
/// Projectiles carry a negligible <see cref="GameObject.Mass"/>, so
/// <see cref="CircleCollisionWorld"/> reports the contact without them meaningfully
/// pushing what they hit. The tuning <c>Mass</c> value itself is unused for the body
/// - it only feeds explosion-damage falloff on subclasses that detonate.
/// </para>
/// <para>
/// The owner-damage exemption uses <see cref="GameObject.AddCollisionException"/> /
/// <see cref="GameObject.ClearCollisionExceptions"/>, which replace the Godot
/// <c>add_collision_exception_with</c> plumbing on the base class; see
/// <c>GameObject.cs</c> for how the solver honours them.
/// </para>
/// </remarks>
public class Projectile : GameObject
{
    /// <summary>
    /// Projectiles are simulated bodies so they must not bounce anything: a
    /// negligible mass lets the solver report contacts while transferring no
    /// meaningful momentum.
    /// </summary>
    private const float ContactMass = 0.001f;

    /// <summary>
    /// Seconds a client waits past a shot's nominal lifetime before retiring it itself.
    /// </summary>
    /// <remarks>
    /// Long enough that the authority's detonation message wins the race in normal play,
    /// short enough that an orphaned body does not linger for the rest of the match.
    /// </remarks>
    private const float ClientExpiryGrace = 2.0f;

    private readonly List<DetonationHit> _authoritativeHits = [];

    private float _durationRemaining;
    private int _pierceRemaining;
    private int _bouncesRemaining;
    private bool _clientExpiryPending;

    /// <summary>
    /// The weapon-derived modifiers this shot was fired with.
    /// </summary>
    /// <remarks>
    /// Set by <see cref="ApplyShotSpec"/> on the authority and replicated verbatim, so a
    /// client builds an identical body without consulting its own weapon table.
    /// </remarks>
    public ShotSpec Spec { get; private set; } = ShotSpec.Default;

    /// <summary>
    /// Designer-tunable stats. Subclasses swap in their own tuning instance and call
    /// <see cref="ApplyTuning"/> again, matching the exported field the Godot version
    /// let a scene override.
    /// </summary>
    public ProjectileTuning Tuning { get; set; } = TuningLibrary.Laser;

    public ProjectileType ProjectileType { get; private set; } = ProjectileType.Laser;

    public bool CanDamageOwner { get; private set; }

    public float DamageAmount { get; private set; }

    public float DamageRadius { get; private set; }

    /// <summary>Unique id of the ship that fired this shot, or zero if none.</summary>
    public int OwnerId { get; private set; }

    public float StartingVelocity { get; private set; }

    public Projectile() : this(ProjectileType.Laser)
    {
    }

    /// <summary>Constructs a projectile pre-loaded with a specific tuning, for pooling.</summary>
    public Projectile(ProjectileType projectileType)
    {
        ObjectType = GameObjectType.Projectile;
        SetCollisionFilter(
            CollisionLayer.Projectiles,
            CollisionLayer.Ships | CollisionLayer.Asteroids | CollisionLayer.Projectiles | CollisionLayer.Walls);
        Tuning = TuningLibrary.Projectile(projectileType);
        ApplyTuning();
    }

    /// <summary>Copies the current <see cref="Tuning"/> into the body's live stats.</summary>
    protected void ApplyTuning()
    {
        ProjectileType = Tuning.ProjectileType;
        Mass = ContactMass;
        Radius = Tuning.Radius * Spec.RadiusScale;
        StartingVelocity = Tuning.Velocity * Spec.SpeedScale;
        CanDamageOwner = Tuning.CanDamageOwner;
        DamageAmount = Tuning.DamageAmount * Spec.DamageScale;
        DamageRadius = Spec.SplashRadius < 0.0f ? Tuning.DamageRadius : Spec.SplashRadius;
    }

    /// <summary>
    /// Applies the weapon-derived modifiers for this shot, then re-derives the body's
    /// stats from them.
    /// </summary>
    /// <remarks>
    /// The spec is sanitised here rather than at the call site so both paths into a live
    /// projectile - the authority firing one and a client rebuilding one from a spawn
    /// message - are covered by the same clamps. See <see cref="ShotSpec.Sanitized"/>.
    /// </remarks>
    public void ApplyShotSpec(ShotSpec spec)
    {
        Spec = spec.Sanitized();
        ApplyTuning();
    }

    public override void Start()
    {
        // Range is expressed as a lifetime scale: a shot's reach is how long it lives
        // multiplied by how fast it travels, and scaling the clock is the one way to
        // shorten a straight-line body's range without also slowing it down.
        _durationRemaining = Tuning.Duration * Spec.RangeScale;
        _pierceRemaining = Spec.Pierce;
        _bouncesRemaining = Spec.Bounces;
        _clientExpiryPending = false;
        Health = Tuning.Health;
        _authoritativeHits.Clear();
    }

    /// <summary>
    /// Runs the self-destruct countdown that replaces the Godot <c>DurationTimer</c>
    /// node.
    /// </summary>
    /// <remarks>
    /// The original timer ran on its own physics-process callback, independent of
    /// <c>tick()</c>. There is no timer primitive here, so it is folded into
    /// <see cref="Tick"/> instead; because every subclass funnels through this base
    /// call once per step, the cadence is identical.
    /// </remarks>
    public override void Tick(float delta)
    {
        if (Spec.HomingRate > 0.0f)
        {
            SteerTowardsTarget(delta);
        }

        base.Tick(delta);

        _durationRemaining -= delta;

        if (_durationRemaining > 0.0f)
        {
            return;
        }

        OnDurationExpired();
    }

    /// <summary>
    /// Turns the shot towards the nearest ship that is not its owner.
    /// </summary>
    /// <remarks>
    /// Steering is a capped rotation of the velocity vector rather than a re-aim, so a
    /// missile keeps its speed and traces a visible arc instead of snapping onto the
    /// target. Guidance runs on <b>every</b> peer, not just the authority: a client that
    /// simulated its missiles in a straight line would see them drift away from the
    /// authority's path and then teleport back on the next correction.
    /// </remarks>
    private void SteerTowardsTarget(float delta)
    {
        var target = World?.FindNearestEnemyShip(Position, OwnerId);

        if (target is null)
        {
            return;
        }

        var speed = Velocity.Length();

        if (speed <= 0.0f)
        {
            return;
        }

        var desired = Vector2.Normalize(target.Position - Position);
        var current = Velocity / speed;

        if (!float.IsFinite(desired.X) || !float.IsFinite(desired.Y))
        {
            return;
        }

        // Signed angle from current to desired, via the 2D cross and dot products.
        var cross = (current.X * desired.Y) - (current.Y * desired.X);
        var dot = Vector2.Dot(current, desired);
        var maxTurn = Spec.HomingRate * delta;
        var turn = Math.Clamp(MathF.Atan2(cross, dot), -maxTurn, maxTurn);

        var cosine = MathF.Cos(turn);
        var sine = MathF.Sin(turn);

        var steered = new Vector2(
            (current.X * cosine) - (current.Y * sine),
            (current.X * sine) + (current.Y * cosine));

        Velocity = steered * speed;
        Teleport(Position, MathF.Atan2(steered.X, -steered.Y));
    }

    /// <summary>
    /// Retires a shot that has run out its lifetime.
    /// </summary>
    /// <remarks>
    /// <see cref="Die"/> is authority-only, so on a client the authority's detonation
    /// message is the <b>only</b> thing that can retire this body. If that message is
    /// lost - or the host stops tracking the shot while the client keeps flying it -
    /// nothing else ever recycles it and the shot sits on the field for the rest of the
    /// match, visible as bullets drifting on clients that the host never showed. Wait one
    /// grace period past the nominal lifetime, so the authority still wins in normal play,
    /// then clean it up locally.
    /// </remarks>
    private void OnDurationExpired()
    {
        if (World is { IsAuthority: false })
        {
            if (!_clientExpiryPending)
            {
                _clientExpiryPending = true;
                _durationRemaining = ClientExpiryGrace;
                return;
            }

            _clientExpiryPending = false;
            World.RetireOrphanedProjectile(this);
            return;
        }

        Die();
    }

    /// <summary>
    /// Points the shot along <paramref name="direction"/> from the firing ship and
    /// marks that ship as its owner.
    /// </summary>
    public void SetOwnerAndDirection(Ship? ship, Vector2 direction)
    {
        if (ship is null)
        {
            return;
        }

        SetOwnerShip(ship);
        Velocity = direction * StartingVelocity;

        // Forward is (sin(rot), -cos(rot)), so the facing that points along the travel
        // direction is atan2(x, -y). Anything else leaves the sprite - and the rocket's
        // exhaust trail, which is emitted out of the body's local -forward - pointing
        // away from where the projectile is actually going.
        Teleport(ship.Position, MathF.Atan2(direction.X, -direction.Y));
    }

    /// <summary>
    /// Records which ship fired this shot, exempting it from the owner's collision
    /// depenetration so a shot spawned inside its own ship is not shoved off course.
    /// </summary>
    public void SetOwnerShip(Ship? ship)
    {
        ClearCollisionExceptions();

        if (ship is null)
        {
            return;
        }

        OwnerId = ship.UniqueId;
        AddCollisionException(ship);
    }

    /// <summary>Client-side activation driven by a spawn message from the authority.</summary>
    public void ActivateFromAuthority(int newOwnerId, Vector2 pos, Vector2 vel, float rot, ShotSpec spec)
    {
        SetOwnerShip(World?.GetShipById(newOwnerId));
        OwnerId = newOwnerId;

        // Before Start, because the spec scales the lifetime and pierce budget that Start
        // seeds, and the radius the collision pass will use on the very next step.
        ApplyShotSpec(spec);
        Teleport(pos, rot);
        Velocity = vel;
        IsActive = true;
        Start();
    }

    /// <summary>Client-side recycle driven by a detonation message from the authority.</summary>
    public void DeactivateFromAuthority(Vector2 pos)
    {
        Teleport(pos);
        DeactivateProjectile();
    }

    public override void OnContact(GameObject other)
    {
        switch (other)
        {
            case Ship ship:
                OnContactShip(ship);
                break;
            case Asteroid asteroid:
                OnContactAsteroid(asteroid);
                break;
            case Projectile projectile:
                OnContactProjectile(projectile);
                break;
        }
    }

    /// <summary>
    /// Projectiles die against the barrier unless the weapon that fired them bought
    /// bounces.
    /// </summary>
    /// <remarks>
    /// The barrier is four axis-aligned walls and the contact carries no normal, so the
    /// wall that was hit is inferred from which side of the world the shot has left. That
    /// is exact for an axis-aligned box and costs nothing.
    /// </remarks>
    public override void OnWallContact()
    {
        if (_bouncesRemaining <= 0 || World is null)
        {
            Die();
            return;
        }

        _bouncesRemaining--;
        var reflected = Velocity;

        if (Position.X <= 0.0f || Position.X >= World.WorldWidth)
        {
            reflected.X = -reflected.X;
        }

        if (Position.Y <= 0.0f || Position.Y >= World.WorldHeight)
        {
            reflected.Y = -reflected.Y;
        }

        // A contact reported with the shot inside the box leaves nothing to reflect, and
        // leaving it flying would let it escape the world entirely.
        if (reflected == Velocity)
        {
            Die();
            return;
        }

        Velocity = reflected;
        Teleport(Position, MathF.Atan2(reflected.X, -reflected.Y));
    }

    /// <summary>
    /// Spends one of the shot's lives on the thing it just hit.
    /// </summary>
    /// <remarks>
    /// A piercing shot has to stop colliding with what it passed through, or the solver
    /// keeps handing back the same contact while the two bodies remain overlapped and the
    /// beam is consumed several times over by a single target.
    /// </remarks>
    private void ConsumeHit(GameObject target)
    {
        if (_pierceRemaining <= 0)
        {
            Die();
            return;
        }

        _pierceRemaining--;
        AddCollisionException(target);

        if (World is { IsAuthority: true })
        {
            World.EmitGameplayEvent(GameplayEventType.LaserImpact, Position);
        }
    }

    private void OnContactShip(Ship ship)
    {
        if (ship.UniqueId == OwnerId)
        {
            return;
        }

        if (World is { IsAuthority: true })
        {
            ship.TakeDamage(this, DamageAmount);
            RecordAuthoritativeHit(ship);
        }

        ConsumeHit(ship);
    }

    /// <summary>
    /// Asteroids used to be indestructible scenery that shots simply died against. They
    /// now take damage and break apart, so a hit is recorded the same way a ship hit is:
    /// the authority applies the damage and the resulting health rides the detonation
    /// message, keeping clients in step until the split itself is announced.
    /// </summary>
    private void OnContactAsteroid(Asteroid asteroid)
    {
        if (World is { IsAuthority: true })
        {
            asteroid.TakeDamage(this, DamageAmount);
            RecordAuthoritativeHit(asteroid);
        }

        ConsumeHit(asteroid);
    }

    private void OnContactProjectile(Projectile other)
    {
        if (OwnerId == other.OwnerId)
        {
            return;
        }

        if (World is { IsAuthority: true })
        {
            other.TakeDamage(this, DamageAmount);
            RecordAuthoritativeHit(other);
        }

        ConsumeHit(other);
    }

    public override void TakeDamage(GameObject? source, float damage)
    {
        if (!IsActive || damage <= 0.0f)
        {
            return;
        }

        Health = MathF.Max(Health - damage, 0.0f);

        if (Health <= 0.0f)
        {
            Die();
        }
    }

    public override void Die()
    {
        if (!IsActive || World is null || !World.IsAuthority)
        {
            return;
        }

        var pos = Position;
        DeactivateProjectile();
        World.NotifyProjectileDetonated(this, pos, _authoritativeHits);
        _authoritativeHits.Clear();
    }

    /// <summary>
    /// The <see cref="Projectile"/>-level recycle path: stops the duration countdown
    /// and hands off to the plain <see cref="GameObject.Deactivate"/>. Exposed to
    /// subclasses because <see cref="MineProjectile"/> and
    /// <see cref="RocketProjectile"/> both detonate through their own paths that
    /// still need this cleanup.
    /// </summary>
    protected void DeactivateProjectile()
    {
        _durationRemaining = 0.0f;
        Deactivate();
    }

    private void RecordAuthoritativeHit(GameObject obj)
    {
        if (World is { IsAuthority: true })
        {
            _authoritativeHits.Add(World.CreateDetonationHit(obj));
        }
    }
}
