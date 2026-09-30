namespace NetRumble.Core.Tuning;

/// <summary>
/// The twenty-entry weapon table. Ported from
/// <c>scripts/gameplay/tuning/weapon_library.gd</c>.
/// </summary>
/// <remarks>
/// <para>
/// Held in code rather than as twenty data files. Weapons are balanced against each
/// other, so having the whole set side by side in one screenful is worth more than
/// per-weapon editing, and it keeps the tuning assets from filling up with
/// near-identical entries. The tunings that remain - projectile and ship - describe
/// <i>bodies</i>, which is a different thing: a weapon only ever says how those bodies
/// differ for this trigger pull.
/// </para>
/// <para>
/// The first four entries establish the baseline feel of the game and are deliberately
/// close to how those weapons have always played. The remaining sixteen use the same
/// three projectile bodies (laser, mine, rocket) but vary their parameters to produce
/// entirely different behaviours.
/// </para>
/// <para>
/// Balance shape: everything is paid for with ammo. Pickups drop constantly, so an exotic
/// weapon is a burst of a few seconds rather than a permanent upgrade, and running dry
/// drops the ship back to the unlimited laser instead of leaving it unarmed.
/// </para>
/// </remarks>
public static class WeaponLibrary
{
    /// <summary>
    /// Perpendicular spacing used by the double laser and inherited by the other
    /// wing-mounted weapons. Matches <see cref="NRConst.DoubleLaserOffset"/>.
    /// </summary>
    private const float WingSpacing = 10.0f;

    private const float Tau = MathF.PI * 2.0f;

    private static readonly Dictionary<WeaponType, WeaponDefinition> Table = BuildTable();

    /// <summary>
    /// Returns the definition for a weapon, falling back to the plain laser so an
    /// unrecognised value can never leave a ship unable to shoot.
    /// </summary>
    public static WeaponDefinition Get(WeaponType weapon)
        => Table.TryGetValue(weapon, out var definition) ? definition : Table[WeaponType.Laser];

    public static IReadOnlyCollection<WeaponDefinition> All => Table.Values;

    public static string DisplayName(WeaponType weapon) => Get(weapon).DisplayName;

    private static Dictionary<WeaponType, WeaponDefinition> BuildTable()
    {
        var table = new Dictionary<WeaponType, WeaponDefinition>();

        void Add(WeaponDefinition definition) => table[definition.WeaponType] = definition;

        // --- The four baseline weapons --------------------------------------
        Add(new WeaponDefinition
        {
            WeaponType = WeaponType.Laser,
            DisplayName = "LASER",
            FireRate = 0.15f,
        });

        Add(new WeaponDefinition
        {
            WeaponType = WeaponType.DoubleLaser,
            DisplayName = "DOUBLE LASER",
            ShotCount = 2,
            LateralSpacing = WingSpacing,
            FireRate = 0.15f,
        });

        Add(new WeaponDefinition
        {
            WeaponType = WeaponType.TripleLaser,
            DisplayName = "TRIPLE LASER",
            ShotCount = 3,
            Spread = 0.4f,
            FireRate = 0.3f,
        });

        Add(new WeaponDefinition
        {
            WeaponType = WeaponType.Rocket,
            DisplayName = "ROCKET",
            ProjectileType = ProjectileType.Rocket,
            FireRate = 0.5f,
            LightRadius = 160.0f,
            LightEnergy = 1.6f,
        });

        // --- Multi-shot kinetics --------------------------------------------
        Add(new WeaponDefinition
        {
            WeaponType = WeaponType.QuadLaser,
            DisplayName = "QUAD LASER",
            ShotCount = 4,
            Spread = 0.16f,
            LateralSpacing = WingSpacing * 1.6f,
            FireRate = 0.2f,
            DamageScale = 0.8f,
            Ammo = 40,
            ShotColor = RgbaColor.FromFloat(0.6f, 0.95f, 1.0f),
        });

        Add(new WeaponDefinition
        {
            WeaponType = WeaponType.SpreadShot,
            DisplayName = "SPREAD SHOT",
            ShotCount = 5,
            Spread = 0.9f,
            FireRate = 0.28f,
            DamageScale = 0.7f,
            RangeScale = 0.7f,
            Ammo = 35,
            ShotColor = RgbaColor.FromFloat(1.0f, 0.85f, 0.4f),
        });

        Add(new WeaponDefinition
        {
            WeaponType = WeaponType.ScatterGun,
            DisplayName = "SCATTER GUN",
            ShotCount = 8,
            Spread = 1.1f,
            SpreadJitter = 0.12f,
            FireRate = 0.4f,
            DamageScale = 0.45f,
            SpeedScale = 0.85f,

            // Short-range by design: the shots expire in a fifth of a second, so the
            // cloud only reaches a few hundred pixels and the pool recovers quickly
            // enough to survive an eight-shot volley every 0.4s.
            RangeScale = 0.06f,
            Ammo = 30,
            ShotColor = RgbaColor.FromFloat(1.0f, 0.6f, 0.25f),
        });

        Add(new WeaponDefinition
        {
            WeaponType = WeaponType.ShotgunBlast,
            DisplayName = "SHOTGUN",
            ShotCount = 7,
            Spread = 0.55f,
            SpreadJitter = 0.09f,
            FireRate = 0.55f,
            DamageScale = 0.8f,
            SpeedScale = 1.1f,
            RangeScale = 0.12f,
            Ammo = 24,
            SpriteScale = 1.3f,
            ShotColor = RgbaColor.FromFloat(1.0f, 0.45f, 0.35f),
        });

        Add(new WeaponDefinition
        {
            WeaponType = WeaponType.Vulcan,
            DisplayName = "VULCAN",
            ShotCount = 1,
            SpreadJitter = 0.07f,
            LateralSpacing = WingSpacing,

            // Four times the plain laser's rate of fire, at a third of the damage.
            FireRate = 0.04f,
            DamageScale = 0.35f,
            SpeedScale = 1.2f,
            RangeScale = 0.5f,
            Ammo = 160,
            SpriteScale = 0.8f,
            LightRadius = 60.0f,
            ShotColor = RgbaColor.FromFloat(1.0f, 0.95f, 0.55f),
        });

        // --- Beams ------------------------------------------------------------
        Add(new WeaponDefinition
        {
            WeaponType = WeaponType.BeamLance,
            DisplayName = "BEAM LANCE",

            // A beam is a very fast, very short-lived shot that refuses to stop at the
            // first thing it hits. At 4x speed over a tenth of the laser's lifetime it
            // covers roughly 1300px in half a second, which reads on screen as a lance
            // rather than as a bullet - and no new projectile type is needed for it.
            SpeedScale = 4.0f,
            RangeScale = 0.1f,
            DamageScale = 1.4f,
            Pierce = 4,
            FireRate = 0.35f,
            Ammo = 30,
            SpriteScale = 2.2f,
            LightRadius = 140.0f,
            LightEnergy = 1.8f,
            ShotColor = RgbaColor.FromFloat(0.55f, 1.0f, 0.75f),
        });

        Add(new WeaponDefinition
        {
            WeaponType = WeaponType.PulseBeam,
            DisplayName = "PULSE BEAM",
            ShotCount = 3,
            LateralSpacing = WingSpacing * 0.5f,
            SpeedScale = 3.0f,
            RangeScale = 0.16f,
            DamageScale = 0.9f,
            Pierce = 2,
            FireRate = 0.22f,
            Ammo = 45,
            SpriteScale = 1.6f,
            LightRadius = 120.0f,
            LightEnergy = 1.5f,
            ShotColor = RgbaColor.FromFloat(0.5f, 0.8f, 1.0f),
        });

        Add(new WeaponDefinition
        {
            WeaponType = WeaponType.Railgun,
            DisplayName = "RAILGUN",
            SpeedScale = 5.0f,
            RangeScale = 0.14f,
            DamageScale = 3.0f,
            Pierce = 8,
            FireRate = 0.9f,
            Ammo = 12,
            SpriteScale = 2.6f,
            LightRadius = 180.0f,
            LightEnergy = 2.2f,
            ShotColor = RgbaColor.FromFloat(0.85f, 0.7f, 1.0f),
        });

        Add(new WeaponDefinition
        {
            WeaponType = WeaponType.ChainLightning,
            DisplayName = "CHAIN LIGHTNING",
            ShotCount = 3,
            Spread = 0.5f,
            SpreadJitter = 0.25f,
            SpeedScale = 2.2f,
            RangeScale = 0.2f,
            DamageScale = 0.6f,
            Pierce = 3,
            FireRate = 0.18f,
            Ammo = 60,
            SpriteScale = 1.2f,
            LightRadius = 110.0f,
            LightEnergy = 1.7f,
            ShotColor = RgbaColor.FromFloat(0.7f, 0.9f, 1.0f),
        });

        // --- Guided -----------------------------------------------------------
        Add(new WeaponDefinition
        {
            WeaponType = WeaponType.HomingMissile,
            DisplayName = "HOMING MISSILE",
            ProjectileType = ProjectileType.Rocket,
            HomingRate = 3.2f,
            SpeedScale = 0.8f,
            RangeScale = 1.5f,
            FireRate = 0.7f,
            Ammo = 10,
            LightRadius = 170.0f,
            LightEnergy = 1.8f,
            ShotColor = RgbaColor.FromFloat(1.0f, 0.55f, 0.55f),
        });

        Add(new WeaponDefinition
        {
            WeaponType = WeaponType.SwarmMissiles,
            DisplayName = "SWARM MISSILES",
            ProjectileType = ProjectileType.Rocket,
            ShotCount = 4,
            Spread = 1.6f,
            SpreadJitter = 0.2f,
            HomingRate = 2.4f,
            SpeedScale = 0.7f,
            RangeScale = 1.4f,
            DamageScale = 0.4f,
            SplashRadius = 80.0f,
            FireRate = 1.1f,
            Ammo = 5,
            SpriteScale = 0.7f,
            LightRadius = 120.0f,
            ShotColor = RgbaColor.FromFloat(1.0f, 0.7f, 0.4f),
        });

        Add(new WeaponDefinition
        {
            WeaponType = WeaponType.SeekerMines,
            DisplayName = "SEEKER MINES",
            ProjectileType = ProjectileType.Mine,
            ShotCount = 3,
            Spread = 2.4f,
            MuzzleOffset = 36.0f,
            HomingRate = 0.8f,
            SpeedScale = 2.0f,
            RangeScale = 0.4f,
            DamageScale = 0.5f,
            FireRate = 1.4f,
            Ammo = 3,
            LightRadius = 140.0f,
            ShotColor = RgbaColor.FromFloat(1.0f, 0.4f, 0.7f),
        });

        // --- Explosives -------------------------------------------------------
        Add(new WeaponDefinition
        {
            WeaponType = WeaponType.PlasmaCannon,
            DisplayName = "PLASMA CANNON",
            SpeedScale = 0.5f,
            RangeScale = 1.4f,
            DamageScale = 2.0f,
            RadiusScale = 3.0f,
            SplashRadius = 140.0f,
            FireRate = 0.75f,
            Ammo = 14,
            SpriteScale = 3.2f,
            LightRadius = 200.0f,
            LightEnergy = 2.0f,
            ShotColor = RgbaColor.FromFloat(0.55f, 1.0f, 0.55f),
        });

        Add(new WeaponDefinition
        {
            WeaponType = WeaponType.FlakBurst,
            DisplayName = "FLAK BURST",
            ShotCount = 5,
            Spread = 0.7f,
            SpreadJitter = 0.15f,
            SpeedScale = 0.8f,
            RangeScale = 0.3f,
            DamageScale = 0.5f,
            SplashRadius = 90.0f,
            RadiusScale = 1.5f,
            FireRate = 0.5f,
            Ammo = 20,
            SpriteScale = 1.5f,
            LightRadius = 120.0f,
            ShotColor = RgbaColor.FromFloat(1.0f, 0.8f, 0.3f),
        });

        Add(new WeaponDefinition
        {
            WeaponType = WeaponType.NovaBurst,
            DisplayName = "NOVA BURST",

            // A full ring: twelve shots across TAU, so the aim direction stops mattering
            // and the weapon becomes a panic button.
            ShotCount = 12,
            Spread = Tau,
            MuzzleOffset = 26.0f,
            RangeScale = 0.25f,
            DamageScale = 0.6f,
            SpeedScale = 0.7f,
            SplashRadius = 60.0f,
            FireRate = 1.2f,
            Ammo = 6,
            SpriteScale = 1.4f,
            LightRadius = 130.0f,
            LightEnergy = 1.6f,
            ShotColor = RgbaColor.FromFloat(1.0f, 1.0f, 0.85f),
        });

        Add(new WeaponDefinition
        {
            WeaponType = WeaponType.RicochetGun,
            DisplayName = "RICOCHET GUN",
            ShotCount = 2,
            LateralSpacing = WingSpacing * 1.2f,
            Bounces = 4,
            RangeScale = 2.0f,
            DamageScale = 0.9f,
            FireRate = 0.3f,
            Ammo = 40,
            SpriteScale = 1.2f,
            ShotColor = RgbaColor.FromFloat(0.8f, 1.0f, 0.4f),
        });

        return table;
    }
}
