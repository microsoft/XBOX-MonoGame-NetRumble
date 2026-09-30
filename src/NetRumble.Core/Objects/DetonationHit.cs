using System.Numerics;

namespace NetRumble.Core.Objects;

/// <summary>
/// The authoritative post-damage state of one object caught in a detonation, replacing
/// the dictionary returned by <c>world.gd::create_detonation_hit()</c>.
/// </summary>
/// <remarks>
/// Explosion damage is resolved entirely on the host and the <em>results</em> are
/// replicated, rather than each client re-running the damage calculation. Chained mine
/// detonations are order-dependent, so recomputing them per peer would drift.
/// </remarks>
public struct DetonationHit
{
    public int Id;

    public float Health;

    /// <summary>
    /// The target's shield, or -1 when the target is not a ship.
    /// </summary>
    /// <remarks>
    /// The sentinel matters: a plain default of zero would be indistinguishable from a
    /// fully depleted shield, and the receiver uses this to decide whether to route the
    /// update through the ship's damage path at all.
    /// </remarks>
    public float Shield;

    public Vector2 Position;

    public Vector2 Velocity;

    public bool IsActive;
}
