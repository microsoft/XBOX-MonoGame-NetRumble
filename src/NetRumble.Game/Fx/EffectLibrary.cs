using Microsoft.Xna.Framework;
using NetRumble.Core;

namespace NetRumble.Game.Fx;

/// <summary>
/// The event-to-effect and event-to-sound tables, ported from
/// <c>assets/fx/gameplay_events.tres</c> and <c>scripts/fx/particle_manager.gd</c>.
/// </summary>
/// <remarks>
/// <para>
/// Both tables originate in the C++ sample: the effect mapping is
/// <c>Game/Gameplay/GameplayEffectManager.cpp</c> and the sound mapping is
/// <c>GameplayScreen::HandleGameplayEvent</c>. The Godot port split them across a resource
/// file and a script; they are reunited here because there is no inspector to serve.
/// </para>
/// <para>
/// <b>The two tables are not the same shape and must not be merged.</b> Three events fire a
/// sound with no particles (<see cref="GameplayEventType.LaserFired"/>,
/// <see cref="GameplayEventType.RocketFired"/>), one fires particles with no sound
/// (<see cref="GameplayEventType.LaserImpact"/>), one fires neither
/// (<see cref="GameplayEventType.RocketTrail"/>, which is a local-only cosmetic event the
/// original never gave an effect to), and
/// <see cref="GameplayEventType.ShipDestroyed"/> fires two sounds.
/// </para>
/// </remarks>
public static class EffectLibrary
{
    private static readonly Vector2 Rightward = new(1f, 0f);

    /// <summary>Every shipped effect emits into a full circle (Godot <c>spread = 180</c>).</summary>
    private const float FullCircleSpread = 180f;

    /// <summary>Godot <c>scale_amount_min</c>/<c>max</c>, identical across all seven effects.</summary>
    private const float ScaleMin = 0.25f;

    private const float ScaleMax = 0.75f;

    /// <summary>From <c>laser_impact.tscn</c>.</summary>
    public static readonly ParticleBurstDefinition LaserImpact = new(
        "LaserImpact",
        [
            new ParticleEmitterDefinition(
                TextureKey: "Particle_Spark",
                Amount: 10,
                Lifetime: 0.35f,
                Direction: Rightward,
                SpreadDegrees: FullCircleSpread,
                Gravity: Vector2.Zero,
                InitialVelocityMin: 52f,
                InitialVelocityMax: 130f,
                Damping: 104f,
                ScaleMin: ScaleMin,
                ScaleMax: ScaleMax,
                Color: Color.White,
                Additive: true),
        ]);

    /// <summary>From <c>ship_spawn.tscn</c>.</summary>
    public static readonly ParticleBurstDefinition ShipSpawn = new(
        "ShipSpawn",
        [
            new ParticleEmitterDefinition(
                TextureKey: "Particle_Default",
                Amount: 24,
                Lifetime: 0.8f,
                Direction: Rightward,
                SpreadDegrees: FullCircleSpread,
                Gravity: Vector2.Zero,
                InitialVelocityMin: 36f,
                InitialVelocityMax: 90f,
                Damping: 72f,
                ScaleMin: ScaleMin,
                ScaleMax: ScaleMax,
                Color: Color.White,
                Additive: true),
        ]);

    /// <summary>
    /// From <c>ship_explosion.tscn</c>. The only two-emitter effect: orange sparks with a
    /// slower, longer-lived grey smoke layer behind them.
    /// </summary>
    /// <remarks>
    /// The smoke emitter carries no <c>CanvasItemMaterial</c>, so it alpha-blends while the
    /// sparks add. Keeping smoke out of the additive pass is what stops the explosion
    /// blowing out to white at its centre.
    /// </remarks>
    public static readonly ParticleBurstDefinition ShipExplosion = new(
        "ShipExplosion",
        [
            new ParticleEmitterDefinition(
                TextureKey: "Particle_Spark",
                Amount: 48,
                Lifetime: 1.0f,
                Direction: Rightward,
                SpreadDegrees: FullCircleSpread,
                Gravity: Vector2.Zero,
                InitialVelocityMin: 88f,
                InitialVelocityMax: 220f,
                Damping: 176f,
                ScaleMin: ScaleMin,
                ScaleMax: ScaleMax,
                Color: new Color(255, 165, 0),
                Additive: true),
            new ParticleEmitterDefinition(
                TextureKey: "Particle_Smoke",
                Amount: 20,
                Lifetime: 1.5f,
                Direction: Rightward,
                SpreadDegrees: FullCircleSpread,
                Gravity: Vector2.Zero,
                InitialVelocityMin: 32f,
                InitialVelocityMax: 80f,
                Damping: 64f,
                ScaleMin: ScaleMin,
                ScaleMax: ScaleMax,
                Color: new Color(128, 128, 128),
                Additive: false),
        ]);

    /// <summary>From <c>mine_detonation.tscn</c>.</summary>
    public static readonly ParticleBurstDefinition MineDetonation = new(
        "MineDetonation",
        [
            new ParticleEmitterDefinition(
                TextureKey: "Particle_Spark",
                Amount: 40,
                Lifetime: 1.0f,
                Direction: Rightward,
                SpreadDegrees: FullCircleSpread,
                Gravity: Vector2.Zero,
                InitialVelocityMin: 104f,
                InitialVelocityMax: 260f,
                Damping: 208f,
                ScaleMin: ScaleMin,
                ScaleMax: ScaleMax,
                Color: new Color(255, 69, 0),
                Additive: true),
        ]);

    /// <summary>From <c>rocket_detonation.tscn</c>.</summary>
    public static readonly ParticleBurstDefinition RocketDetonation = new(
        "RocketDetonation",
        [
            new ParticleEmitterDefinition(
                TextureKey: "Particle_Spark",
                Amount: 32,
                Lifetime: 0.8f,
                Direction: Rightward,
                SpreadDegrees: FullCircleSpread,
                Gravity: Vector2.Zero,
                InitialVelocityMin: 84f,
                InitialVelocityMax: 210f,
                Damping: 168f,
                ScaleMin: ScaleMin,
                ScaleMax: ScaleMax,
                Color: new Color(255, 165, 0),
                Additive: true),
        ]);

    /// <summary>From <c>power_up_burst.tscn</c>.</summary>
    public static readonly ParticleBurstDefinition PowerUpBurst = new(
        "PowerUpBurst",
        [
            new ParticleEmitterDefinition(
                TextureKey: "Particle_Default",
                Amount: 20,
                Lifetime: 0.6f,
                Direction: Rightward,
                SpreadDegrees: FullCircleSpread,
                Gravity: Vector2.Zero,
                InitialVelocityMin: 40f,
                InitialVelocityMax: 100f,
                Damping: 80f,
                ScaleMin: ScaleMin,
                ScaleMax: ScaleMax,
                Color: new Color(255, 255, 0),
                Additive: true),
        ]);

    /// <summary>From <c>asteroid_impact.tscn</c>.</summary>
    public static readonly ParticleBurstDefinition AsteroidImpact = new(
        "AsteroidImpact",
        [
            new ParticleEmitterDefinition(
                TextureKey: "Particle_Spark",
                Amount: 8,
                Lifetime: 0.3f,
                Direction: Rightward,
                SpreadDegrees: FullCircleSpread,
                Gravity: Vector2.Zero,
                InitialVelocityMin: 36f,
                InitialVelocityMax: 90f,
                Damping: 72f,
                ScaleMin: ScaleMin,
                ScaleMax: ScaleMax,
                Color: new Color(211, 211, 211),
                Additive: true),
        ]);

    /// <summary>
    /// Event to effect, from the <c>effects</c> dictionary in
    /// <c>assets/fx/gameplay_events.tres</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="GameplayEventType.PowerUpSpawned"/> and
    /// <see cref="GameplayEventType.PowerUpCollected"/> deliberately share one effect; the
    /// resource pointed both keys at the same <c>PackedScene</c>.
    /// </remarks>
    public static readonly IReadOnlyDictionary<GameplayEventType, ParticleBurstDefinition> Effects =
        new Dictionary<GameplayEventType, ParticleBurstDefinition>
        {
            [GameplayEventType.LaserImpact] = LaserImpact,
            [GameplayEventType.ShipSpawned] = ShipSpawn,
            [GameplayEventType.ShipDestroyed] = ShipExplosion,
            [GameplayEventType.MineDetonated] = MineDetonation,
            [GameplayEventType.RocketDetonated] = RocketDetonation,
            [GameplayEventType.PowerUpSpawned] = PowerUpBurst,
            [GameplayEventType.PowerUpCollected] = PowerUpBurst,
            [GameplayEventType.AsteroidImpact] = AsteroidImpact,
        };

    /// <summary>
    /// Event to sound key(s), from <c>_event_sounds</c> in <c>particle_manager.gd</c>.
    /// </summary>
    /// <remarks>
    /// Keys resolve against <see cref="Content.AssetRegistry.AudioPaths"/> or
    /// <see cref="Content.AssetRegistry.SoundVariants"/>; <c>LaserFire</c> and
    /// <c>RocketFire</c> are the two randomised pools.
    /// </remarks>
    public static readonly IReadOnlyDictionary<GameplayEventType, string[]> Sounds =
        new Dictionary<GameplayEventType, string[]>
        {
            [GameplayEventType.LaserFired] = ["LaserFire"],
            [GameplayEventType.ShipSpawned] = ["PlayerSpawn"],

            // Two clips, in this order: the shockwave is the low thump under the crack of
            // the explosion itself. The original layered them rather than authoring one clip.
            [GameplayEventType.ShipDestroyed] = ["ExplosionShockwave", "ExplosionLarge"],
            [GameplayEventType.MineDetonated] = ["ExplosionLarge"],
            [GameplayEventType.RocketFired] = ["RocketFire"],
            [GameplayEventType.RocketDetonated] = ["ExplosionMedium"],
            [GameplayEventType.PowerUpSpawned] = ["PowerUpSpawn"],
            [GameplayEventType.PowerUpCollected] = ["PowerUpTouch"],
            [GameplayEventType.AsteroidImpact] = ["AsteroidTouch"],
        };
}
