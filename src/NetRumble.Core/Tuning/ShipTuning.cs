namespace NetRumble.Core.Tuning;

/// <summary>
/// Designer-tunable ship stats, ported from <c>scripts/gameplay/tuning/ship_tuning.gd</c>
/// and <c>assets/tuning/ship_tuning.tres</c>.
/// </summary>
/// <remarks>
/// Every property default here matches the GDScript <c>@export</c> default, which in turn
/// matches the original C++ NetRumble constants. The shipped <c>.tres</c> overrode none of
/// them, so this class alone reproduces the retail tuning.
/// </remarks>
public sealed class ShipTuning
{
    // Movement

    /// <summary>Acceleration applied per second while the stick is fully deflected.</summary>
    public float SpeedMax { get; set; } = 400.0f;

    public float VelocityMax { get; set; } = 400.0f;

    /// <summary>Fraction of current velocity shed per second.</summary>
    public float VelocityDecayRate { get; set; } = 0.7f;

    public float RotationPerSecond { get; set; } = 6.0f;

    /// <summary>Below this angular error the ship stops correcting its heading.</summary>
    public float AngleThreshold { get; set; } = 0.001f;

    /// <summary>Squared speed above which the thruster sprite appears.</summary>
    public float ThrusterVelocityThresholdSquared { get; set; } = 100.0f;

    // Durability

    public float HealthMax { get; set; } = 25.0f;
    public float ShieldMax { get; set; } = 100.0f;
    public float ShieldRechargeRate { get; set; } = 50.0f;
    public float ShieldRechargeDelay { get; set; } = 2.5f;
    public float InvulnerableTimerMax { get; set; } = 4.0f;
    public float Mass { get; set; } = 32.0f;
    public float Radius { get; set; } = 24.0f;

    /// <summary>Ships shrink slightly once their shield is down.</summary>
    public float RadiusNoShield { get; set; } = 20.0f;

    // Weapons

    public float WeaponFireRate { get; set; } = 0.15f;
    public float FireRateTripleLaser { get; set; } = 0.3f;
    public float FireRateRocket { get; set; } = 0.5f;

    /// <summary>Squared stick deflection required to count as "firing".</summary>
    public float FireThresholdSquared { get; set; } = 0.25f;

    public float MineDeployRate { get; set; } = 3.0f;

    // Presentation

    public float ShieldAlphaMax { get; set; } = 0.588f;
    public float TextureDimension { get; set; } = 64.0f;
    public float ShieldTextureDimension { get; set; } = 162.0f;
    public float ThrusterTextureDimension { get; set; } = 180.0f;

    /// <summary>
    /// Seconds between shots for the supplied weapon.
    /// </summary>
    /// <remarks>
    /// Single and double laser both use the base <see cref="WeaponFireRate"/>; the double
    /// laser's advantage is two projectiles per shot, not a faster cadence.
    /// </remarks>
    public float FireRateFor(WeaponType weapon) => weapon switch
    {
        WeaponType.TripleLaser => FireRateTripleLaser,
        WeaponType.Rocket => FireRateRocket,
        _ => WeaponFireRate,
    };

    /// <summary>Collision radius for the ship's current shield value.</summary>
    /// <remarks>
    /// Mirrors <c>ship.gd::_update_shield</c>: any shield at all, however small, keeps the
    /// ship at its full radius. The shrink happens only once the shield is fully depleted.
    /// </remarks>
    public float RadiusFor(float shield) => shield > 0.0f ? Radius : RadiusNoShield;
}
