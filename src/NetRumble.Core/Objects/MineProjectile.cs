using System.Numerics;
using NetRumble.Core.Tuning;

namespace NetRumble.Core.Objects;

/// <summary>
/// A slow-drifting mine that anchors once it slows down and detonates in an area
/// blast. Ported from <c>scripts/gameplay/objects/mine_projectile.gd</c>.
/// </summary>
/// <remarks>
/// <para>
/// The GDScript relied on the body's <c>linear_damp</c> (in
/// <c>DAMP_MODE_REPLACE</c>) for drag; Godot's physics server multiplies velocity by
/// <c>1 - damp * step</c> once per physics step, entirely outside the script.
/// <see cref="Simulation.CircleCollisionWorld"/> has no equivalent per-body damping,
/// so that decay is folded into <see cref="Tick"/> here instead. See the
/// "JUDGEMENT CALLS" note in the porting report for the exact ordering chosen.
/// </para>
/// </remarks>
public sealed class MineProjectile : Projectile
{
    private bool _anchored;
    private bool _detonationQueued;

    /// <summary>True once the mine has slowed below its anchor threshold and stopped.</summary>
    public bool IsAnchored => _anchored;

    public MineProjectile()
    {
        Tuning = TuningLibrary.Mine;
        ApplyTuning();
    }

    public override void Start()
    {
        base.Start();
        _anchored = false;
        _detonationQueued = false;
    }

    /// <summary>
    /// Applies drag until the mine anchors, then holds it in place, and keeps up its
    /// constant spin.
    /// </summary>
    public override void Tick(float delta)
    {
        if (!_anchored)
        {
            if (Velocity.LengthSquared() <= Tuning.MinimumVelocitySquared)
            {
                _anchored = true;
                Velocity = Vector2.Zero;
            }
            else
            {
                Velocity *= 1.0f - (Tuning.DragPerSecond * delta);
            }
        }
        else
        {
            Velocity = Vector2.Zero;
        }

        Rotation += Tuning.RotationSpeed * delta;

        base.Tick(delta);
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

        if (other is Ship or Asteroid)
        {
            Die();
        }
    }

    /// <summary>
    /// Mines take chip damage from other projectiles but are one-shot detonated by
    /// anything else that touches them (for example a ship ramming it).
    /// </summary>
    public override void TakeDamage(GameObject? source, float damage)
    {
        if (!IsActive || damage <= 0.0f)
        {
            return;
        }

        if (source is { ObjectType: GameObjectType.Projectile })
        {
            Health = MathF.Max(Health - damage, 0.0f);
        }
        else
        {
            Health = 0.0f;
        }

        if (Health <= 0.0f && World is { IsAuthority: true })
        {
            World.QueueMineDetonation(this);
        }
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

    /// <summary>
    /// Detonates a mine whose destruction was deferred by
    /// <see cref="TryQueueDetonation"/>, once the authority is ready to process it.
    /// </summary>
    public void DetonateQueued() => Detonate(Position, notifyAuthority: true);

    /// <summary>
    /// Marks this mine for a deferred detonation, so several mines destroyed in the
    /// same step do not all blast in the middle of iterating the world's object list.
    /// </summary>
    /// <returns>False if the mine is already inactive or already queued.</returns>
    public bool TryQueueDetonation()
    {
        if (!IsActive || _detonationQueued)
        {
            return false;
        }

        _detonationQueued = true;
        return true;
    }

    private void Detonate(Vector2 pos, bool notifyAuthority)
    {
        if (!IsActive)
        {
            return;
        }

        Teleport(pos);
        _detonationQueued = false;
        DeactivateProjectile();

        if (World is not null && notifyAuthority)
        {
            var hits = World.ApplyExplosionDamage(this, pos, DamageAmount, DamageRadius, CanDamageOwner, null);
            World.NotifyProjectileDetonated(this, pos, hits);
        }
    }
}
