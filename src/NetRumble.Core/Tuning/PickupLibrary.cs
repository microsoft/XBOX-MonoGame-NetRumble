namespace NetRumble.Core.Tuning;

/// <summary>
/// The thirty-two-entry pickup table: twenty weapons, ten buffs and two restores. Ported
/// from <c>scripts/gameplay/tuning/pickup_library.gd</c>.
/// </summary>
/// <remarks>
/// <para>
/// Like <see cref="WeaponLibrary"/> this lives in code. Thirty-two separate tuning assets
/// would each be a dozen lines of near-identical boilerplate, and the drop table has to be
/// balanced as a whole - which is far easier to do when every row and every spawn weight
/// is visible at once.
/// </para>
/// <para>
/// Weapon pickups are simply mirrored off <see cref="WeaponLibrary"/>: their display name
/// comes from the weapon they grant, so retuning or renaming a weapon does not leave a
/// stale pickup behind. Only their colour and label are stated here.
/// </para>
/// </remarks>
public static class PickupLibrary
{
    /// <summary>
    /// Textures the pickups are drawn from. There are three, and thirty-two pickups, so
    /// the silhouette indicates the broad family and the tint plus label identifies the
    /// individual pickup.
    /// </summary>
    private const string TextureWeapon = "PowerUp_DoubleLaser";
    private const string TextureOrdnance = "PowerUp_Rocket";
    private const string TextureSupport = "PowerUp_TripleLaser";

    private static readonly Dictionary<PowerUpType, PowerUpDefinition> Table = [];

    /// <summary>
    /// <see cref="PowerUpType"/> repeated in proportion to its spawn weight.
    /// </summary>
    /// <remarks>
    /// The weights are expanded once into a flat list of repeated enumerators rather than
    /// being summed and searched on every draw. Drops are frequent now - one every second
    /// or two, for the whole match - so the draw happens often enough to be worth making a
    /// single list index, and the table is small enough that the expansion costs nothing.
    /// </remarks>
    private static readonly List<PowerUpType> WeightedTypes = [];

    static PickupLibrary() => BuildTable();

    public static PowerUpDefinition Get(PowerUpType type)
        => Table.TryGetValue(type, out var definition) ? definition : Table[PowerUpType.DoubleLaser];

    public static IReadOnlyCollection<PowerUpType> AllTypes => Table.Keys;

    /// <summary>Picks a pickup honouring <see cref="PowerUpDefinition.SpawnWeight"/>.</summary>
    public static PowerUpType RandomType(Random random)
        => WeightedTypes[random.Next(WeightedTypes.Count)];

    private static void BuildTable()
    {
        // --- Weapon pickups -------------------------------------------------
        // One per weapon, including a "laser refit" so a player who wants to dump an
        // awkward exotic can pick their way back to the reliable default.
        AddWeapon(PowerUpType.LaserRefit, WeaponType.Laser, 0.85f, 0.9f, 1.0f, "LSR", TextureWeapon, 0.6f);
        AddWeapon(PowerUpType.DoubleLaser, WeaponType.DoubleLaser, 0.6f, 0.85f, 1.0f, "II", TextureWeapon, 1.6f);
        AddWeapon(PowerUpType.TripleLaser, WeaponType.TripleLaser, 0.5f, 1.0f, 0.9f, "III", TextureSupport, 1.4f);
        AddWeapon(PowerUpType.QuadLaser, WeaponType.QuadLaser, 0.6f, 0.95f, 1.0f, "IV", TextureWeapon, 1.2f);
        AddWeapon(PowerUpType.SpreadShot, WeaponType.SpreadShot, 1.0f, 0.85f, 0.4f, "SPR", TextureSupport, 1.2f);
        AddWeapon(PowerUpType.ScatterGun, WeaponType.ScatterGun, 1.0f, 0.6f, 0.25f, "SCT", TextureSupport, 1.0f);
        AddWeapon(PowerUpType.ShotgunBlast, WeaponType.ShotgunBlast, 1.0f, 0.45f, 0.35f, "SHT", TextureSupport, 1.0f);
        AddWeapon(PowerUpType.Vulcan, WeaponType.Vulcan, 1.0f, 0.95f, 0.55f, "VUL", TextureWeapon, 1.0f);
        AddWeapon(PowerUpType.BeamLance, WeaponType.BeamLance, 0.55f, 1.0f, 0.75f, "BEA", TextureWeapon, 0.9f);
        AddWeapon(PowerUpType.PulseBeam, WeaponType.PulseBeam, 0.5f, 0.8f, 1.0f, "PLS", TextureWeapon, 0.9f);
        AddWeapon(PowerUpType.Railgun, WeaponType.Railgun, 0.85f, 0.7f, 1.0f, "RAI", TextureWeapon, 0.6f);
        AddWeapon(PowerUpType.ChainLightning, WeaponType.ChainLightning, 0.7f, 0.9f, 1.0f, "CHN", TextureWeapon, 0.9f);
        AddWeapon(PowerUpType.Rocket, WeaponType.Rocket, 1.0f, 0.5f, 0.35f, "RKT", TextureOrdnance, 1.2f);
        AddWeapon(PowerUpType.HomingMissile, WeaponType.HomingMissile, 1.0f, 0.55f, 0.55f, "HOM", TextureOrdnance, 0.8f);
        AddWeapon(PowerUpType.SwarmMissiles, WeaponType.SwarmMissiles, 1.0f, 0.7f, 0.4f, "SWM", TextureOrdnance, 0.7f);
        AddWeapon(PowerUpType.SeekerMines, WeaponType.SeekerMines, 1.0f, 0.4f, 0.7f, "SKR", TextureOrdnance, 0.7f);
        AddWeapon(PowerUpType.PlasmaCannon, WeaponType.PlasmaCannon, 0.55f, 1.0f, 0.55f, "PLA", TextureOrdnance, 0.8f);
        AddWeapon(PowerUpType.FlakBurst, WeaponType.FlakBurst, 1.0f, 0.8f, 0.3f, "FLK", TextureOrdnance, 0.9f);
        AddWeapon(PowerUpType.NovaBurst, WeaponType.NovaBurst, 1.0f, 1.0f, 0.85f, "NVA", TextureOrdnance, 0.6f);
        AddWeapon(PowerUpType.RicochetGun, WeaponType.RicochetGun, 0.8f, 1.0f, 0.4f, "RIC", TextureWeapon, 0.9f);

        // --- Buffs ----------------------------------------------------------
        // Durations are short because drops are frequent: a buff is a window of advantage
        // a player fights to make use of, not a state they settle into.
        AddBuff(PowerUpType.BuffRapidFire, BuffType.RapidFire, "RAPID FIRE", 12.0f, 1.0f, 0.85f, 0.2f, ">>");
        AddBuff(PowerUpType.BuffAfterburner, BuffType.Afterburner, "AFTERBURNER", 12.0f, 0.4f, 0.8f, 1.0f, "^^");
        AddBuff(PowerUpType.BuffCloak, BuffType.Cloak, "CLOAK", 8.0f, 0.6f, 0.6f, 0.8f, "()");
        AddBuff(PowerUpType.BuffDoubleDamage, BuffType.DoubleDamage, "DOUBLE DAMAGE", 12.0f, 1.0f, 0.3f, 0.3f, "x2");
        AddBuff(PowerUpType.BuffOvershield, BuffType.Overshield, "OVERSHIELD", 15.0f, 0.4f, 1.0f, 1.0f, "[]");
        AddBuff(PowerUpType.BuffRegeneration, BuffType.Regeneration, "REGENERATION", 15.0f, 0.4f, 1.0f, 0.5f, "++");
        AddBuff(PowerUpType.BuffQuickCharge, BuffType.QuickCharge, "QUICK CHARGE", 15.0f, 0.5f, 0.9f, 1.0f, "~~");
        AddBuff(PowerUpType.BuffMultiShot, BuffType.MultiShot, "MULTI SHOT", 12.0f, 1.0f, 0.6f, 1.0f, "*");
        AddBuff(PowerUpType.BuffRicochet, BuffType.Ricochet, "RICOCHET", 12.0f, 0.8f, 1.0f, 0.4f, "/\\");
        AddBuff(PowerUpType.BuffVampiric, BuffType.Vampiric, "VAMPIRIC", 12.0f, 0.9f, 0.2f, 0.5f, "<3");

        // --- Restores -------------------------------------------------------
        // Weighted heavily: with this much ordnance in the air a match without a steady
        // supply of repairs turns into a respawn queue.
        AddRestore(PowerUpType.RestoreShield, "SHIELD BOOST", 0.0f, 100.0f, 0.35f, 0.8f, 1.0f, "SHL", 3.0f);
        AddRestore(PowerUpType.RestoreHull, "HULL REPAIR", 25.0f, 25.0f, 0.4f, 1.0f, 0.6f, "HUL", 2.5f);
    }

    private static void AddWeapon(
        PowerUpType type,
        WeaponType weapon,
        float r,
        float g,
        float b,
        string label,
        string texture,
        float weight)
        => Register(new PowerUpDefinition
        {
            PowerUpType = type,
            Kind = PickupKind.Weapon,
            WeaponGranted = weapon,
            DisplayName = WeaponLibrary.DisplayName(weapon),
            Tint = RgbaColor.FromFloat(r, g, b),
            Label = label,
            Texture = texture,
            SpawnWeight = weight,
        });

    private static void AddBuff(
        PowerUpType type,
        BuffType buff,
        string displayName,
        float duration,
        float r,
        float g,
        float b,
        string label)
        => Register(new PowerUpDefinition
        {
            PowerUpType = type,
            Kind = PickupKind.Buff,
            BuffGranted = buff,
            BuffDuration = duration,
            DisplayName = displayName,
            Tint = RgbaColor.FromFloat(r, g, b),
            Label = label,
            Texture = TextureSupport,
            SpawnWeight = 1.4f,
            RotationSpeed = -2.5f,
        });

    private static void AddRestore(
        PowerUpType type,
        string displayName,
        float health,
        float shield,
        float r,
        float g,
        float b,
        string label,
        float weight)
        => Register(new PowerUpDefinition
        {
            PowerUpType = type,
            Kind = PickupKind.Restore,
            RestoreHealth = health,
            RestoreShield = shield,
            DisplayName = displayName,
            Tint = RgbaColor.FromFloat(r, g, b),
            Label = label,
            Texture = TextureSupport,
            SpawnWeight = weight,
            PulseAmplitude = 0.2f,
            LightRadius = 150.0f,
        });

    private static void Register(PowerUpDefinition definition)
    {
        Table[definition.PowerUpType] = definition;

        // Weights are expressed in tenths so fractional values survive the expansion.
        var slots = (int)MathF.Round(definition.SpawnWeight * 10.0f);

        for (var i = 0; i < slots; i++)
        {
            WeightedTypes.Add(definition.PowerUpType);
        }
    }
}
