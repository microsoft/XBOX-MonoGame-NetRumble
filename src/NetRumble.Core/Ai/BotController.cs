using System.Numerics;
using NetRumble.Core.Objects;
using NetRumble.Core.Tuning;

namespace NetRumble.Core.Ai;

/// <summary>
/// Drives one NPC opponent in a practice match. Ported from
/// <c>scripts/gameplay/ai/bot_controller.gd</c>.
/// </summary>
/// <remarks>
/// <para>
/// A bot is a completely ordinary <see cref="Ship"/> whose <see cref="ShipInput"/> is
/// filled in by this class instead of by a keyboard or a remote peer. Everything
/// downstream - movement, weapon cooldowns, damage, scoring, respawn - runs through
/// exactly the same code paths a human player's ship does, so bots cannot special-case
/// the simulation.
/// </para>
/// <para>
/// Bots exist only on the authority, and practice is offline, so nothing here is ever
/// replicated. Input is submitted through <see cref="Ship.UpdateRemoteInput"/> rather than
/// written onto the ship directly, because that is the same door remote players come
/// through: it clamps the vectors and refreshes the idle timeout that would otherwise zero
/// the bot's input a second after it started flying.
/// </para>
/// <para>
/// The behaviour is a small steering blend, evaluated fresh every tick:
/// </para>
/// <code>
/// engage    hold a ring around the target, closing or backing off to reach it
/// orbit     circle at that range rather than sitting still in front of the guns
/// avoid     push away from asteroids on a collision course, weighted by closeness
/// contain   turn back before reaching the barrier
/// </code>
/// <para>
/// Avoidance is weighted heavily enough to override engagement, so a bot will break off
/// an attack run to get out of the way of a rock rather than fly into it - which matters
/// far more now that asteroids split and the field fills with debris.
/// </para>
/// </remarks>
public sealed class BotController
{
    /// <summary>Engagement ring the bot tries to hold, in world units.</summary>
    public const float EngageRangeMin = 220.0f;

    public const float EngageRangeMax = 460.0f;

    /// <summary>
    /// Beyond this the bot stops shooting and just closes the distance; a laser's flight
    /// time past here makes leading guesswork and the shots only litter the arena.
    /// </summary>
    public const float FireRange = 620.0f;

    /// <summary>
    /// How far ahead the bot looks for asteroids, and how much wider than the two radii a
    /// rock has to clear before it stops being treated as a threat.
    /// </summary>
    public const float AvoidLookahead = 260.0f;

    public const float AvoidMargin = 46.0f;

    /// <summary>
    /// Weight of the avoidance vector relative to the engagement vector. Above 1.0 so a
    /// rock on a collision course always wins the argument.
    /// </summary>
    public const float AvoidWeight = 2.4f;

    /// <summary>
    /// Distance from the barrier at which the bot starts turning back, and the weight of
    /// that correction.
    /// </summary>
    public const float ContainMargin = 180.0f;

    public const float ContainWeight = 2.0f;

    /// <summary>
    /// Seconds between target re-evaluations. Re-picking every frame makes a bot dither
    /// between two equidistant enemies instead of committing to one.
    /// </summary>
    public const float RetargetInterval = 0.8f;

    /// <summary>Seconds between orbit flips, so bots do not settle into a fixed circle.</summary>
    public const float OrbitFlipInterval = 3.5f;

    /// <summary>
    /// Aim error, in radians, applied as a slowly wandering offset rather than per-frame
    /// noise - jitter that changes every frame averages out and reads as perfect aim.
    /// </summary>
    public const float AimErrorMax = 0.075f;

    public const float AimWanderRate = 1.4f;

    /// <summary>
    /// Seconds a bot waits before reacting to a target it has only just acquired, so it
    /// does not open fire the instant a player respawns in front of it.
    /// </summary>
    public const float ReactionDelay = 0.35f;

    private readonly World _world;
    private readonly Ship _ship;

    private Ship? _target;
    private float _retargetTimer;
    private float _orbitTimer;
    private float _orbitSign;
    private float _reactionTimer;
    private float _aimPhase;
    private float _aimError;
    private int _sequence;

    /// <summary>
    /// Creates a controller for one ship. The timers start at random offsets so a field of
    /// bots does not retarget and flip its orbits in lockstep.
    /// </summary>
    public BotController(World world, Ship ship, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);

        _world = world ?? throw new ArgumentNullException(nameof(world));
        _ship = ship ?? throw new ArgumentNullException(nameof(ship));

        _orbitSign = random.NextDouble() < 0.5 ? 1.0f : -1.0f;
        _orbitTimer = (float)random.NextDouble() * OrbitFlipInterval;
        _retargetTimer = (float)random.NextDouble() * RetargetInterval;
        _aimPhase = (float)random.NextDouble() * MathF.Tau;
    }

    /// <summary>The ship this controller drives.</summary>
    public Ship Ship => _ship;

    public void Tick(float delta)
    {
        if (!_ship.IsActive || _ship.Health <= 0.0f)
        {
            // A dead bot is waiting on MatchDirector's respawn timer. Zeroing the input
            // means it does not come back still thrusting in whatever direction it died
            // facing, which looks like the corpse flying away from its own wreck.
            Submit(Vector2.Zero, Vector2.Zero);
            _target = null;
            _reactionTimer = ReactionDelay;
            return;
        }

        UpdateTarget(delta);
        UpdateAimError(delta);

        Submit(Steer(delta), Aim(delta));
    }

    // --- Targeting ----------------------------------------------------------

    private void UpdateTarget(float delta)
    {
        _retargetTimer -= delta;

        if (_target is not null && (!_target.IsActive || _target.Health <= 0.0f))
        {
            _target = null;
        }

        if (_target is not null && _retargetTimer > 0.0f)
        {
            return;
        }

        _retargetTimer = RetargetInterval;
        var previous = _target;
        _target = NearestEnemy();

        if (_target != previous)
        {
            _reactionTimer = ReactionDelay;
        }
    }

    private Ship? NearestEnemy()
    {
        Ship? best = null;
        var bestDistance = float.PositiveInfinity;

        foreach (var candidate in _world.Ships.Values)
        {
            if (candidate == _ship || !candidate.IsActive || candidate.Health <= 0.0f)
            {
                continue;
            }

            var distance = (candidate.Position - _ship.Position).LengthSquared();

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best;
    }

    // --- Steering -----------------------------------------------------------

    /// <summary>
    /// Blends engagement, orbiting, asteroid avoidance and barrier containment into the
    /// single direction vector a ship's "left stick" expects.
    /// </summary>
    private Vector2 Steer(float delta)
    {
        var steering = Engage(delta)
            + (AvoidAsteroids() * AvoidWeight)
            + (Contain() * ContainWeight);

        return IsZeroApprox(steering) ? Vector2.Zero : Vector2.Normalize(steering);
    }

    private Vector2 Engage(float delta)
    {
        if (_target is null)
        {
            // Nothing to fight: drift towards the middle so idle bots do not pile up in a
            // corner where the player never meets them.
            return TowardCenter() * 0.35f;
        }

        _orbitTimer -= delta;

        if (_orbitTimer <= 0.0f)
        {
            _orbitTimer = OrbitFlipInterval;
            _orbitSign = -_orbitSign;
        }

        var toTarget = _target.Position - _ship.Position;
        var distance = toTarget.Length();

        if (distance <= 0.001f)
        {
            return Vector2.Zero;
        }

        var direction = toTarget / distance;
        var orbit = new Vector2(-direction.Y, direction.X) * _orbitSign;

        if (distance > EngageRangeMax)
        {
            // Close, but keep a little sideways drift so the approach is not a straight
            // line down the target's guns.
            return direction + (orbit * 0.25f);
        }

        return distance < EngageRangeMin
            ? -direction + (orbit * 0.6f)
            : orbit + (direction * 0.15f);
    }

    /// <summary>
    /// Sums a push away from every asteroid the bot is closing on. Rocks are weighted by
    /// how little clearance is left rather than by raw distance, so a large asteroid is
    /// given the wider berth it needs.
    /// </summary>
    private Vector2 AvoidAsteroids()
    {
        var avoidance = Vector2.Zero;
        var travel = _ship.Velocity;
        var speed = travel.Length();

        var heading = speed > 1.0f
            ? travel / speed
            : new Vector2(MathF.Sin(_ship.Rotation), -MathF.Cos(_ship.Rotation));

        foreach (var asteroid in _world.Asteroids.Values)
        {
            if (!asteroid.IsActive)
            {
                continue;
            }

            var offset = asteroid.Position - _ship.Position;
            var distance = offset.Length();
            var clearance = distance - asteroid.Radius - _ship.Radius - AvoidMargin;

            if (clearance >= AvoidLookahead || distance <= 0.001f)
            {
                continue;
            }

            var direction = offset / distance;

            // Weight by remaining clearance: touching is 1.0, a lookahead away is 0.0.
            var urgency = 1.0f - Math.Clamp(clearance / AvoidLookahead, 0.0f, 1.0f);

            // Rocks the bot is actually heading into matter more than ones beside or
            // behind it, but never zero - an asteroid drifting into the bot is still a
            // problem even when the bot is flying the other way.
            var approach = 0.35f + (0.65f * MathF.Max(Vector2.Dot(heading, direction), 0.0f));

            // Steering sideways rather than straight backwards keeps the bot moving; a
            // pure reversal has it bounce off the rock's approach vector indefinitely.
            var sidestep = new Vector2(-direction.Y, direction.X);

            if (Vector2.Dot(sidestep, heading) < 0.0f)
            {
                sidestep = -sidestep;
            }

            avoidance += ((-direction * 0.7f) + (sidestep * 0.7f)) * urgency * urgency * approach;
        }

        return avoidance;
    }

    /// <summary>
    /// Turns the bot back before it reaches the barrier. Hitting the wall is survivable -
    /// the body simply bounces - but a bot pinned against it is a sitting target.
    /// </summary>
    /// <remarks>
    /// The Godot version asked the <c>Barrier</c> node for its four edges. There is no such
    /// node here; the arena is the collision world's bounds, which run from the origin to
    /// <c>World.WorldWidth</c> by <c>World.WorldHeight</c>.
    /// </remarks>
    private Vector2 Contain()
    {
        var position = _ship.Position;
        var push = Vector2.Zero;

        push.X += MathF.Max(0.0f, 1.0f - (position.X / ContainMargin));
        push.X -= MathF.Max(0.0f, 1.0f - ((_world.WorldWidth - position.X) / ContainMargin));
        push.Y += MathF.Max(0.0f, 1.0f - (position.Y / ContainMargin));
        push.Y -= MathF.Max(0.0f, 1.0f - ((_world.WorldHeight - position.Y) / ContainMargin));

        return push;
    }

    private Vector2 TowardCenter()
    {
        var center = new Vector2(_world.WorldWidth * 0.5f, _world.WorldHeight * 0.5f);
        var offset = center - _ship.Position;

        return offset.LengthSquared() < 1.0f ? Vector2.Zero : Vector2.Normalize(offset);
    }

    // --- Shooting -----------------------------------------------------------

    /// <summary>
    /// A unit aim vector when the bot should shoot, zero otherwise. The ship's weapon code
    /// squares this against its fire threshold, so a unit vector fires and a zero vector
    /// holds.
    /// </summary>
    private Vector2 Aim(float delta)
    {
        if (_target is null)
        {
            return Vector2.Zero;
        }

        _reactionTimer = MathF.Max(_reactionTimer - delta, 0.0f);

        if (_reactionTimer > 0.0f)
        {
            return Vector2.Zero;
        }

        var toTarget = _target.Position - _ship.Position;
        var distance = toTarget.Length();

        if (distance > FireRange)
        {
            return Vector2.Zero;
        }

        var aim = LeadTarget(toTarget);

        // Never shoot through a rock. Bots that do simply feed the asteroid field and
        // never land a hit, which reads as them ignoring the player entirely.
        if (IsZeroApprox(aim) || ShotIsBlocked(aim, distance))
        {
            return Vector2.Zero;
        }

        return Rotate(aim, _aimError);
    }

    /// <summary>
    /// First-order intercept: aim at where the target will be once the laser gets there.
    /// </summary>
    /// <remarks>
    /// Solved iteratively rather than with the quadratic because two passes converge well
    /// inside the aim error the bot deliberately carries anyway. The muzzle speed comes
    /// from the same tuning the projectiles use, so a designer retuning the weapon retunes
    /// the aim with it.
    /// </remarks>
    private Vector2 LeadTarget(Vector2 toTarget)
    {
        var muzzleSpeed = TuningLibrary.Laser.Velocity;

        if (muzzleSpeed <= 0.0f)
        {
            return IsZeroApprox(toTarget) ? Vector2.Zero : Vector2.Normalize(toTarget);
        }

        var relativeVelocity = _target!.Velocity - _ship.Velocity;
        var intercept = toTarget;

        for (var pass = 0; pass < 2; pass++)
        {
            var flightTime = intercept.Length() / muzzleSpeed;
            intercept = toTarget + (relativeVelocity * flightTime);
        }

        return IsZeroApprox(intercept) ? Vector2.Zero : Vector2.Normalize(intercept);
    }

    /// <summary>
    /// Ray-versus-circle against every live asteroid, limited to the segment between the
    /// bot and its target so rocks behind the target are ignored.
    /// </summary>
    private bool ShotIsBlocked(Vector2 direction, float distance)
    {
        foreach (var asteroid in _world.Asteroids.Values)
        {
            if (!asteroid.IsActive)
            {
                continue;
            }

            var offset = asteroid.Position - _ship.Position;
            var along = Vector2.Dot(offset, direction);

            if (along <= 0.0f || along >= distance)
            {
                continue;
            }

            // The 2D cross product: for a unit direction this is the perpendicular
            // distance from the rock's centre to the line of fire.
            var perpendicular = MathF.Abs((offset.X * direction.Y) - (offset.Y * direction.X));

            if (perpendicular < asteroid.Radius + _ship.Radius)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Wanders the aim error along a sine rather than re-rolling it per frame: per-frame
    /// noise averages out over a burst and the bot ends up shooting perfectly straight.
    /// </summary>
    private void UpdateAimError(float delta)
    {
        _aimPhase = (_aimPhase + (AimWanderRate * delta)) % MathF.Tau;
        _aimError = MathF.Sin(_aimPhase) * AimErrorMax;
    }

    // --- Input submission ---------------------------------------------------

    private void Submit(Vector2 movement, Vector2 fire)
        => _ship.UpdateRemoteInput(movement, fire, false, ++_sequence);

    private static Vector2 Rotate(Vector2 value, float radians)
    {
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        return new Vector2((value.X * cos) - (value.Y * sin), (value.X * sin) + (value.Y * cos));
    }

    /// <remarks>
    /// Godot's <c>Vector2.is_zero_approx</c> tests each component against its epsilon of
    /// 1e-6, which is not the same as testing the vector's length, so it is reproduced
    /// component-wise here.
    /// </remarks>
    private static bool IsZeroApprox(Vector2 value)
        => MathF.Abs(value.X) < 1e-6f && MathF.Abs(value.Y) < 1e-6f;
}
