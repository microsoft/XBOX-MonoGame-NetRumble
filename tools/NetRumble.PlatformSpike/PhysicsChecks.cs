using System.Numerics;
using NetRumble.Core;
using NetRumble.Core.Objects;
using NetRumble.Core.Simulation;

/// <summary>
/// Behavioural checks for <see cref="CircleCollisionWorld"/>.
/// </summary>
/// <remarks>
/// The solver replaces Godot's physics server, so nothing upstream will catch a
/// regression in it. These assert the properties the gameplay actually depends on:
/// conservation under an elastic impulse, contact callbacks firing on the entry edge
/// only, layer filtering, and the wall constraint.
/// </remarks>
internal static class PhysicsChecks
{
    public static void Run()
    {
        Console.WriteLine("[6] Collision solver");

        EqualMassHeadOnSwapsVelocity();
        ContactFiresOncePerEntry();
        LayerFilteringIsOrBased();
        ExemptionSuppressesContact();
        WallsReflectAndContain();
        MomentumIsConserved();

        Console.WriteLine();
    }

    /// <summary>
    /// Two equal masses meeting head on must exchange velocities exactly. This is the
    /// textbook result for a perfectly elastic collision and the whole reason asteroids
    /// feel right.
    /// </summary>
    private static void EqualMassHeadOnSwapsVelocity()
    {
        var world = new CircleCollisionWorld(10_000, 10_000);

        var a = new TestBody(1, mass: 10.0f, radius: 20.0f)
        {
            Position = new Vector2(5000, 5000),
            Velocity = new Vector2(100, 0),
        };

        var b = new TestBody(2, mass: 10.0f, radius: 20.0f)
        {
            Position = new Vector2(5039, 5000),
            Velocity = new Vector2(-100, 0),
        };

        world.Step([a, b], 1.0f / 60.0f);

        Check(
            "equal masses swap velocity head on",
            Approx(a.Velocity.X, -100.0f, 0.5f) && Approx(b.Velocity.X, 100.0f, 0.5f));
    }

    /// <summary>
    /// Godot's <c>body_entered</c> is an edge, not a level. If this regresses, collision
    /// damage silently multiplies by the frame rate.
    /// </summary>
    private static void ContactFiresOncePerEntry()
    {
        var world = new CircleCollisionWorld(10_000, 10_000);

        // Overlapping and stationary: they stay in contact across many steps.
        var a = new TestBody(1, mass: 10.0f, radius: 20.0f) { Position = new Vector2(5000, 5000) };
        var b = new TestBody(2, mass: 10.0f, radius: 20.0f) { Position = new Vector2(5010, 5000) };

        for (var i = 0; i < 10; i++)
        {
            world.Step([a, b], 1.0f / 60.0f);
        }

        Check($"persistent overlap raises 1 contact, not 10 (got {a.Contacts})", a.Contacts == 1);

        // Separate them, let a step pass, then overlap again: that is a second entry.
        a.Position = new Vector2(0, 0);
        world.Step([a, b], 1.0f / 60.0f);
        a.Position = new Vector2(5010, 5000);
        world.Step([a, b], 1.0f / 60.0f);

        Check($"re-entry raises a new contact (got {a.Contacts})", a.Contacts == 2);
    }

    /// <summary>
    /// Power-ups mask against ships but ships do not mask against pickups, so an AND
    /// would make every power-up in the game uncollectable.
    /// </summary>
    private static void LayerFilteringIsOrBased()
    {
        var world = new CircleCollisionWorld(10_000, 10_000);

        var ship = new TestBody(1, 32.0f, 24.0f, CollisionLayer.Ships,
            CollisionLayer.Ships | CollisionLayer.Asteroids | CollisionLayer.Walls)
        {
            Position = new Vector2(5000, 5000),
        };

        var pickup = new TestBody(2, 0.001f, 20.0f, CollisionLayer.Pickups, CollisionLayer.Ships)
        {
            Position = new Vector2(5020, 5000),
        };

        world.Step([ship, pickup], 1.0f / 60.0f);
        Check("ship contacts power-up despite asymmetric masks", ship.Contacts == 1);

        // A pickup and an asteroid share no mask in either direction.
        var world2 = new CircleCollisionWorld(10_000, 10_000);

        var asteroid = new TestBody(3, 30.0f, 60.0f, CollisionLayer.Asteroids,
            CollisionLayer.Ships | CollisionLayer.Asteroids | CollisionLayer.Walls)
        {
            Position = new Vector2(5000, 5000),
        };

        var pickup2 = new TestBody(4, 0.001f, 20.0f, CollisionLayer.Pickups, CollisionLayer.Ships)
        {
            Position = new Vector2(5020, 5000),
        };

        world2.Step([asteroid, pickup2], 1.0f / 60.0f);
        Check("asteroid ignores power-up", asteroid.Contacts == 0);
    }

    /// <summary>Laser volleys rely on this to avoid blowing their own spread apart.</summary>
    private static void ExemptionSuppressesContact()
    {
        var world = new CircleCollisionWorld(10_000, 10_000);

        var a = new TestBody(1, 0.001f, 4.0f, CollisionLayer.Projectiles, CollisionLayer.Projectiles)
        {
            Position = new Vector2(5000, 5000),
        };

        var b = new TestBody(2, 0.001f, 4.0f, CollisionLayer.Projectiles, CollisionLayer.Projectiles)
        {
            Position = new Vector2(5001, 5000),
        };

        a.AddCollisionException(b);
        world.Step([a, b], 1.0f / 60.0f);

        Check("exempted pair does not contact or depenetrate",
            a.Contacts == 0 && Approx(a.Position.X, 5000.0f, 0.001f));

        // Clearing must lift the exemption from BOTH sides. The solver orders pairs by id
        // and only consults the lower body's set, so a projectile that cleared only its own
        // would stay permanently exempted on the lower-id ship that fired it - and since the
        // pool is shared, that projectile could never hit that ship again all match.
        b.ClearCollisionExceptions();

        Check("clearing on one body lifts the exemption on the other",
            !a.IsExemptFrom(b) && !b.IsExemptFrom(a));

        var world2 = new CircleCollisionWorld(10_000, 10_000);
        a.Position = new Vector2(5000, 5000);
        b.Position = new Vector2(5001, 5000);
        world2.Step([a, b], 1.0f / 60.0f);

        Check($"recycled body collides again (got {a.Contacts})", a.Contacts == 1);
    }

    private static void WallsReflectAndContain()
    {
        var world = new CircleCollisionWorld(2400, 2400);

        // Fast enough to genuinely breach the boundary in one step. At -600 the body
        // lands exactly tangent (x - r == 0), which is not penetration and correctly
        // raises nothing.
        var body = new TestBody(1, 10.0f, 20.0f, CollisionLayer.Ships, CollisionLayer.Walls)
        {
            Position = new Vector2(30, 1200),
            Velocity = new Vector2(-1800, 0),
        };

        world.Step([body], 1.0f / 60.0f);

        Check($"wall reflects velocity (got {body.Velocity.X:F0})", body.Velocity.X > 0.0f);
        Check($"body stays inside bounds (got {body.Position.X:F1})", body.Position.X >= 20.0f - 0.001f);
        Check($"wall contact raised once (got {body.WallContacts})", body.WallContacts == 1);

        // Moving away must not re-raise the entry edge on subsequent steps.
        for (var i = 0; i < 5; i++)
        {
            world.Step([body], 1.0f / 60.0f);
        }

        Check($"receding body does not re-raise wall contact (got {body.WallContacts})", body.WallContacts == 1);

        // A body that does not mask walls must pass straight through.
        var world2 = new CircleCollisionWorld(2400, 2400);

        var pickup = new TestBody(2, 0.001f, 20.0f, CollisionLayer.Pickups, CollisionLayer.Ships)
        {
            Position = new Vector2(10, 1200),
            Velocity = new Vector2(-600, 0),
        };

        world2.Step([pickup], 1.0f / 60.0f);
        Check("non-masking body is not constrained", pickup.Position.X < 0.0f);
    }

    /// <summary>
    /// Unequal masses must still conserve momentum. A ship ramming an asteroid is the
    /// common case and it has to transfer, not teleport, energy.
    /// </summary>
    private static void MomentumIsConserved()
    {
        var world = new CircleCollisionWorld(10_000, 10_000);

        var ship = new TestBody(1, mass: 32.0f, radius: 24.0f)
        {
            Position = new Vector2(5000, 5000),
            Velocity = new Vector2(400, 0),
        };

        var asteroid = new TestBody(2, mass: 48.0f, radius: 60.0f)
        {
            Position = new Vector2(5080, 5000),
            Velocity = new Vector2(-50, 0),
        };

        var before = (ship.Mass * ship.Velocity) + (asteroid.Mass * asteroid.Velocity);
        world.Step([ship, asteroid], 1.0f / 60.0f);
        var after = (ship.Mass * ship.Velocity) + (asteroid.Mass * asteroid.Velocity);

        Check($"momentum conserved (before {before.X:F1}, after {after.X:F1})",
            Approx(before.X, after.X, 0.01f));
        Check("heavier body deflects the lighter one", ship.Velocity.X < 0.0f);
    }

    private static bool Approx(float a, float b, float tolerance) => MathF.Abs(a - b) <= tolerance;

    private static void Check(string label, bool condition)
        => Console.WriteLine($"    {(condition ? "PASS" : "FAIL")} {label}");

    /// <summary>
    /// A minimal concrete <see cref="GameObject"/>. The real entities pull in the whole
    /// world and its pools, which would make these checks test everything at once.
    /// </summary>
    private sealed class TestBody : GameObject
    {
        public TestBody(
            int id,
            float mass,
            float radius,
            CollisionLayer layer = CollisionLayer.Asteroids,
            CollisionLayer mask = CollisionLayer.Ships | CollisionLayer.Asteroids | CollisionLayer.Walls)
        {
            UniqueId = id;
            Mass = mass;
            Radius = radius;
            SetCollisionFilter(layer, mask);
        }

        public int Contacts { get; private set; }

        public int WallContacts { get; private set; }

        public override void OnContact(GameObject other) => Contacts++;

        public override void OnWallContact() => WallContacts++;
    }
}
