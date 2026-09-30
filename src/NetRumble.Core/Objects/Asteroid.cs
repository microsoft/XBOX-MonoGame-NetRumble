using System.Numerics;
using NetRumble.Core.Simulation;
using NetRumble.Core.Tuning;

namespace NetRumble.Core.Objects;

/// <summary>
/// A drifting obstacle that bounces off ships, other asteroids and the barrier, takes
/// weapon damage and breaks apart. Ported from
/// <c>scripts/gameplay/objects/asteroid.gd</c>.
/// </summary>
/// <remarks>
/// <para>
/// Rocks run through five size tiers and split into fragments of the tier below (see
/// <see cref="AsteroidTuning.SplitSizeFor"/>) until <see cref="AsteroidSize.Tiny"/>,
/// which is destroyed outright. The split itself is <see cref="World"/>'s job: this
/// class only drives health to zero and <c>World.DetectSplitAsteroids</c> picks it up at
/// the end of the tick.
/// </para>
/// <para>
/// In the Godot version, movement, bouncing and elastic collision were all handled
/// by the <c>RigidBody2D</c> + <c>PhysicsMaterial</c> authored in
/// <c>asteroid.tscn</c>; the script only owned the gameplay consequences. Here
/// <see cref="Simulation.CircleCollisionWorld"/> plays that role for bounce and wall
/// contact, but it has no per-body linear damping, so the velocity decay that used
/// to come from the body's <c>linear_damp</c> is applied explicitly in
/// <see cref="Tick"/> instead - see the "JUDGEMENT CALLS" note in the porting report.
/// </para>
/// </remarks>
public sealed class Asteroid : GameObject
{
    /// <summary>
    /// Designer-tunable stats, overridable per-instance like the Godot
    /// <c>@export</c> field.
    /// </summary>
    public AsteroidTuning Tuning { get; set; } = TuningLibrary.Asteroid;

    public AsteroidSize AsteroidSize { get; private set; } = AsteroidSize.Small;

    /// <summary>Which of <see cref="AsteroidTuning.Textures"/> this instance uses.</summary>
    public int Variation { get; private set; }

    /// <summary>
    /// Unique id of the last object to damage this rock, mirroring the same field on
    /// <see cref="Ship"/>. <see cref="World"/> reads it when the rock breaks apart, to
    /// work out whose shot did it.
    /// </summary>
    public int LastDamagedById { get; private set; }

    public Asteroid()
    {
        ObjectType = GameObjectType.Asteroid;
        SetCollisionFilter(
            CollisionLayer.Asteroids,
            CollisionLayer.Ships | CollisionLayer.Asteroids | CollisionLayer.Walls);
    }

    /// <summary>
    /// Authority-side construction: rolls size-derived stats, a random spin, a random
    /// initial velocity and a random texture variation.
    /// </summary>
    /// <param name="size">The asteroid's size class.</param>
    /// <param name="random">
    /// The world's deterministic random source. Passed in explicitly rather than
    /// using a shared/global instance because spawn rolls happen only on the
    /// authority and must be reproducible from its own seeded stream, not from a
    /// source shared with anything else.
    /// </param>
    public void Setup(AsteroidSize size, Random random)
    {
        UniqueId = AllocateId();
        ApplySize(size);
        Rotation = RandRange(random, 0.0f, MathF.Tau);
        var initialVelocity = RandRange(random, Tuning.VelocityInitialMin, Tuning.VelocityInitialMax);
        Velocity = RandomDirection(random) * initialVelocity;
        Variation = random.Next(0, 3);
    }

    public void SetupNetworked(int id, AsteroidSize size, int texVariation)
    {
        UniqueId = id;
        ApplySize(size);
        Variation = texVariation;
    }

    /// <summary>
    /// Authority-side construction for a fragment thrown off by a split. The id, tier
    /// and texture are decided by <see cref="World"/> so the same values can be replayed
    /// on every client.
    /// </summary>
    public void SetupFragment(int id, AsteroidSize size, int texVariation, float rotation)
    {
        UniqueId = id;
        ApplySize(size);
        Variation = texVariation;
        Rotation = rotation;
    }

    /// <summary>
    /// The tier this asteroid breaks into, or <c>null</c> when it is the terminal tier
    /// and is simply destroyed.
    /// </summary>
    public AsteroidSize? SplitSize() => AsteroidTuning.SplitSizeFor(AsteroidSize);

    /// <summary>
    /// Weapon damage.
    /// </summary>
    /// <remarks>
    /// Reaching zero deliberately does not act here: <see cref="World"/> polls for dead
    /// asteroids once the tick's iteration has finished and performs the split then.
    /// Spawning fragments - or removing this body - from inside a contact callback would
    /// mutate the object dictionary while explosion damage is still walking it.
    /// </remarks>
    public override void TakeDamage(GameObject? source, float damage)
    {
        if (!IsActive || damage <= 0.0f || source is null || Health <= 0.0f)
        {
            return;
        }

        LastDamagedById = source.UniqueId;
        Health = MathF.Max(Health - damage, 0.0f);
    }

    private void ApplySize(AsteroidSize size)
    {
        AsteroidSize = size;
        Radius = Tuning.RadiusFor(size);
        Health = Radius * Tuning.RadiusHealthRatio;
        Mass = Radius * Tuning.RadiusMassRatio;
    }

    /// <summary>
    /// Spin is coupled to speed, so it stays script-driven; linear decay reproduces
    /// what used to be the body's <c>linear_damp</c>.
    /// </summary>
    public override void Tick(float delta)
    {
        var velocityMassRatio = Velocity.LengthSquared() / Mass;
        Rotation += velocityMassRatio * Tuning.VelocityMassRatioToRotationScalar * delta;

        // Godot applied `v *= 1 - damp * step` once per physics step, entirely outside
        // the script; folded in here since CircleCollisionWorld has no equivalent.
        Velocity *= 1.0f - (Tuning.VelocityDecayRate * delta);

        base.Tick(delta);
    }

    public override void OnContact(GameObject other)
    {
        switch (other)
        {
            case Asteroid asteroid:
                OnContactAsteroid(asteroid);
                break;
            case Ship ship:
                OnContactShip(ship);
                break;
        }
    }

    private void OnContactAsteroid(Asteroid other)
    {
        // Only one side of the pair reports the event, otherwise it would fire twice
        // per contact - once from each body's OnContact call.
        if (World is not null && UniqueId < other.UniqueId)
        {
            World.EmitGameplayEvent(GameplayEventType.AsteroidImpact, Position);
        }
    }

    /// <summary>
    /// Ramming damage uses the velocities sampled before the physics step, because
    /// the solver has already exchanged momentum by the time the contact is
    /// reported.
    /// </summary>
    private void OnContactShip(Ship ship)
    {
        var toShip = Position - ship.Position;
        var distanceSquared = toShip.LengthSquared();

        if (distanceSquared > 0.0f)
        {
            toShip *= 1.0f / MathF.Sqrt(distanceSquared);
            var asteroidSpeed = Vector2.Dot(toShip, PreStepVelocity);
            var shipSpeed = Vector2.Dot(toShip, ship.PreStepVelocity);
            var rammingSpeed = shipSpeed - asteroidSpeed;
            var momentum = Mass * rammingSpeed;
            ship.TakeDamage(this, momentum * Tuning.MomentumDamageScalar);
        }

        if (World is not null)
        {
            World.EmitGameplayEvent(GameplayEventType.AsteroidImpact, Position);
        }
    }

    /// <summary>Reimplements Godot's <c>randf_range(min, max)</c>.</summary>
    private static float RandRange(Random random, float min, float max)
        => min + ((max - min) * random.NextSingle());

    private static Vector2 RandomDirection(Random random)
    {
        var angle = RandRange(random, 0.0f, MathF.Tau);
        return new Vector2(MathF.Cos(angle), MathF.Sin(angle));
    }
}
