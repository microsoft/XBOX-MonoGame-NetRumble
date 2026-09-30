using System.Numerics;
using NetRumble.Core.Simulation;
using NetRumble.Core.Tuning;

namespace NetRumble.Core.Objects;

/// <summary>
/// A collectible pickup that grants a weapon on contact. Ported from
/// <c>scripts/gameplay/objects/power_up.gd</c>.
/// </summary>
/// <remarks>
/// Its mass is negligible and it only masks the ships layer, so a ship that touches
/// it collects the pickup without being deflected - which retires the C++
/// <c>mass = -1</c> "immovable" hack.
/// </remarks>
public sealed class PowerUp : GameObject
{
    /// <summary>
    /// Power-ups are simulated bodies purely so the solver can report the ship
    /// contact; a negligible mass keeps them from being pushed or from pushing the
    /// ship that collects them.
    /// </summary>
    private const float ContactMass = 0.001f;

    /// <summary>Elapsed time driving <see cref="PulseScale"/>.</summary>
    private float _pulseElapsed;

    public PowerUpType PowerUpType { get; private set; } = PowerUpType.DoubleLaser;

    public PowerUpDefinition Definition { get; private set; }

    /// <summary>
    /// Renderer hook replacing the Godot <c>pulse</c> animation, which oscillated the
    /// sprite's scale between <c>1 - amplitude</c> and <c>1 + amplitude</c> with cubic
    /// keyframes at the quarter points of the period.
    /// </summary>
    /// <remarks>
    /// A pure sine wave passes exactly through the same five keyframes the Godot
    /// animation used (base, +amplitude, base, -amplitude, base at 0, T/4, T/2, 3T/4,
    /// T), so it reproduces that curve without needing an animation-track type in an
    /// assembly that has no rendering framework.
    /// </remarks>
    public float PulseScale
    {
        get
        {
            if (Definition.PulseRate <= 0.0f)
            {
                return 1.0f;
            }

            return 1.0f + (Definition.PulseAmplitude * MathF.Sin(_pulseElapsed / Definition.PulseRate));
        }
    }

    public PowerUp()
    {
        ObjectType = GameObjectType.PowerUp;
        SetCollisionFilter(CollisionLayer.Pickups, CollisionLayer.Ships);
        Definition = ApplyDefinition(PowerUpType);
    }

    public void Setup(PowerUpType type)
    {
        UniqueId = AllocateId();
        Definition = ApplyDefinition(type);
    }

    public void SetupNetworked(int id, PowerUpType type)
    {
        UniqueId = id;
        Definition = ApplyDefinition(type);
    }

    /// <summary>
    /// Retypes a pooled body at spawn time.
    /// </summary>
    /// <remarks>
    /// Pooled bodies are generic: which pickup they represent is decided when they are
    /// spawned, and replicated alongside the position. That is what lets a two-dozen-body
    /// pool stand in for a thirty-two-entry table - the alternative, one body per type,
    /// caps the field at one of each and makes the pool grow with the table.
    /// </remarks>
    public void AssignType(PowerUpType type) => Definition = ApplyDefinition(type);

    private PowerUpDefinition ApplyDefinition(PowerUpType type)
    {
        PowerUpType = type;
        var definition = TuningLibrary.PowerUp(type);
        Mass = ContactMass;
        Radius = definition.Radius;
        return definition;
    }

    public override void Start()
    {
        Health = 1.0f;
        Velocity = Vector2.Zero;
        _pulseElapsed = 0.0f;
    }

    public override void Tick(float delta)
    {
        Rotation += Definition.RotationSpeed * delta;
        _pulseElapsed += delta;
        base.Tick(delta);
    }

    public override void OnContact(GameObject other)
    {
        if (World is null)
        {
            return;
        }

        if (other is Ship ship && World.IsAuthority)
        {
            World.CollectPowerUp(this, ship);
        }
    }
}
