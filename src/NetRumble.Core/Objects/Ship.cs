using System.Numerics;
using NetRumble.Core.Simulation;
using NetRumble.Core.Tuning;

namespace NetRumble.Core.Objects;

/// <summary>
/// A player ship, ported from <c>scripts/gameplay/objects/ship.gd</c> (originally
/// <c>Game/Gameplay/GameObject/Ship.cpp</c>).
/// </summary>
public sealed class Ship : GameObject
{
    private readonly ShipTuning _tuning;

    private float _invulnerabilityTimer;
    private float _shieldRechargeTimer;
    private float _timeToNextFire;
    private float _timeToNextMine;

    public Ship(ShipTuning? tuning = null)
    {
        _tuning = tuning ?? TuningLibrary.Ship;
        ObjectType = GameObjectType.Ship;
        Mass = _tuning.Mass;
        Radius = _tuning.Radius;
        SetCollisionFilter(
            CollisionLayer.Ships,
            CollisionLayer.Ships | CollisionLayer.Asteroids | CollisionLayer.Walls);
    }

    public ShipTuning Tuning => _tuning;

    public int OwnerPeerId { get; set; }

    public int ShipColorId { get; set; }

    public int ShipStyleId { get; set; }

    public RgbaColor ShipColor { get; set; } = RgbaColor.White;

    public float Shield { get; set; }

    public WeaponType PrimaryWeapon { get; private set; } = WeaponType.Laser;

    /// <summary>
    /// Volleys left before the weapon runs dry, or -1 for an unlimited weapon.
    /// </summary>
    public int WeaponAmmo { get; private set; } = -1;

    public int LastDamagedById { get; set; }

    public ShipInput ShipInput { get; } = new();

    /// <summary>True while the spawn protection timer is running.</summary>
    public bool IsInvulnerable => _invulnerabilityTimer > 0.0f;

    // --- Buffs --------------------------------------------------------------

    /// <summary>
    /// How much the buffs currently in effect change the ship.
    /// </summary>
    /// <remarks>
    /// Kept as named constants rather than magic numbers scattered through the query
    /// methods, since these are the numbers that actually decide whether a buff feels
    /// worth chasing.
    /// </remarks>
    private const float RapidFireScale = 0.45f;
    private const float AfterburnerScale = 1.55f;
    private const float DoubleDamageScale = 2.0f;
    private const float OvershieldScale = 2.0f;
    private const float QuickChargeDelayScale = 0.3f;
    private const float QuickChargeRateScale = 2.5f;
    private const float RegenerationPerSecond = 2.5f;
    private const int MultiShotExtraShots = 2;
    private const float MultiShotExtraSpread = 0.35f;
    private const int RicochetExtraBounces = 3;
    private const float VampiricFraction = 0.25f;

    /// <summary>Alpha the ship is drawn at while cloaked.</summary>
    public const float CloakAlpha = 0.22f;

    /// <summary>
    /// Buff type to seconds remaining. Absent means "not active", so the whole buff
    /// system is one dictionary rather than a field per buff.
    /// </summary>
    private readonly Dictionary<BuffType, float> _buffs = [];

    /// <summary>The buffs currently in effect, for the HUD and for replication.</summary>
    public IReadOnlyDictionary<BuffType, float> Buffs => _buffs;

    /// <summary>
    /// Ages every active buff out and drops the ones that have run down.
    /// </summary>
    /// <remarks>
    /// Ticked on every peer rather than only on the authority. Buffs change how the ship
    /// moves and shoots, so a client that kept predicting with a buff that had expired
    /// would drift from the authority and be corrected with a visible snap.
    /// </remarks>
    public void TickBuffs(float delta)
    {
        if (_buffs.Count == 0)
        {
            return;
        }

        // Materialised because the loop erases from the dictionary it walks.
        foreach (var buff in _buffs.Keys.ToArray())
        {
            var remaining = _buffs[buff] - delta;

            if (remaining <= 0.0f)
            {
                _buffs.Remove(buff);
                OnBuffExpired(buff);
            }
            else
            {
                _buffs[buff] = remaining;
            }
        }
    }

    /// <summary>Grants or refreshes a timed buff.</summary>
    /// <remarks>
    /// Re-collecting a buff refreshes it rather than stacking, so a player camping a
    /// spawn cannot accumulate an unbounded advantage.
    /// </remarks>
    public void GrantBuff(BuffType buff, float duration)
    {
        _buffs[buff] = MathF.Max(duration, _buffs.GetValueOrDefault(buff));

        if (buff == BuffType.Overshield)
        {
            Shield = ShieldMaximum;
        }
    }

    public bool HasBuff(BuffType buff) => _buffs.ContainsKey(buff);

    public float BuffTimeRemaining(BuffType buff) => _buffs.GetValueOrDefault(buff);

    /// <summary>Replaces the whole buff table, for a client applying a snapshot.</summary>
    public void ApplyBuffState(IReadOnlyDictionary<BuffType, float> state)
    {
        var hadOvershield = HasBuff(BuffType.Overshield);

        _buffs.Clear();

        foreach (var (buff, remaining) in state)
        {
            _buffs[buff] = remaining;
        }

        // Losing overshield has to shed the extra shield here too, or a client would keep
        // drawing a shield bar past its own maximum until the next damage event.
        if (hadOvershield && !HasBuff(BuffType.Overshield))
        {
            Shield = MathF.Min(Shield, ShieldMaximum);
        }
    }

    private void OnBuffExpired(BuffType buff)
    {
        if (buff == BuffType.Overshield)
        {
            Shield = MathF.Min(Shield, ShieldMaximum);
        }
    }

    /// <summary>Shield ceiling, doubled while overshield is active.</summary>
    public float ShieldMaximum
        => _tuning.ShieldMax * (HasBuff(BuffType.Overshield) ? OvershieldScale : 1.0f);

    public float DamageMultiplier() => HasBuff(BuffType.DoubleDamage) ? DoubleDamageScale : 1.0f;

    public int ExtraShots() => HasBuff(BuffType.MultiShot) ? MultiShotExtraShots : 0;

    public float ExtraSpread() => HasBuff(BuffType.MultiShot) ? MultiShotExtraSpread : 0.0f;

    public int ExtraBounces() => HasBuff(BuffType.Ricochet) ? RicochetExtraBounces : 0;

    /// <summary>
    /// Whether guided weapons should ignore this ship.
    /// </summary>
    /// <remarks>
    /// Cloak hides the ship from homing as well as from the eye, so the buff genuinely
    /// means "cannot be hit" rather than merely "hard to see".
    /// </remarks>
    public bool IsUntargetable() => HasBuff(BuffType.Cloak);

    /// <summary>Returns a share of damage dealt to a vampiric shooter as health.</summary>
    public void CreditDamageDealt(float damageDealt)
    {
        if (damageDealt <= 0.0f || !HasBuff(BuffType.Vampiric))
        {
            return;
        }

        Health = MathF.Min(_tuning.HealthMax, Health + (damageDealt * VampiricFraction));
    }

    // --- Render state -------------------------------------------------------
    // NetRumble.Core has no rendering framework, so the ship publishes what the
    // renderer needs rather than owning sprites. Scale and layering are the
    // renderer's business; only values derived from simulation state appear here.

    /// <summary>Whether the thruster sprite should be drawn this frame.</summary>
    public bool IsThrusting
        => Velocity.LengthSquared() > _tuning.ThrusterVelocityThresholdSquared && IsActive;

    /// <summary>Shield sprite alpha, 0 when the shield is down.</summary>
    public float ShieldAlpha
        => Shield > 0.0f ? _tuning.ShieldAlphaMax * Shield / _tuning.ShieldMax : 0.0f;

    /// <summary>
    /// Phase of the invulnerability colour cycle, in seconds, or null when not
    /// invulnerable.
    /// </summary>
    /// <remarks>
    /// The Godot version drove this with a looping <c>Tween</c> over seven key colours at
    /// 0.2 s each. Only the elapsed phase is simulation state; picking and blending the
    /// colours belongs to the renderer.
    /// </remarks>
    public float? InvulnerabilityPhase { get; private set; }

    public void Setup(PlayerState playerState)
    {
        UniqueId = AllocateId();
        OwnerPeerId = playerState.PeerId;
        ShipColorId = playerState.ShipColorId;
        ShipStyleId = playerState.ShipStyleId;
        ShipColor = TuningLibrary.Palette.ColorAt(ShipColorId);
    }

    public void SetupNetworked(int id, int peerId, int colorId, int styleId)
    {
        UniqueId = id;
        OwnerPeerId = peerId;
        ShipColorId = colorId;
        ShipStyleId = styleId;
        ShipColor = TuningLibrary.Palette.ColorAt(ShipColorId);
    }

    public override void Start()
    {
        Velocity = Vector2.Zero;
        Health = _tuning.HealthMax;
        _buffs.Clear();
        Shield = _tuning.ShieldMax;
        _invulnerabilityTimer = _tuning.InvulnerableTimerMax;
        _shieldRechargeTimer = 0.0f;
        _timeToNextMine = 0.0f;
        SetPrimaryWeapon(WeaponType.Laser);

        // A dead ship is inactive and therefore never ticked, so neither the local input
        // pass nor the remote ageing runs and the last movement and fire vectors stay
        // latched. Without this the ship respawns already thrusting, and already shooting
        // for anyone who died mid-burst.
        ShipInput.Reset();
        InvulnerabilityPhase = 0.0f;
    }

    public override void Tick(float delta)
    {
        TickCooldowns(delta);
        TickBuffs(delta);

        // Remote players' input arrives over an unreliable channel, so the authority ages
        // it out rather than steering their ship on a packet that may be seconds old.
        if (World is { IsAuthority: true } && World.LocalShip != this)
        {
            ShipInput.TickRemoteInput(delta);
        }

        UpdateShield(delta);

        ProcessControlsMovement(delta);
        ProcessControlsWeapon();
        ProcessControlsMine();

        base.Tick(delta);
    }

    public void SetPrimaryWeapon(WeaponType weapon)
    {
        PrimaryWeapon = weapon;
        WeaponAmmo = WeaponLibrary.Get(weapon).Ammo;
        _timeToNextFire = 0.0f;
    }

    /// <summary>
    /// Snapshot form of <see cref="SetPrimaryWeapon"/>.
    /// </summary>
    /// <remarks>
    /// Ammo is spent on the authority and its consequence - the weapon itself - is what
    /// gets replicated, so this must not reset a count that is mid-burst. Refilling on
    /// every snapshot would make a client's ammo readout climb back to full between
    /// shots.
    /// </remarks>
    public void SetPrimaryWeaponNetworked(WeaponType weapon)
    {
        if (PrimaryWeapon == weapon)
        {
            return;
        }

        PrimaryWeapon = weapon;
        WeaponAmmo = WeaponLibrary.Get(weapon).Ammo;
    }

    /// <summary>
    /// Spends one volley of ammunition, dropping back to the laser when the weapon runs
    /// dry. Unlimited weapons are left alone.
    /// </summary>
    public void ConsumeWeaponAmmo()
    {
        if (WeaponAmmo < 0)
        {
            return;
        }

        WeaponAmmo--;

        if (WeaponAmmo <= 0)
        {
            SetPrimaryWeapon(WeaponType.Laser);
        }
    }

    /// <summary>
    /// Relays a remote player's input onto this ship, called by the match director on the
    /// authority.
    /// </summary>
    public void UpdateRemoteInput(Vector2 movement, Vector2 fire, bool deployMine, int sequence)
        => ShipInput.UpdateRemoteInput(movement, fire, deployMine, sequence);

    public void OnMineDeployed() => _timeToNextMine = _tuning.MineDeployRate;

    public void SetInvulnerable(bool invulnerable)
    {
        if (invulnerable)
        {
            _invulnerabilityTimer = _tuning.InvulnerableTimerMax;
            InvulnerabilityPhase = 0.0f;
        }
        else
        {
            _invulnerabilityTimer = 0.0f;
            InvulnerabilityPhase = null;
        }
    }

    public override void TakeDamage(GameObject? source, float damage)
    {
        if (Health <= 0.0f || source is null || IsInvulnerable || damage <= 0.0f)
        {
            return;
        }

        var delay = _tuning.ShieldRechargeDelay;

        if (HasBuff(BuffType.QuickCharge))
        {
            delay *= QuickChargeDelayScale;
        }

        _shieldRechargeTimer = delay;

        if (Shield <= 0.0f)
        {
            Health -= damage;
        }
        else
        {
            Shield -= damage;

            if (Shield < 0.0f)
            {
                // Shield overflow (negative) is carried into health.
                Health += Shield;
                Shield = 0.0f;
            }
        }

        LastDamagedById = source.UniqueId;

        // Vampiric returns a share of damage dealt to the shooter, so the credit has to
        // happen where the damage is actually resolved - and only on the authority, which
        // is the only peer that knows the real amount.
        if (World is { IsAuthority: true } && source is Projectile shot)
        {
            World.CreditDamageDealt(shot.OwnerId, damage);
        }
    }

    public void ApplyAuthoritativeDamageState(float newHealth, float newShield)
    {
        Health = newHealth;
        Shield = newShield;
        _shieldRechargeTimer = _tuning.ShieldRechargeDelay;
    }

    /// <summary>
    /// Advances the four one-shot cooldowns.
    /// </summary>
    /// <remarks>
    /// These were Godot <c>Timer</c> nodes set to physics processing and paused whenever
    /// the simulation stopped. Ticking them here from the ship's own update reproduces
    /// that pausing for free, because the ship is not ticked while the match is halted.
    /// </remarks>
    private void TickCooldowns(float delta)
    {
        var wasInvulnerable = _invulnerabilityTimer > 0.0f;

        _invulnerabilityTimer = MathF.Max(0.0f, _invulnerabilityTimer - delta);
        _shieldRechargeTimer = MathF.Max(0.0f, _shieldRechargeTimer - delta);
        _timeToNextFire = MathF.Max(0.0f, _timeToNextFire - delta);
        _timeToNextMine = MathF.Max(0.0f, _timeToNextMine - delta);

        if (wasInvulnerable && _invulnerabilityTimer <= 0.0f)
        {
            InvulnerabilityPhase = null;
        }
        else if (InvulnerabilityPhase.HasValue)
        {
            InvulnerabilityPhase = InvulnerabilityPhase.Value + delta;
        }
    }

    private void ProcessControlsMovement(float delta)
    {
        var newVelocity = Velocity;

        // Forward is byte-for-byte the C++ sim vector. This space is y-down with
        // clockwise-positive rotation, so rotating the up-pointing ship art by Rotation
        // yields (sin, -cos) with no offset.
        var forward = Forward;
        var right = new Vector2(-forward.Y, forward.X);

        var leftStick = ShipInput.MovementDirection;
        var sensitivity = leftStick.LengthSquared();

        if (sensitivity > 0.0f)
        {
            sensitivity = MathF.Sqrt(sensitivity);

            var stickForward = leftStick * (1.0f / sensitivity);
            var angleDiff = MathF.Acos(Math.Clamp(Vector2.Dot(stickForward, forward), -1.0f, 1.0f));
            var direction = Vector2.Dot(stickForward, right) > 0.0f ? 1.0f : -1.0f;

            if (angleDiff > _tuning.AngleThreshold)
            {
                var toRotate = _tuning.RotationPerSecond * delta;

                // Deliberately asymmetric, and preserved from the C++ original: turning
                // clockwise clamps the step to the remaining angle, while turning
                // anticlockwise takes min(angleDiff, -toRotate), which is always the
                // negative term and so never clamps. "Fixing" it to a symmetric
                // MathF.Min(angleDiff, toRotate) * direction changes how the ship settles
                // onto a heading and is a feel regression, not a bug fix.
                Rotation += MathF.Min(angleDiff, direction * toRotate);
            }

            var boost = HasBuff(BuffType.Afterburner) ? AfterburnerScale : 1.0f;
            var speed = _tuning.SpeedMax * boost * delta;
            newVelocity += leftStick * speed;

            var length = newVelocity.Length();

            if (length > _tuning.VelocityMax * boost)
            {
                newVelocity *= _tuning.VelocityMax * boost / length;
            }
        }

        var decay = _tuning.VelocityDecayRate * delta;
        newVelocity -= newVelocity * decay;

        Velocity = newVelocity;
    }

    private void ProcessControlsWeapon()
    {
        if (World is null)
        {
            return;
        }

        var direction = ShipInput.FireDirection;

        if (direction.LengthSquared() <= _tuning.FireThresholdSquared)
        {
            return;
        }

        SetInvulnerable(false);

        if (_timeToNextFire > 0.0f)
        {
            return;
        }

        World.CreateProjectiles(PrimaryWeapon, UniqueId, Vector2.Normalize(direction));

        // Fire rate is a property of the weapon now, not a per-weapon field on ShipTuning:
        // there are twenty weapons and adding one should not mean touching the ship.
        var rate = WeaponLibrary.Get(PrimaryWeapon).FireRate;

        if (HasBuff(BuffType.RapidFire))
        {
            rate *= RapidFireScale;
        }

        _timeToNextFire = rate;

        // After the cooldown gate, so this is a shot that actually left the muzzle.
        // CreateProjectiles is authority-only; this is not, which is what lets a client
        // observe its own weapon use at all.
        World.RaiseLocalWeaponFired(this, PrimaryWeapon);
    }

    private void ProcessControlsMine()
    {
        if (!ShipInput.DeployMinePressed)
        {
            return;
        }

        // The deploy edge is consumed even when cooldown or pool state prevents spawning.
        ShipInput.ResetMineInput();

        if (World is null || _timeToNextMine > 0.0f || !World.IsAuthority)
        {
            return;
        }

        var backward = new Vector2(-MathF.Sin(Rotation), MathF.Cos(Rotation));

        if (World.CreateMine(UniqueId, backward))
        {
            SetInvulnerable(false);
            OnMineDeployed();
        }
    }

    private void UpdateShield(float delta)
    {
        var shieldMax = ShieldMaximum;

        if (_shieldRechargeTimer <= 0.0f && Shield < shieldMax)
        {
            var rate = _tuning.ShieldRechargeRate;

            if (HasBuff(BuffType.QuickCharge))
            {
                rate *= QuickChargeRateScale;
            }

            Shield = MathF.Min(shieldMax, Shield + (rate * delta));
        }

        if (HasBuff(BuffType.Regeneration) && Health < _tuning.HealthMax)
        {
            Health = MathF.Min(_tuning.HealthMax, Health + (RegenerationPerSecond * delta));
        }

        Radius = _tuning.RadiusFor(Shield);
    }
}
