namespace NetRumble.Core.Tuning;

/// <summary>
/// The shot-affecting half of a <see cref="WeaponDefinition"/>, resolved by the authority
/// and replicated verbatim with every projectile spawn.
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>WeaponDefinition.to_shot_spec()</c>. Clients never look a weapon up:
/// they are told, per shot, exactly what the authority built. That keeps the two sides in
/// step even when a client is running a build whose weapon table has been retuned, and it
/// means a client only needs the dozen numbers that change how a shot looks and how long
/// it lives - damage and splash are resolved by the authority and arrive as detonation
/// results.
/// </para>
/// <para>
/// This rides the wire once per shot, and the scatter-class weapons send eight at a time,
/// so it is kept to the fields that actually change client-side behaviour.
/// </para>
/// <para>
/// Every field is attacker-controlled on receipt. <see cref="Sanitized"/> is the single
/// place that is enforced - see the remarks there for why the clamps are what they are.
/// </para>
/// </remarks>
public readonly record struct ShotSpec(
    float DamageScale,
    float SpeedScale,
    float RangeScale,
    float RadiusScale,
    float SplashRadius,
    int Pierce,
    int Bounces,
    float HomingRate,
    float SpriteScale,
    float LightRadius,
    float LightEnergy,
    RgbaColor ShotColor)
{
    /// <summary>Upper bound on every multiplier, and on the derived body values.</summary>
    /// <remarks>
    /// The largest scale in the shipped table is the railgun's 5x speed, so this leaves
    /// generous headroom for retuning while still bounding what a hostile peer can ask
    /// for. Without it a crafted spec could request a projectile radius or lifetime large
    /// enough to stall the collision broadphase.
    /// </remarks>
    public const float MaxScale = 16.0f;

    /// <summary>Upper bound on pierce and bounce counts.</summary>
    /// <remarks>
    /// Bounces are the dangerous one: each reflection is extra work for a shot that would
    /// otherwise have been retired, so an unbounded count is a way to keep a projectile
    /// alive indefinitely. The table's maximum is the railgun's 8 pierce.
    /// </remarks>
    public const int MaxHits = 32;

    /// <summary>
    /// The smallest a multiplicative scale may be.
    /// </summary>
    /// <remarks>
    /// Not zero, because every one of these is a multiplier on a value the projectile
    /// needs to be non-zero to exist meaningfully: a zero speed scale is a shot that never
    /// leaves the muzzle, a zero range scale is one that expires on the frame it spawns,
    /// and a zero radius scale is one that can never collide with anything. Each would be
    /// a projectile that is alive, replicated, and inert - which is harder to recognise as
    /// wrong than one that is simply out of range.
    /// </remarks>
    public const float MinScale = 0.01f;

    /// <summary>The spec a shot falls back to when nothing overrides it.</summary>
    public static ShotSpec Default { get; } = new(
        1.0f, 1.0f, 1.0f, 1.0f, -1.0f, 0, 0, 0.0f, 1.0f, 90.0f, 1.0f, new RgbaColor(0, 0, 0, 0));

    /// <summary>
    /// Returns a copy with every field forced into a range the simulation can survive.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A projectile spawn is a host-to-client message, so on a client this arrives from
    /// whoever the transport believes is the host. The transport now authenticates that
    /// (see <c>design-notes.md</c> Phase 24), but a compromised or buggy host is still
    /// able to send whatever it likes, and these values feed the body's radius, lifetime
    /// and speed directly.
    /// </para>
    /// <para>
    /// Non-finite values are the case that matters most: a NaN scale propagates into the
    /// body's position and poisons every snapshot built from it thereafter, which is far
    /// harder to diagnose than an out-of-range shot. They are replaced with the default
    /// rather than rejected, because dropping the spawn would desynchronise the pools.
    /// </para>
    /// </remarks>
    public ShotSpec Sanitized() => new(
        Scale(DamageScale, 1.0f),
        Scale(SpeedScale, 1.0f),
        Scale(RangeScale, 1.0f),
        Scale(RadiusScale, 1.0f),
        float.IsFinite(SplashRadius) ? Math.Clamp(SplashRadius, -1.0f, 4096.0f) : -1.0f,
        Math.Clamp(Pierce, 0, MaxHits),
        Math.Clamp(Bounces, 0, MaxHits),
        NonNegative(HomingRate, 0.0f),
        Scale(SpriteScale, 1.0f),
        float.IsFinite(LightRadius) ? Math.Clamp(LightRadius, 0.0f, 4096.0f) : 0.0f,
        NonNegative(LightEnergy, 1.0f),
        ShotColor);

    /// <summary>Clamps a multiplier that must stay strictly positive to remain meaningful.</summary>
    private static float Scale(float value, float fallback)
        => float.IsFinite(value) ? Math.Clamp(value, MinScale, MaxScale) : fallback;

    /// <summary>
    /// Clamps a value for which zero is a legitimate answer - a homing rate of zero is
    /// simply a shot that does not steer, and a light energy of zero is one that does not
    /// glow.
    /// </summary>
    private static float NonNegative(float value, float fallback)
        => float.IsFinite(value) ? Math.Clamp(value, 0.0f, MaxScale) : fallback;
}
