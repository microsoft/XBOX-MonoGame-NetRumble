namespace NetRumble.Core.Tuning;

/// <summary>
/// Designer-tunable asteroid stats, shared by every asteroid size. Ported from
/// <c>scripts/gameplay/tuning/asteroid_tuning.gd</c>.
/// </summary>
public sealed class AsteroidTuning
{
    // Movement

    public float VelocityInitialMin { get; set; } = 32.0f;
    public float VelocityInitialMax { get; set; } = 96.0f;

    /// <summary>Asteroids stop decaying once they drop below this speed.</summary>
    public float VelocityMinThreshold { get; set; } = 25.0f;

    public float VelocityDecayRate { get; set; } = 0.15f;
    public float VelocityMassRatioToRotationScalar { get; set; } = 0.0017f;

    // Size

    /// <summary>
    /// Radii per <see cref="AsteroidSize"/> tier, smallest first.
    /// </summary>
    /// <remarks>
    /// Each tier is roughly 1.6x the one below it, which keeps a split visibly a step
    /// down without the fragments shrinking to specks after two generations.
    /// </remarks>
    public float RadiusTiny { get; set; } = 14.0f;

    public float RadiusSmall { get; set; } = 24.0f;
    public float RadiusMedium { get; set; } = 40.0f;
    public float RadiusLarge { get; set; } = 64.0f;
    public float RadiusHuge { get; set; } = 96.0f;

    /// <summary>Health and mass are both derived from the radius.</summary>
    public float RadiusHealthRatio { get; set; } = 1.5f;

    public float RadiusMassRatio { get; set; } = 0.5f;

    // Splitting

    /// <summary>
    /// Fragments a destroyed asteroid breaks into. The tier below is spawned, so a
    /// <see cref="AsteroidSize.Huge"/> rock ultimately yields <c>SplitCount^4</c> tiny
    /// fragments - keep this low.
    /// </summary>
    public int SplitCount { get; set; } = 2;

    /// <summary>
    /// Fragments are pushed apart from the parent's centre at this speed, on top of
    /// whatever momentum the parent had.
    /// </summary>
    public float SplitSpeedMin { get; set; } = 60.0f;

    public float SplitSpeedMax { get; set; } = 140.0f;

    /// <summary>
    /// How far out fragments start from the parent's centre, as a fraction of the space
    /// the parent's radius leaves around the child.
    /// </summary>
    /// <remarks>
    /// Below 1.0 they begin overlapping and the solver blows them apart much harder than
    /// the split velocity intends.
    /// </remarks>
    public float SplitSpawnSpread { get; set; } = 1.15f;

    /// <summary>
    /// Randomness added to each fragment's launch angle, in radians, so a split does not
    /// look like a mechanical rosette.
    /// </summary>
    public float SplitAngleJitter { get; set; } = 0.5f;

    // Damage

    public float MomentumDamageScalar { get; set; } = 0.007f;

    // Presentation

    /// <summary>
    /// Radius, in texels, of the rock painted inside the asteroid textures.
    /// </summary>
    /// <remarks>
    /// The art does not reach the texture's edge, so the sprite is scaled from this rather
    /// than from half the texture's width. That is what keeps the drawn rock the same size
    /// as the collision circle instead of dwarfing it.
    /// </remarks>
    public float TextureRadius { get; set; } = 117.0f;

    /// <summary>Texture registry keys, indexed by asteroid variation.</summary>
    public string[] Textures { get; set; } = ["Asteroid0", "Asteroid1", "Asteroid2"];

    /// <summary>Radius for an asteroid size, falling back to the smallest tier.</summary>
    public float RadiusFor(AsteroidSize size) => size switch
    {
        AsteroidSize.Small => RadiusSmall,
        AsteroidSize.Medium => RadiusMedium,
        AsteroidSize.Large => RadiusLarge,
        AsteroidSize.Huge => RadiusHuge,
        _ => RadiusTiny,
    };

    /// <summary>
    /// The tier a destroyed asteroid of <paramref name="size"/> breaks into, or
    /// <c>null</c> when it is the terminal tier and simply disappears.
    /// </summary>
    /// <remarks>
    /// Godot returned <c>-1</c> for "no split" because GDScript has no nullable enum.
    /// A <see cref="Nullable{T}"/> says the same thing without a sentinel that could be
    /// cast back into a bogus <see cref="AsteroidSize"/>.
    /// </remarks>
    public static AsteroidSize? SplitSizeFor(AsteroidSize size)
        => size <= AsteroidSize.Tiny ? null : size - 1;

    /// <summary>
    /// Texture key for a variation index, wrapping so any value is safe.
    /// </summary>
    /// <remarks>
    /// Uses Godot <c>posmod</c> semantics rather than C# <c>%</c>: the result is always
    /// non-negative, so a negative variation cannot index out of bounds.
    /// </remarks>
    public string TextureFor(int variation)
    {
        if (Textures.Length == 0)
        {
            return string.Empty;
        }

        var index = ((variation % Textures.Length) + Textures.Length) % Textures.Length;
        return Textures[index];
    }
}
