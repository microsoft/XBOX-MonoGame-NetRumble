using System.Numerics;
using NetRumble.Core.Simulation;

namespace NetRumble.Core.Objects;

/// <summary>
/// Base class for every simulated entity, ported from
/// <c>scripts/gameplay/objects/game_object.gd</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Coordinate convention</b>, carried over unchanged so the netcode and the tuning
/// stay valid: y is down, rotation increases clockwise, and rotation 0 points up, so
/// forward is <c>(sin(rot), -cos(rot))</c>. The ship art is drawn nose-up, which is why
/// no sprite rotation offset is needed anywhere.
/// </para>
/// <para>
/// <b>What was dropped.</b> The Godot version was a <c>RigidBody2D</c> and inherited a
/// long tail of workarounds for the physics server owning the transform:
/// <c>teleport()</c> and <c>set_facing()</c> pushing state through
/// <c>PhysicsServer2D</c>, the deferred <c>freeze</c> handling that had to save and
/// restore velocity, and clearing collision layers instead of disabling shapes so the
/// server would not drop the body. None of that has an analogue here -
/// <c>CircleCollisionWorld</c> reads these fields directly and owns nothing - so
/// position and rotation are now plain assignments.
/// </para>
/// </remarks>
public abstract class GameObject
{
    private static int _nextUniqueId;

    public GameObjectType ObjectType { get; protected set; } = GameObjectType.Unknown;

    public int UniqueId { get; set; }

    public float Health { get; set; } = 1.0f;

    public Vector2 Position;

    public Vector2 Velocity;

    /// <summary>Facing, in radians. Rotation 0 is up; increasing is clockwise.</summary>
    public float Rotation;

    /// <summary>
    /// Velocity sampled before the current physics step.
    /// </summary>
    /// <remarks>
    /// Contact handlers need the approach speed to compute momentum damage, and by the
    /// time contacts are dispatched the solver has already applied the bounce. Written
    /// once per step by the collision world.
    /// </remarks>
    public Vector2 PreStepVelocity;

    public float Mass { get; set; } = 1.0f;

    /// <summary>Collision radius.</summary>
    public float Radius { get; set; }

    public CollisionLayer CollisionLayer { get; protected set; }

    public CollisionLayer CollisionMask { get; protected set; }

    public World? World { get; private set; }

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Cleared by the match director between match phases so bodies hold still without
    /// being deactivated.
    /// </summary>
    public bool SimulationRunning { get; set; } = true;

    /// <summary>True when this body should be integrated and collided this step.</summary>
    public bool IsSimulating => IsActive && SimulationRunning;

    /// <summary>Unit vector this object is facing.</summary>
    public Vector2 Forward => new(MathF.Sin(Rotation), -MathF.Cos(Rotation));

    /// <remarks>
    /// Exact comparison against zero, matching the GDScript. Health is only ever assigned
    /// a literal zero on death, never decremented to it, so this cannot miss.
    /// </remarks>
    public bool IsDead => Health == 0.0f;

    /// <summary>
    /// Allocates a replication id.
    /// </summary>
    /// <remarks>
    /// Ids are allocated by the authority and replicated, so this counter only advances on
    /// the host. Client objects are stamped with the id carried in the spawn payload.
    /// </remarks>
    public static int AllocateId() => ++_nextUniqueId;

    /// <summary>Resets the id counter, for match teardown.</summary>
    public static void ResetIdCounter() => _nextUniqueId = 0;

    public void SetWorld(World world) => World = world;

    /// <summary>Repositions the body, optionally reorienting it.</summary>
    public void Teleport(Vector2 to, float? newRotation = null)
    {
        Position = to;

        if (newRotation.HasValue)
        {
            Rotation = newRotation.Value;
        }
    }

    public virtual void Start()
    {
    }

    /// <summary>
    /// Per-frame gameplay update, run before the physics step.
    /// </summary>
    /// <remarks>
    /// Unlike the Godot original this does <em>not</em> sample
    /// <see cref="PreStepVelocity"/>. The collision world does that, so the sampling
    /// cannot drift out of step if an override forgets to call the base implementation.
    /// </remarks>
    public virtual void Tick(float delta)
    {
    }

    /// <summary>
    /// Called once per contact with another simulated entity. Momentum exchange is the
    /// collision world's job; overrides only add gameplay consequences.
    /// </summary>
    public virtual void OnContact(GameObject other)
    {
    }

    /// <summary>
    /// Called when this entity touches the barrier. Bodies that mask against walls bounce
    /// automatically, so only projectiles override this.
    /// </summary>
    public virtual void OnWallContact()
    {
    }

    public virtual void TakeDamage(GameObject? source, float damage)
    {
    }

    public virtual void Die() => Deactivate();

    /// <summary>
    /// The <see cref="GameObject"/>-level death used by projectile detonation results and
    /// by the projectile subclasses, which override <see cref="Die"/> but still need the
    /// plain recycle path.
    /// </summary>
    protected void Deactivate()
    {
        if (World is null || !IsActive)
        {
            return;
        }

        IsActive = false;
        Health = 0.0f;
        World.DestroyGameObjectById(UniqueId);
    }

    public GameObjectSnapshot Serialize() => new()
    {
        Type = ObjectType,
        Id = UniqueId,
        Position = Position,
        Velocity = Velocity,
        Mass = Mass,
        Radius = Radius,
        Rotation = Rotation,
        Health = Health,
        IsActive = IsActive,
    };

    public virtual void ApplySnapshot(in GameObjectSnapshot snapshot)
    {
        Position = snapshot.Position;
        Rotation = snapshot.Rotation;
        Velocity = snapshot.Velocity;
        Health = snapshot.Health;
    }

    /// <summary>Called when <see cref="SimulationRunning"/> changes.</summary>
    public virtual void OnSimulationRunningChanged(bool running)
    {
    }

    /// <summary>Sets the collision filtering for this body type.</summary>
    protected void SetCollisionFilter(CollisionLayer layer, CollisionLayer mask)
    {
        CollisionLayer = layer;
        CollisionMask = mask;
    }

    private HashSet<int>? _collisionExceptions;

    /// <summary>
    /// The bodies this one has exempted, kept so the exemption can be withdrawn from
    /// <em>their</em> sets too. Ids alone are not enough: clearing needs the object.
    /// </summary>
    private List<GameObject>? _collisionExceptionPartners;

    /// <summary>
    /// Suppresses collision between this body and another, replacing Godot's
    /// <c>add_collision_exception_with</c>.
    /// </summary>
    /// <remarks>
    /// Used for laser volleys: shots in a spread leave the muzzle overlapping each other,
    /// and without an exemption the solver depenetrates them and sprays the spread apart.
    /// Exemptions are recorded on both bodies so the solver only has to test one of them.
    /// </remarks>
    public void AddCollisionException(GameObject other)
    {
        if (ReferenceEquals(other, this))
        {
            return;
        }

        if ((_collisionExceptions ??= []).Add(other.UniqueId))
        {
            (_collisionExceptionPartners ??= []).Add(other);
        }

        if ((other._collisionExceptions ??= []).Add(UniqueId))
        {
            (other._collisionExceptionPartners ??= []).Add(this);
        }
    }

    /// <summary>
    /// Drops every exemption involving this body, on both sides of each pair.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Must be called when a pooled body is recycled. Ids are reused across a match, so a
    /// stale exemption would otherwise make a fresh projectile silently pass through an
    /// unrelated object.
    /// </para>
    /// <para>
    /// <b>Clearing the partner's set is the load-bearing half.</b> Only the recycled body
    /// (always a projectile) ever has this called on it, but the solver tests the exemption
    /// on the lower-id body of the pair, and ships are always allocated before projectiles.
    /// Clearing one side only would leave the projectile's id permanently exempted on every
    /// ship that ever fired it, and since the pool is shared, that projectile could never
    /// hit that ship again for the rest of the match.
    /// </para>
    /// </remarks>
    public void ClearCollisionExceptions()
    {
        if (_collisionExceptionPartners is not null)
        {
            foreach (var partner in _collisionExceptionPartners)
            {
                partner._collisionExceptions?.Remove(UniqueId);
                partner._collisionExceptionPartners?.Remove(this);
            }

            _collisionExceptionPartners.Clear();
        }

        _collisionExceptions?.Clear();
    }

    public bool IsExemptFrom(GameObject other)
        => _collisionExceptions is not null && _collisionExceptions.Contains(other.UniqueId);
}
