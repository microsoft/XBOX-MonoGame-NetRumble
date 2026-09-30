namespace NetRumble.Core.Tuning;

/// <summary>
/// Describes one collectible pickup. Ported from
/// <c>scripts/gameplay/tuning/power_up_definition.gd</c>.
/// </summary>
/// <remarks>
/// <para>
/// Pickups come in three flavours - weapons, timed buffs and instant restores - so the
/// definition carries a <see cref="Kind"/> that says which of the payload fields is
/// meaningful.
/// </para>
/// <para>
/// The project ships exactly three power-up textures and there are thirty-two pickups.
/// Every definition picks one of the three as its silhouette and supplies a
/// <see cref="Tint"/> and a short <see cref="Label"/>, which the game layer draws over
/// the sprite. A player reads the colour at a glance and the label when they are close
/// enough for it to matter.
/// </para>
/// </remarks>
public sealed class PowerUpDefinition
{
    public PowerUpType PowerUpType { get; set; } = PowerUpType.DoubleLaser;

    public PickupKind Kind { get; set; } = PickupKind.Weapon;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Key into the game layer's texture registry.</summary>
    public string Texture { get; set; } = string.Empty;

    // --- Payload ------------------------------------------------------------

    /// <summary>Meaningful when <see cref="Kind"/> is <see cref="PickupKind.Weapon"/>.</summary>
    public WeaponType WeaponGranted { get; set; } = WeaponType.DoubleLaser;

    /// <summary>Meaningful when <see cref="Kind"/> is <see cref="PickupKind.Buff"/>.</summary>
    public BuffType BuffGranted { get; set; } = BuffType.RapidFire;

    public float BuffDuration { get; set; } = 12.0f;

    /// <summary>
    /// Meaningful when <see cref="Kind"/> is <see cref="PickupKind.Restore"/>. Applied on
    /// top of the collector's current values and clamped to their maxima by the ship.
    /// </summary>
    public float RestoreHealth { get; set; }

    public float RestoreShield { get; set; }

    /// <summary>Relative likelihood of this pickup being chosen when a drop spawns.</summary>
    public float SpawnWeight { get; set; } = 1.0f;

    // --- Body ---------------------------------------------------------------

    public float Radius { get; set; } = 20.0f;

    // --- Presentation -------------------------------------------------------

    /// <summary>Sprite tint, and the colour of the light the pickup casts.</summary>
    public RgbaColor Tint { get; set; } = RgbaColor.White;

    /// <summary>
    /// Two or three characters drawn across the pickup so it is identifiable without
    /// dedicated art.
    /// </summary>
    public string Label { get; set; } = string.Empty;

    public float RotationSpeed { get; set; } = 2.0f;
    public float PulseAmplitude { get; set; } = 0.1f;
    public float PulseRate { get; set; } = 0.1f;
    public float LightRadius { get; set; } = 110.0f;
}
