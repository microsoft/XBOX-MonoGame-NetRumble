namespace NetRumble.Core.Tuning;

/// <summary>
/// Designer-tunable projectile stats, ported from
/// <c>scripts/gameplay/tuning/projectile_tuning.gd</c>.
/// </summary>
/// <remarks>
/// One instance per projectile type rather than one class per weapon, matching the Godot
/// layout. The <c>Drift</c> properties are mine-only and stay at zero for the others.
/// </remarks>
public sealed class ProjectileTuning
{
    public ProjectileType ProjectileType { get; set; } = ProjectileType.Laser;

    // Body

    public float Mass { get; set; } = 0.5f;
    public float Radius { get; set; } = 4.0f;
    public float Velocity { get; set; } = 640.0f;
    public float Health { get; set; } = 1.0f;

    /// <summary>Seconds before the projectile expires on its own.</summary>
    public float Duration { get; set; } = 5.0f;

    // Damage

    public float DamageAmount { get; set; } = 20.0f;

    /// <summary>Zero means a direct hit only, with no area-of-effect falloff.</summary>
    public float DamageRadius { get; set; }

    public bool CanDamageOwner { get; set; }

    // Drift (mine only)

    /// <summary>Fraction of velocity shed per second until the mine anchors.</summary>
    public float DragPerSecond { get; set; }

    /// <summary>Squared speed below which the mine anchors in place.</summary>
    public float MinimumVelocitySquared { get; set; }

    /// <summary>Constant spin, in radians per second.</summary>
    public float RotationSpeed { get; set; }

    // Presentation

    /// <summary>
    /// Key into the game layer's texture registry.
    /// </summary>
    /// <remarks>
    /// The Godot resource held a <c>Texture2D</c> directly. This assembly is deliberately
    /// free of any rendering framework, so tuning refers to art by key and the game layer
    /// resolves it.
    /// </remarks>
    public string Texture { get; set; } = string.Empty;

    /// <summary>
    /// Extra sprite rotation, for art that is not drawn nose-up. The body itself always
    /// faces the direction the projectile travels.
    /// </summary>
    public float SpriteRotation { get; set; }
}
