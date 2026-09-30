namespace NetRumble.Core.Tuning;

/// <summary>
/// Everything the simulation needs to know about one of the twenty weapons. Ported from
/// <c>scripts/gameplay/tuning/weapon_definition.gd</c>.
/// </summary>
/// <remarks>
/// <para>
/// Each weapon is data: how many shots, how they are arranged, and a set of multipliers
/// applied to the pooled projectile that carries them. <see cref="World.CreateProjectiles"/>
/// is a single generic routine that reads this definition for every trigger pull,
/// regardless of weapon type.
/// </para>
/// <para>
/// The multipliers are deliberately relative to the projectile's own tuning rather than
/// absolute. A weapon says "twice the damage, half the range"; it does not restate the
/// laser's stats, so retuning <see cref="TuningLibrary.Laser"/> still moves every
/// laser-derived weapon with it.
/// </para>
/// <para>
/// Only the projectile <i>pool</i> is a real type (laser / mine / rocket): there are
/// three pools and twenty weapons, so a "plasma cannon" is a laser body with a large
/// radius, a slow speed and a splash radius, and a "beam lance" is a laser body that
/// travels very fast, lives briefly and pierces. Guidance is the one behaviour that
/// cannot be expressed as a number on a straight-line body, so <see cref="HomingRate"/>
/// is read by <c>Projectile.Tick</c> directly.
/// </para>
/// </remarks>
public sealed class WeaponDefinition
{
    public WeaponType WeaponType { get; set; } = WeaponType.Laser;

    public string DisplayName { get; set; } = "LASER";

    /// <summary>
    /// Which pool the shots are drawn from.
    /// </summary>
    /// <remarks>
    /// Pools are finite and sized per player, so a weapon that fires eight shots at once
    /// burns through the laser pool eight times as fast as the plain laser - which is why
    /// the high shot-count weapons also have short ranges, retiring their projectiles
    /// quickly.
    /// </remarks>
    public ProjectileType ProjectileType { get; set; } = ProjectileType.Laser;

    // --- Volley -------------------------------------------------------------

    /// <summary>Number of projectiles per trigger pull.</summary>
    public int ShotCount { get; set; } = 1;

    /// <summary>
    /// Total arc the volley is fanned across, in radians. The shots are distributed
    /// evenly across it and centred on the aim direction, so a two-shot weapon with a
    /// spread of 0.2 puts one shot 0.1rad either side.
    /// </summary>
    public float Spread { get; set; }

    /// <summary>
    /// Extra random angle applied per shot, in radians. This is what makes a scatter gun
    /// read as a cloud rather than as a rigid fan.
    /// </summary>
    public float SpreadJitter { get; set; }

    /// <summary>
    /// Perpendicular spacing between shots, in pixels. Used by the wing-mounted weapons
    /// so their shots travel parallel rather than diverging.
    /// </summary>
    public float LateralSpacing { get; set; }

    /// <summary>Distance ahead of the ship the volley is spawned at.</summary>
    public float MuzzleOffset { get; set; }

    // --- Timing -------------------------------------------------------------

    /// <summary>
    /// Seconds between volleys. This replaces the per-weapon fire-rate fields that used
    /// to live on <see cref="ShipTuning"/>.
    /// </summary>
    public float FireRate { get; set; } = 0.15f;

    /// <summary>
    /// How many volleys the pickup is good for before the ship falls back to the plain
    /// laser. -1 means unlimited.
    /// </summary>
    public int Ammo { get; set; } = -1;

    // --- Shot multipliers ---------------------------------------------------

    public float DamageScale { get; set; } = 1.0f;

    public float SpeedScale { get; set; } = 1.0f;

    /// <summary>
    /// Scales the projectile's lifetime, which is what actually determines its range.
    /// </summary>
    public float RangeScale { get; set; } = 1.0f;

    public float RadiusScale { get; set; } = 1.0f;

    /// <summary>
    /// Splash radius in pixels. -1 inherits the projectile tuning's own value (which is
    /// what the rocket-derived weapons want), and 0 disables the explosion outright.
    /// </summary>
    /// <remarks>
    /// Not a scale, because most weapons derive from the laser, whose splash radius is
    /// zero and cannot be scaled up from.
    /// </remarks>
    public float SplashRadius { get; set; } = -1.0f;

    /// <summary>
    /// Extra bodies the shot passes through before dying. 0 is the normal "dies on first
    /// contact" behaviour.
    /// </summary>
    public int Pierce { get; set; }

    /// <summary>Times the shot reflects off the barrier instead of dying against it.</summary>
    public int Bounces { get; set; }

    /// <summary>
    /// Turn rate in radians per second used to steer towards the nearest enemy. 0 is
    /// dumb fire.
    /// </summary>
    public float HomingRate { get; set; }

    // --- Presentation -------------------------------------------------------

    /// <summary>
    /// Tint applied to the shot. A zero alpha means "use the firing player's colour",
    /// which keeps shots visually tied to the ship that fired them.
    /// </summary>
    /// <remarks>
    /// The exotic weapons override it so their shots are recognisable at a glance, since
    /// the project only ships three projectile textures.
    /// </remarks>
    public RgbaColor ShotColor { get; set; } = new(0, 0, 0, 0);

    /// <summary>Multiplies the sprite scale authored for the projectile.</summary>
    public float SpriteScale { get; set; } = 1.0f;

    /// <summary>
    /// Radius of the light each shot casts onto ships and asteroids. 0 leaves the light
    /// switched off.
    /// </summary>
    public float LightRadius { get; set; } = 90.0f;

    /// <summary>Brightness of that light.</summary>
    public float LightEnergy { get; set; } = 1.0f;

    /// <summary>
    /// Packs the shot-affecting fields into the spec that rides the projectile spawn
    /// message.
    /// </summary>
    /// <remarks>
    /// Clients never look a weapon up: they are told, per shot, exactly what the
    /// authority built. That keeps the two sides in step even when a client is running a
    /// build whose weapon table has been retuned, and it means a client only needs the
    /// dozen numbers that change how a shot looks and how long it lives - damage and
    /// splash are resolved by the authority and arrive as detonation results.
    /// </remarks>
    public ShotSpec ToShotSpec() => new(
        DamageScale,
        SpeedScale,
        RangeScale,
        RadiusScale,
        SplashRadius,
        Pierce,
        Bounces,
        HomingRate,
        SpriteScale,
        LightRadius,
        LightEnergy,
        ShotColor);
}
