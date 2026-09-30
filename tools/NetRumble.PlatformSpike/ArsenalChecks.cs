using NetRumble.Core.Objects;
using NetRumble.Core;
using NetRumble.Core.Tuning;

/// <summary>
/// Checks for the twenty-weapon arsenal ported from the Godot build.
/// </summary>
/// <remarks>
/// <para>
/// These exist because the arsenal is almost entirely <i>data</i>, and data has no
/// compiler. A weapon row with a zero fire rate, a pickup table whose weights sum to
/// nothing, or a buff that never expires are all perfectly valid C# that silently ruins a
/// match - and none of them would be caught by a build or by the autopilot, which can only
/// observe the two or three pickups that happen to drop in the time it runs.
/// </para>
/// <para>
/// The drop distribution in particular cannot be proven by playing: one power-up is live
/// at a time and a ninety-second match sees about one. So it is driven directly here,
/// tens of thousands of rolls, which is the only way to see the whole table.
/// </para>
/// </remarks>
internal static class ArsenalChecks
{
    public static void Run()
    {
        Console.WriteLine("[15] Weapon arsenal and pickups");

        EveryWeaponIsDefined();
        EveryPickupIsDefined();
        DropTableCoversEveryPickup();
        WorldReachesEveryPickup();
        ShotSpecRejectsHostileValues();
        BuffsExpire();

        Console.WriteLine();
    }

    /// <summary>
    /// Every weapon resolves to a row, and every row is playable.
    /// </summary>
    /// <remarks>
    /// <c>WeaponLibrary.Get</c> falls back to the laser for anything it does not know, so
    /// a missing row would otherwise present as "that weapon shoots like a laser" rather
    /// than as an error. The identity check below is what makes the fallback visible.
    /// </remarks>
    private static void EveryWeaponIsDefined()
    {
        var weapons = Enum.GetValues<WeaponType>();
        var missing = new List<WeaponType>();
        var unplayable = new List<WeaponType>();

        foreach (var weapon in weapons)
        {
            var definition = WeaponLibrary.Get(weapon);

            if (definition.WeaponType != weapon)
            {
                missing.Add(weapon);
                continue;
            }

            // A zero or negative fire rate divides through the firing timer; a zero shot
            // count fires nothing at all; a zero ammo count means the weapon is consumed
            // by the pickup that granted it.
            if (definition.FireRate <= 0.0f
                || definition.ShotCount < 1
                || definition.Ammo == 0
                || definition.DamageScale <= 0.0f
                || definition.SpeedScale <= 0.0f
                || definition.RangeScale <= 0.0f)
            {
                unplayable.Add(weapon);
            }
        }

        Check($"all {weapons.Length} weapons have their own row", missing.Count == 0, Describe(missing));
        Check("no weapon row is unplayable", unplayable.Count == 0, Describe(unplayable));
        Check("there are the twenty weapons the Godot build has", weapons.Length == 20, $"got {weapons.Length}");
    }

    /// <summary>
    /// Every pickup resolves to a row, and each row grants what its kind promises.
    /// </summary>
    /// <remarks>
    /// The kind is what <c>World.ApplyPickup</c> switches on, so a buff row that forgot
    /// its duration would be collected, applied, and expire on the same frame - a pickup
    /// that visibly does nothing.
    /// </remarks>
    private static void EveryPickupIsDefined()
    {
        var pickups = Enum.GetValues<PowerUpType>();
        var missing = new List<PowerUpType>();
        var inconsistent = new List<PowerUpType>();

        foreach (var pickup in pickups)
        {
            var definition = PickupLibrary.Get(pickup);

            if (definition.PowerUpType != pickup)
            {
                missing.Add(pickup);
                continue;
            }

            var consistent = definition.Kind switch
            {
                PickupKind.Buff => definition.BuffDuration > 0.0f,
                PickupKind.Restore => definition.RestoreHealth > 0.0f || definition.RestoreShield > 0.0f,
                PickupKind.Weapon => true,
                _ => false,
            };

            if (!consistent || string.IsNullOrWhiteSpace(definition.DisplayName))
            {
                inconsistent.Add(pickup);
            }
        }

        Check($"all {pickups.Length} pickups have their own row", missing.Count == 0, Describe(missing));
        Check("every pickup grants what its kind promises", inconsistent.Count == 0, Describe(inconsistent));
        Check("there are the thirty-two pickups the Godot build has", pickups.Length == 32, $"got {pickups.Length}");

        // There are three textures and thirty-two pickups, so the tint and the label are
        // the only things distinguishing most of them on screen. A blank label or an
        // invisible tint makes a pickup indistinguishable from whichever others share its
        // silhouette - and the player has no way to tell what they are about to pick up.
        var unreadable = new List<PowerUpType>();
        var invisible = new List<PowerUpType>();

        foreach (var pickup in pickups)
        {
            var definition = PickupLibrary.Get(pickup);

            if (string.IsNullOrWhiteSpace(definition.Label) || definition.Label.Length > 6)
            {
                unreadable.Add(pickup);
            }

            if (definition.Tint.A == 0)
            {
                invisible.Add(pickup);
            }
        }

        Check("every pickup has a short readable label", unreadable.Count == 0, Describe(unreadable));
        Check("no pickup is tinted fully transparent", invisible.Count == 0, Describe(invisible));

        // Labels only identify a pickup if they differ. Two rows sharing one is the kind
        // of copy-paste slip a thirty-two-row table invites and nothing else would catch.
        var duplicates = pickups
            .GroupBy(p => PickupLibrary.Get(p).Label, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Check("no two pickups share a label", duplicates.Count == 0, Describe(duplicates));
    }

    /// <summary>
    /// The weighted drop table reaches every pickup, and reaches each about as often as
    /// its weight says.
    /// </summary>
    /// <remarks>
    /// A pickup with a weight the expansion rounds to zero is unreachable: it exists,
    /// it is documented, and no player will ever see it. That is the failure this is
    /// really looking for, and it is invisible from inside a match.
    /// </remarks>
    private static void DropTableCoversEveryPickup()
    {
        const int Rolls = 200_000;
        var random = new Random(20250607);
        var seen = new Dictionary<PowerUpType, int>();

        for (var i = 0; i < Rolls; i++)
        {
            var rolled = PickupLibrary.RandomType(random);
            seen[rolled] = seen.GetValueOrDefault(rolled) + 1;
        }

        var never = Enum.GetValues<PowerUpType>().Where(p => !seen.ContainsKey(p)).ToList();
        Check("every pickup can actually drop", never.Count == 0, Describe(never));

        // Heaviest against lightest rather than each row against its own weight: the point
        // is that the weights are being honoured at all, and a table that ignored them
        // would produce a flat distribution, which this catches.
        var totalWeight = Enum.GetValues<PowerUpType>().Sum(p => PickupLibrary.Get(p).SpawnWeight);
        var worst = 0.0f;
        var worstPickup = PowerUpType.DoubleLaser;

        foreach (var (pickup, count) in seen)
        {
            var expected = PickupLibrary.Get(pickup).SpawnWeight / totalWeight * Rolls;
            var error = MathF.Abs(count - expected) / expected;

            if (error > worst)
            {
                worst = error;
                worstPickup = pickup;
            }
        }

        Check(
            "drop frequency follows the spawn weights",
            worst < 0.15f,
            $"worst was {worstPickup} at {worst * 100.0f:F1}% off expected");
    }

    /// <summary>
    /// The world can actually put every pickup on the field, and honours its own cap.
    /// </summary>
    /// <remarks>
    /// The library checks above prove the thirty-two rows exist and that the weighted
    /// roll returns all of them. Neither says anything about whether the <em>world</em>
    /// can spawn them, and for a long time it could not: the pool was one body per type,
    /// keyed by type, holding the three pickups the original sample shipped, and the
    /// spawn looked its roll up in that dictionary and returned silently on a miss.
    /// Twenty-nine of the thirty-two were unreachable in play while every library check
    /// passed. This drives real spawns through the real world instead.
    /// </remarks>
    private static void WorldReachesEveryPickup()
    {
        GameObject.ResetIdCounter();

        var world = new World(new Random(20260518));
        world.Initialize(
            authority: true,
            [new PlayerState { DisplayName = "Host" }]);
        world.PowerUpFrequency = 1.0f;

        var cap = NRConst.MaxActivePowerUps(world.PowerUpFrequency);
        var interval = NRConst.PowerUpSpawnInterval(world.PowerUpFrequency);
        var seen = new HashSet<PowerUpType>();
        var overCap = false;

        // Fill first, collecting nothing, so the cap is what stops the field growing
        // rather than the test consuming the drops. One spawn per tick, so this needs
        // more passes than the cap it is trying to reach.
        for (var pass = 0; pass < cap * 4; pass++)
        {
            world.TickPowerUps(interval);
            overCap |= world.ActivePowerUpCount > cap;
        }

        var peak = world.ActivePowerUpCount;

        Check($"the field fills to its cap of {cap}", peak >= cap, $"peaked at {peak}");
        Check("the field never exceeds its cap", !overCap, $"peaked at {peak} against {cap}");

        // Then recycle, which is the only way a run this short reaches all thirty-two
        // types: the cap means a field that is never collected only ever shows twenty.
        var ship = world.Ships.Values.FirstOrDefault();

        for (var pass = 0; pass < 4000; pass++)
        {
            foreach (var powerUp in world.ActivePowerUps.ToList())
            {
                seen.Add(powerUp.PowerUpType);
                world.CollectPowerUp(powerUp, ship);
            }

            world.TickPowerUps(interval);
            overCap |= world.ActivePowerUpCount > cap;
        }

        var unreachable = Enum.GetValues<PowerUpType>().Where(t => !seen.Contains(t)).ToList();

        Check("the world can spawn every pickup in the table", unreachable.Count == 0, Describe(unreachable));

        // A pool that leaks bodies still spawns for a while, so a field that stops
        // reaching its cap late is the symptom. Draining it after all that recycling is
        // what catches a body retired twice or never returned.
        foreach (var powerUp in world.ActivePowerUps.ToList())
        {
            world.CollectPowerUp(powerUp, ship);
        }

        Check(
            "every pooled body came back after collection",
            world.ActivePowerUpCount == 0,
            $"{world.ActivePowerUpCount} still active");
    }

    /// <summary>
    /// The shot spec clamps everything a hostile or broken authority could put in it.
    /// </summary>
    /// <remarks>
    /// <c>ShotSpec</c> rides the spawn message, and every field of it is consumed as a
    /// multiplier on a simulation value. A NaN scale poisons a projectile's radius, which
    /// poisons the collision grid, which poisons every snapshot after it - so this is the
    /// single chokepoint the decoder relies on, and it has to hold.
    /// </remarks>
    private static void ShotSpecRejectsHostileValues()
    {
        var hostile = new ShotSpec(
            DamageScale: float.NaN,
            SpeedScale: float.PositiveInfinity,
            RangeScale: -5.0f,
            RadiusScale: 1e30f,
            SplashRadius: float.NegativeInfinity,
            Pierce: int.MaxValue,
            Bounces: -100,
            HomingRate: float.NaN,
            SpriteScale: float.NaN,
            LightRadius: -1e20f,
            LightEnergy: float.PositiveInfinity,
            ShotColor: new RgbaColor(255, 255, 255, 255));

        var clean = hostile.Sanitized();

        Check(
            "no scale survives as NaN or infinite",
            float.IsFinite(clean.DamageScale)
            && float.IsFinite(clean.SpeedScale)
            && float.IsFinite(clean.RangeScale)
            && float.IsFinite(clean.RadiusScale)
            && float.IsFinite(clean.SplashRadius)
            && float.IsFinite(clean.HomingRate)
            && float.IsFinite(clean.SpriteScale)
            && float.IsFinite(clean.LightRadius)
            && float.IsFinite(clean.LightEnergy));

        Check(
            "scales are clamped to a range the simulation can carry",
            clean.DamageScale is > 0.0f and <= ShotSpec.MaxScale
            && clean.SpeedScale is > 0.0f and <= ShotSpec.MaxScale
            && clean.RangeScale is > 0.0f and <= ShotSpec.MaxScale
            && clean.RadiusScale is > 0.0f and <= ShotSpec.MaxScale);

        Check(
            "hit counts cannot be negative or unbounded",
            clean.Pierce is >= 0 and <= ShotSpec.MaxHits && clean.Bounces is >= 0 and <= ShotSpec.MaxHits,
            $"pierce {clean.Pierce}, bounces {clean.Bounces}");

        // The teeth: a spec that was already legal must come back untouched, or the
        // clamping above would pass just as well if Sanitized returned a constant.
        var legal = WeaponLibrary.Get(WeaponType.Rocket).ToShotSpec();
        Check("a legal spec is returned unchanged", legal.Sanitized() == legal);
    }

    /// <summary>
    /// A granted buff expires on its own, and expiry restores the ship it was modifying.
    /// </summary>
    /// <remarks>
    /// Buffs are the one part of the arsenal with state that outlives the frame that
    /// created it. A buff that never expires is a permanent advantage awarded by a random
    /// drop, which is the worst failure in the whole feature and the least visible.
    /// </remarks>
    private static void BuffsExpire()
    {
        var ship = new Ship();

        ship.GrantBuff(BuffType.DoubleDamage, 2.0f);

        Check("a granted buff is held", ship.HasBuff(BuffType.DoubleDamage));
        Check("a damage buff actually multiplies damage", ship.DamageMultiplier() > 1.0f);

        // One long step rather than many short ones: the expiry has to be driven by the
        // elapsed time, not by a per-frame counter that a variable frame rate would skew.
        ship.TickBuffs(1.0f);
        Check("a buff survives until its duration is up", ship.HasBuff(BuffType.DoubleDamage));

        ship.TickBuffs(1.5f);
        Check("a buff expires once its duration is up", !ship.HasBuff(BuffType.DoubleDamage));
        Check("expiry restores the ship's damage", Math.Abs(ship.DamageMultiplier() - 1.0f) < 0.0001f);

        // Regranting must extend rather than stack, or two of the same pickup in a row
        // would double a multiplier that is only ever meant to apply once.
        ship.GrantBuff(BuffType.DoubleDamage, 1.0f);
        var single = ship.DamageMultiplier();
        ship.GrantBuff(BuffType.DoubleDamage, 1.0f);

        Check("regranting a buff refreshes rather than stacks", Math.Abs(ship.DamageMultiplier() - single) < 0.0001f);

        // Respawning must not carry a buff into the next life. Start is the one place
        // that runs on every respawn, so it is the one place this can be guaranteed.
        ship.GrantBuff(BuffType.Cloak, 30.0f);
        ship.Start();

        Check("respawning clears every buff", !ship.HasBuff(BuffType.Cloak) && ship.Buffs.Count == 0);
    }

    private static string Describe<T>(IReadOnlyCollection<T> values)
        => values.Count == 0 ? string.Empty : string.Join(", ", values);

    private static void Check(string label, bool condition, string detail = "")
        => Console.WriteLine(
            $"    {(condition ? "PASS" : "FAIL")} {label}{(condition || detail.Length == 0 ? string.Empty : $" ({detail})")}");
}
