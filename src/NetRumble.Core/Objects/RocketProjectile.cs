using System.Numerics;
using NetRumble.Core.Tuning;

namespace NetRumble.Core.Objects;

/// <summary>
/// A fast projectile that deals a direct hit plus an area-of-effect blast. Ported
/// from <c>scripts/gameplay/objects/rocket_projectile.gd</c>.
/// </summary>
public sealed class RocketProjectile : Projectile
{
    /// <summary>
    /// The single body the rocket directly struck, if any, so it is guaranteed to be
    /// first in the detonation results even when the explosion radius would rank it
    /// behind a closer bystander.
    /// </summary>
    private GameObject? _directTarget;

    /// <summary>
    /// Renderer hook for the exhaust trail: emits only while the rocket is live,
    /// replacing the Godot version's <c>_trail.emitting = is_active</c> sync.
    /// </summary>
    public bool IsTrailEmitting => IsActive;

    public RocketProjectile()
    {
        Tuning = TuningLibrary.Rocket;
        ApplyTuning();
    }

    public override void Start()
    {
        base.Start();
        _directTarget = null;
    }

    public override void OnContact(GameObject other)
    {
        if (!IsActive)
        {
            return;
        }

        if (other is Ship ship && ship.UniqueId == OwnerId)
        {
            return;
        }

        if (other is Projectile projectile && projectile.OwnerId == OwnerId)
        {
            return;
        }

        if (World is not { IsAuthority: true })
        {
            return;
        }

        _directTarget = other;
        other.TakeDamage(this, DamageAmount);
        Die();
    }

    public override void Die()
    {
        if (World is not { IsAuthority: true })
        {
            return;
        }

        Detonate(Position, notifyAuthority: true);
    }

    /// <summary>Client-side detonation driven by a message from the authority.</summary>
    public void DetonateFromAuthority(Vector2 pos) => Detonate(pos, notifyAuthority: false);

    private void Detonate(Vector2 pos, bool notifyAuthority)
    {
        if (!IsActive)
        {
            return;
        }

        Teleport(pos);
        DeactivateProjectile();

        if (World is null || !notifyAuthority)
        {
            return;
        }

        var hits = World.ApplyExplosionDamage(this, pos, DamageAmount, DamageRadius, CanDamageOwner, _directTarget);

        if (_directTarget is not null)
        {
            hits.Insert(0, World.CreateDetonationHit(_directTarget));
        }

        World.NotifyProjectileDetonated(this, pos, hits);
    }
}
