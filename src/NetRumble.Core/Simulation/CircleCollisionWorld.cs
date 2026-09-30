using System.Numerics;
using NetRumble.Core.Objects;

namespace NetRumble.Core.Simulation;

/// <summary>
/// The physics solver, replacing Godot's 2D physics server.
/// </summary>
/// <remarks>
/// <para>
/// Every collider in NetRumble is a circle, gravity is off and rotation is locked, so a
/// general-purpose physics engine was never doing much work here. The scenes gave all
/// bouncing bodies a <c>PhysicsMaterial</c> of <c>bounce = 1.0, friction = 0.0</c>, which
/// makes the whole solver a perfectly elastic, frictionless impulse between two circles.
/// </para>
/// <para>
/// <b>Contact semantics.</b> Godot's <c>body_entered</c> fires when a contact
/// <em>begins</em>, not while it persists. A naive per-step overlap test would instead
/// fire every frame two bodies remained touching, which would multiply collision damage
/// by the frame rate and re-trigger pickups. Contact pairs are therefore tracked across
/// steps and callbacks are raised only for pairs that were not touching last step.
/// </para>
/// <para>
/// <b>Walls.</b> The Godot barrier used four 400-unit-thick slabs outside the play area,
/// thick enough that nothing could tunnel through them in one step. Here the bounds are
/// enforced as a constraint on the body's centre instead, which cannot be tunnelled at
/// any speed and needs no thickness at all.
/// </para>
/// </remarks>
public sealed class CircleCollisionWorld
{
    /// <summary>
    /// Broadphase cell size. The largest collider in the game is a large asteroid at
    /// radius 96, so at 256 a body spans at most two cells on each axis.
    /// </summary>
    private const float CellSize = 256.0f;

    private readonly Dictionary<long, List<GameObject>> _grid = [];
    private readonly List<GameObject> _simulating = [];
    private readonly List<(GameObject A, GameObject B)> _pendingContacts = [];
    private readonly List<GameObject> _pendingWallContacts = [];

    private HashSet<long> _previousPairs = [];
    private HashSet<long> _currentPairs = [];
    private HashSet<int> _previousWallTouches = [];
    private HashSet<int> _currentWallTouches = [];

    public CircleCollisionWorld(float width, float height)
    {
        Width = width;
        Height = height;
    }

    public float Width { get; }

    public float Height { get; }

    /// <summary>
    /// Advances every active body and dispatches the resulting contacts.
    /// </summary>
    /// <remarks>
    /// Ordering matches the Godot frame: gameplay <c>tick</c> has already run by the time
    /// this is called, velocities are sampled, positions integrate, the solver resolves,
    /// and only then are contact callbacks raised - so a handler reading
    /// <see cref="GameObject.PreStepVelocity"/> sees the approach speed rather than the
    /// post-bounce one.
    /// </remarks>
    public void Step(IReadOnlyList<GameObject> bodies, float delta)
    {
        _simulating.Clear();

        foreach (var body in bodies)
        {
            if (!body.IsSimulating)
            {
                continue;
            }

            body.PreStepVelocity = body.Velocity;
            body.Position += body.Velocity * delta;
            _simulating.Add(body);
        }

        _pendingContacts.Clear();
        _pendingWallContacts.Clear();
        _currentPairs.Clear();
        _currentWallTouches.Clear();

        ResolveWalls();
        ResolvePairs();

        // Callbacks run after every impulse has been applied, so a handler that destroys
        // or repositions a body cannot corrupt the solve in progress.
        foreach (var body in _pendingWallContacts)
        {
            body.OnWallContact();
        }

        foreach (var (a, b) in _pendingContacts)
        {
            // Both bodies must still be live. Handlers earlier in this same loop routinely
            // deactivate bodies - a projectile dying, a ship destroyed by a blast - and
            // Godot's own dispatch refused to deliver a contact involving an inactive body.
            // Without the check, a laser is destroyed by a projectile that was already
            // recycled this step, and a power-up is consumed by a ship that is already dead.
            if (!a.IsActive || !b.IsActive)
            {
                continue;
            }

            a.OnContact(b);

            if (a.IsActive && b.IsActive)
            {
                b.OnContact(a);
            }
        }

        (_previousPairs, _currentPairs) = (_currentPairs, _previousPairs);
        (_previousWallTouches, _currentWallTouches) = (_currentWallTouches, _previousWallTouches);
    }

    /// <summary>Forgets all contact history, for match teardown.</summary>
    public void Reset()
    {
        _previousPairs.Clear();
        _currentPairs.Clear();
        _previousWallTouches.Clear();
        _currentWallTouches.Clear();
        _grid.Clear();
    }

    /// <summary>
    /// Keeps bodies inside the play area, reflecting velocity for a perfectly elastic
    /// bounce.
    /// </summary>
    private void ResolveWalls()
    {
        foreach (var body in _simulating)
        {
            if ((body.CollisionMask & CollisionLayer.Walls) == 0)
            {
                continue;
            }

            var radius = body.Radius;
            var touched = false;

            if (body.Position.X - radius < 0.0f)
            {
                body.Position.X = radius;
                body.Velocity.X = MathF.Abs(body.Velocity.X);
                touched = true;
            }
            else if (body.Position.X + radius > Width)
            {
                body.Position.X = Width - radius;
                body.Velocity.X = -MathF.Abs(body.Velocity.X);
                touched = true;
            }

            if (body.Position.Y - radius < 0.0f)
            {
                body.Position.Y = radius;
                body.Velocity.Y = MathF.Abs(body.Velocity.Y);
                touched = true;
            }
            else if (body.Position.Y + radius > Height)
            {
                body.Position.Y = Height - radius;
                body.Velocity.Y = -MathF.Abs(body.Velocity.Y);
                touched = true;
            }

            if (!touched)
            {
                continue;
            }

            _currentWallTouches.Add(body.UniqueId);

            if (!_previousWallTouches.Contains(body.UniqueId))
            {
                _pendingWallContacts.Add(body);
            }
        }
    }

    private void ResolvePairs()
    {
        BuildGrid();

        foreach (var a in _simulating)
        {
            var minX = CellIndex(a.Position.X - a.Radius);
            var maxX = CellIndex(a.Position.X + a.Radius);
            var minY = CellIndex(a.Position.Y - a.Radius);
            var maxY = CellIndex(a.Position.Y + a.Radius);

            for (var cy = minY; cy <= maxY; cy++)
            {
                for (var cx = minX; cx <= maxX; cx++)
                {
                    if (!_grid.TryGetValue(CellKey(cx, cy), out var bucket))
                    {
                        continue;
                    }

                    foreach (var b in bucket)
                    {
                        // Ordering by id both halves the work and guarantees a pair
                        // spanning several shared cells is only considered once.
                        if (a.UniqueId >= b.UniqueId || !ShouldCollide(a, b))
                        {
                            continue;
                        }

                        TryResolve(a, b);
                    }
                }
            }
        }
    }

    private void BuildGrid()
    {
        foreach (var bucket in _grid.Values)
        {
            bucket.Clear();
        }

        foreach (var body in _simulating)
        {
            var minX = CellIndex(body.Position.X - body.Radius);
            var maxX = CellIndex(body.Position.X + body.Radius);
            var minY = CellIndex(body.Position.Y - body.Radius);
            var maxY = CellIndex(body.Position.Y + body.Radius);

            for (var cy = minY; cy <= maxY; cy++)
            {
                for (var cx = minX; cx <= maxX; cx++)
                {
                    var key = CellKey(cx, cy);

                    if (!_grid.TryGetValue(key, out var bucket))
                    {
                        bucket = [];
                        _grid[key] = bucket;
                    }

                    bucket.Add(body);
                }
            }
        }
    }

    /// <summary>
    /// Godot pairs two bodies when <em>either</em> one's mask covers the other's layer.
    /// </summary>
    /// <remarks>
    /// The OR rather than AND is load-bearing: power-ups mask against ships, but ships do
    /// not mask against pickups, so an AND would silently stop every power-up in the game
    /// from being collectable.
    /// </remarks>
    private static bool ShouldCollide(GameObject a, GameObject b)
        => ((a.CollisionMask & b.CollisionLayer) != 0 || (b.CollisionMask & a.CollisionLayer) != 0)
            && !a.IsExemptFrom(b);

    private void TryResolve(GameObject a, GameObject b)
    {
        var delta = b.Position - a.Position;
        var combined = a.Radius + b.Radius;
        var distanceSquared = delta.LengthSquared();

        if (distanceSquared >= combined * combined)
        {
            return;
        }

        var key = PairKey(a.UniqueId, b.UniqueId);

        // The pair set is also the per-step dedupe. Two bodies that share several grid
        // cells are handed to this method once per shared cell, and the overlap test above
        // does not reliably stop the repeats: the first call depenetrates them to exactly
        // touching, so the comparison turns on float rounding. Without this, OnContact
        // would fire twice for the same collision and ram damage would be applied twice.
        if (!_currentPairs.Add(key))
        {
            return;
        }

        if (!_previousPairs.Contains(key))
        {
            _pendingContacts.Add((a, b));
        }

        var distance = MathF.Sqrt(distanceSquared);

        // Two bodies spawned exactly on top of each other have no defined separation
        // axis; pick one so they push apart instead of dividing by zero.
        var normal = distance > 1e-6f ? delta / distance : new Vector2(0.0f, -1.0f);

        var inverseMassA = a.Mass > 0.0f ? 1.0f / a.Mass : 0.0f;
        var inverseMassB = b.Mass > 0.0f ? 1.0f / b.Mass : 0.0f;
        var inverseMassSum = inverseMassA + inverseMassB;

        if (inverseMassSum <= 0.0f)
        {
            return;
        }

        // Push the overlap out, split by inverse mass so the lighter body moves further.
        var penetration = combined - distance;
        var correction = normal * (penetration / inverseMassSum);
        a.Position -= correction * inverseMassA;
        b.Position += correction * inverseMassB;

        var relativeVelocity = Vector2.Dot(b.Velocity - a.Velocity, normal);

        // Already separating: the pair was resolved by an earlier contact this step, and
        // applying a second impulse would suck them back together.
        if (relativeVelocity > 0.0f)
        {
            return;
        }

        // Restitution is 1.0 for every bouncing body in the game, hence the bare 2.
        var impulse = -2.0f * relativeVelocity / inverseMassSum;
        var push = normal * impulse;
        a.Velocity -= push * inverseMassA;
        b.Velocity += push * inverseMassB;
    }

    private static int CellIndex(float value) => (int)MathF.Floor(value / CellSize);

    private static long CellKey(int x, int y) => ((long)x << 32) ^ (uint)y;

    private static long PairKey(int a, int b)
    {
        var low = Math.Min(a, b);
        var high = Math.Max(a, b);
        return ((long)low << 32) | (uint)high;
    }
}
