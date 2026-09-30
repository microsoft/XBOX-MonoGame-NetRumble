namespace NetRumble.Core.Tuning;

/// <summary>
/// The shipped tuning values, replacing the <c>.tres</c> resources under
/// <c>assets/tuning/</c> and the <c>preload</c> tables in <c>scripts/autoload/assets.gd</c>.
/// </summary>
/// <remarks>
/// <para>
/// Each instance below sets only the properties its source <c>.tres</c> actually
/// overrode; everything else comes from the class defaults, which mirror the GDScript
/// <c>@export</c> defaults. That keeps this file diffable against the original resources
/// line for line.
/// </para>
/// <para>
/// These are compile-time constants rather than data files. Godot's motivation for
/// <c>.tres</c> was inspector editing, which has no MonoGame equivalent, so the runtime
/// cost and failure modes of parsing data at startup would buy nothing here. If external
/// retuning is ever wanted, add a JSON overlay that populates these instances - note that
/// it must populate rather than replace them, or a partial file would silently reset the
/// mine and rocket back to laser defaults.
/// </para>
/// </remarks>
public static class TuningLibrary
{
    public static ShipTuning Ship { get; } = new();

    public static AsteroidTuning Asteroid { get; } = new();

    public static PlayerPalette Palette { get; } = new();

    public static ProjectileTuning Laser { get; } = new()
    {
        ProjectileType = ProjectileType.Laser,
        Texture = "Projectile_Laser",
    };

    public static ProjectileTuning Mine { get; } = new()
    {
        ProjectileType = ProjectileType.Mine,
        Mass = 5.0f,
        Radius = 10.0f,
        Velocity = 80.0f,
        Health = 150.0f,
        Duration = 20.0f,
        DamageAmount = 200.0f,
        DamageRadius = 300.0f,
        CanDamageOwner = false,
        DragPerSecond = 0.9f,
        MinimumVelocitySquared = 100.0f,
        RotationSpeed = 1.0f,
        Texture = "Projectile_Mine",
    };

    public static ProjectileTuning Rocket { get; } = new()
    {
        ProjectileType = ProjectileType.Rocket,
        Mass = 10.0f,
        Radius = 8.0f,
        Velocity = 650.0f,
        Health = 1.0f,
        Duration = 4.0f,
        DamageAmount = 150.0f,
        DamageRadius = 128.0f,
        CanDamageOwner = false,
        Texture = "Projectile_Rocket",
    };

    private static readonly Dictionary<ProjectileType, ProjectileTuning> Projectiles = new()
    {
        [ProjectileType.Laser] = Laser,
        [ProjectileType.Mine] = Mine,
        [ProjectileType.Rocket] = Rocket,
    };

    /// <summary>
    /// The match rules. Deathmatch - free-for-all, four players, first to five kills - is
    /// the only mode the game has.
    /// </summary>
    /// <remarks>
    /// There used to be three, selected on a setup screen before the lobby: Deathmatch,
    /// Team Deathmatch (8 players) and Big Team Battle (16). They are gone, and with them
    /// the <c>GameModeType</c> enum and the <c>GameMode</c> wire message that published a
    /// host's choice to its clients. Nothing chooses any more, so nothing needs telling.
    /// </remarks>
    public static GameModeConfig GameMode { get; } = new()
    {
        DisplayName = "Deathmatch",
        Description = "Free-for-all battle!\nFirst to 5 kills.",
        PlayerCount = 4,
        TargetScore = 5,
        TimeLimit = 600.0f,
    };

    /// <summary>Tuning for a projectile type, falling back to the laser.</summary>
    public static ProjectileTuning Projectile(ProjectileType type)
        => Projectiles.TryGetValue(type, out var tuning) ? tuning : Laser;

    /// <summary>
    /// Shortens the match time limit. <b>Debug developer switch</b>, driven by
    /// <c>--match-seconds=</c> and never applied on a normal run.
    /// </summary>
    /// <remarks>
    /// A Deathmatch runs for ten minutes, which put the end of a match out of reach of any
    /// unattended run and meant the win conditions, the standings and the results screen
    /// went unexecuted for the whole port. Compressing the clock is the smallest change
    /// that lets a real match actually finish while still going through every state the
    /// real one does; the alternative, waiting for a bot to score five kills, is neither
    /// bounded nor reliable.
    /// </remarks>
    /// <param name="seconds">New limit. Values of zero or less are ignored.</param>
    public static void OverrideMatchTimeLimit(float seconds)
    {
        if (seconds <= 0.0f)
        {
            return;
        }

        GameMode.TimeLimit = seconds;
    }

    /// <summary>
    /// Definition for a power-up type, or the double-laser default for an unknown type,
    /// matching <c>Assets.power_up_definition</c>.
    /// </summary>
    /// <remarks>
    /// Delegates to <see cref="PickupLibrary"/>, which owns the thirty-two-entry table.
    /// Kept here so the existing call sites keep working.
    /// </remarks>
    public static PowerUpDefinition PowerUp(PowerUpType type) => PickupLibrary.Get(type);

    /// <summary>
    /// Tuning for the projectile a weapon fires.
    /// </summary>
    /// <remarks>
    /// Only the projectile <i>pool</i> is a real type. Twenty weapons share three bodies,
    /// so the pool comes from the weapon's definition and everything that makes the
    /// weapon distinct is applied on top as a <see cref="ShotSpec"/>.
    /// </remarks>
    public static ProjectileTuning ProjectileForWeapon(WeaponType weapon)
        => Projectile(WeaponLibrary.Get(weapon).ProjectileType);
}
