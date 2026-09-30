using System.Numerics;
using NetRumble.Core.Objects;
using NetRumble.Core.Tuning;

namespace NetRumble.Core.Net.Wire;

/// <summary>
/// Encodes and decodes every match message.
/// </summary>
/// <remarks>
/// <para>
/// Godot's <c>MultiplayerAPI</c> serialised <c>Dictionary</c> payloads itself, so the
/// GDScript never had a wire format to port - this one is new, and the encode/decode
/// pairs are kept adjacent so a field added to one is obvious if it is missing from the
/// other.
/// </para>
/// <para>
/// Enums are written as a single byte and validated on the way back in. An out-of-range
/// value from a modified client would otherwise become an undefined enum member and fall
/// through every <c>switch</c> in the simulation.
/// </para>
/// </remarks>
public static class MatchMessageCodec
{
    // --- Encode -------------------------------------------------------------

    public static MessageWriter EncodeMatchCreated(MatchCreatedPayload payload)
    {
        var w = new MessageWriter(MessageType.MatchCreated, 1024);
        w.WriteInt(payload.Width);
        w.WriteInt(payload.Height);

        w.WriteCount(payload.Asteroids.Count);
        foreach (var a in payload.Asteroids)
        {
            w.WriteInt(a.Id);
            w.WriteByte((byte)a.Size);
            w.WriteInt(a.Variation);
            w.WriteVector2(a.Position);
            w.WriteVector2(a.Velocity);
            w.WriteFloat(a.Rotation);
        }

        w.WriteCount(payload.Ships.Count);
        foreach (var s in payload.Ships)
        {
            w.WriteInt(s.Id);
            w.WriteInt(s.PeerId);
            w.WriteInt(s.ColorId);
            w.WriteInt(s.StyleId);
            w.WriteVector2(s.Position);
            w.WriteFloat(s.Rotation);
        }

        w.WriteCount(payload.PowerUps.Count);
        foreach (var p in payload.PowerUps)
        {
            w.WriteInt(p.Id);
            w.WriteByte((byte)p.PowerUpType);
        }

        return w;
    }

    public static MessageWriter EncodeMatchStarting(MatchStartingPayload payload)
    {
        var w = new MessageWriter(MessageType.MatchStarting, 512);
        w.WriteCount(payload.Resets.Count);

        foreach (var r in payload.Resets)
        {
            w.WriteInt(r.Id);
            w.WriteVector2(r.Position);
            w.WriteVector2(r.Velocity);
            w.WriteFloat(r.Rotation);
        }

        return w;
    }

    public static MessageWriter EncodeWorldSnapshot(WorldSnapshot snapshot)
    {
        var w = new MessageWriter(MessageType.WorldSnapshot, 1024);
        w.WriteInt(snapshot.Frame);
        w.WriteCount(snapshot.Objects.Count);

        foreach (var o in snapshot.Objects)
        {
            w.WriteInt(o.Id);
            w.WriteVector2(o.Position);
            w.WriteVector2(o.Velocity);
            w.WriteFloat(o.Rotation);
            w.WriteFloat(o.Health);
            w.WriteFloat(o.Shield);
        }

        return w;
    }

    public static MessageWriter EncodeProjectileSpawned(ProjectileSpawnedPayload p)
    {
        var w = new MessageWriter(MessageType.ProjectileSpawned, 96);
        w.WriteInt(p.Id);
        w.WriteByte((byte)p.ProjectileType);
        w.WriteInt(p.OwnerId);
        w.WriteVector2(p.Position);
        w.WriteVector2(p.Velocity);
        w.WriteFloat(p.Rotation);
        WriteShotSpec(ref w, p.Spec);
        return w;
    }

    /// <summary>
    /// Writes the per-shot modifiers.
    /// </summary>
    /// <remarks>
    /// This rides once per shot and the scatter-class weapons send eight at a time, so
    /// the counts and radii are bytes and shorts where their ranges allow it rather than
    /// full floats.
    /// </remarks>
    private static void WriteShotSpec(ref MessageWriter w, ShotSpec s)
    {
        w.WriteFloat(s.DamageScale);
        w.WriteFloat(s.SpeedScale);
        w.WriteFloat(s.RangeScale);
        w.WriteFloat(s.RadiusScale);
        w.WriteFloat(s.SplashRadius);
        w.WriteByte((byte)Math.Clamp(s.Pierce, 0, 255));
        w.WriteByte((byte)Math.Clamp(s.Bounces, 0, 255));
        w.WriteFloat(s.HomingRate);
        w.WriteFloat(s.SpriteScale);
        w.WriteFloat(s.LightRadius);
        w.WriteFloat(s.LightEnergy);
        w.WriteByte(s.ShotColor.R);
        w.WriteByte(s.ShotColor.G);
        w.WriteByte(s.ShotColor.B);
        w.WriteByte(s.ShotColor.A);
    }

    /// <summary>
    /// Reads a per-shot spec.
    /// </summary>
    /// <remarks>
    /// No validation happens here beyond the reader's own length checks: the values are
    /// clamped by <see cref="ShotSpec.Sanitized"/> at the point they are applied to a
    /// body, so both the authority's own path and this one are covered by one set of
    /// rules rather than two that can drift apart.
    /// </remarks>
    private static bool TryReadShotSpec(ref MessageReader r, out ShotSpec spec)
    {
        spec = ShotSpec.Default;

        if (!r.TryReadFloat(out var damageScale)
            || !r.TryReadFloat(out var speedScale)
            || !r.TryReadFloat(out var rangeScale)
            || !r.TryReadFloat(out var radiusScale)
            || !r.TryReadFloat(out var splashRadius)
            || !r.TryReadByte(out var pierce)
            || !r.TryReadByte(out var bounces)
            || !r.TryReadFloat(out var homingRate)
            || !r.TryReadFloat(out var spriteScale)
            || !r.TryReadFloat(out var lightRadius)
            || !r.TryReadFloat(out var lightEnergy)
            || !r.TryReadByte(out var red)
            || !r.TryReadByte(out var green)
            || !r.TryReadByte(out var blue)
            || !r.TryReadByte(out var alpha))
        {
            return false;
        }

        spec = new ShotSpec(
            damageScale,
            speedScale,
            rangeScale,
            radiusScale,
            splashRadius,
            pierce,
            bounces,
            homingRate,
            spriteScale,
            lightRadius,
            lightEnergy,
            new RgbaColor(red, green, blue, alpha));

        return true;
    }

    public static MessageWriter EncodeProjectileDetonated(ProjectileDetonatedPayload p)
    {
        var w = new MessageWriter(MessageType.ProjectileDetonated, 256);
        w.WriteInt(p.Id);
        w.WriteVector2(p.Position);
        w.WriteCount(p.Hits.Count);

        foreach (var h in p.Hits)
        {
            w.WriteInt(h.Id);
            w.WriteFloat(h.Health);
            w.WriteFloat(h.Shield);
            w.WriteVector2(h.Position);
            w.WriteVector2(h.Velocity);
            w.WriteBool(h.IsActive);
        }

        return w;
    }

    public static MessageWriter EncodePowerUpSpawned(PowerUpSpawnedPayload p)
    {
        var w = new MessageWriter(MessageType.PowerUpSpawned, 32);
        w.WriteInt(p.Id);
        w.WriteByte((byte)p.PowerUpType);
        w.WriteVector2(p.Position);
        return w;
    }

    public static MessageWriter EncodePowerUpCollected(PowerUpCollectedPayload p)
    {
        var w = new MessageWriter(MessageType.PowerUpCollected, 32);
        w.WriteInt(p.Id);
        w.WriteInt(p.CollectorId);
        w.WriteByte((byte)p.PowerUp);
        return w;
    }

    public static MessageWriter EncodeShipSpawned(ShipSpawnedPayload p)
    {
        var w = new MessageWriter(MessageType.ShipSpawned, 32);
        w.WriteInt(p.ShipId);
        w.WriteInt(p.PeerId);
        w.WriteVector2(p.Position);
        return w;
    }

    public static MessageWriter EncodeShipDestroyed(ShipDestroyedPayload p)
    {
        var w = new MessageWriter(MessageType.ShipDestroyed, 16);
        w.WriteInt(p.ShipId);
        w.WriteInt(p.PeerId);
        return w;
    }

    public static MessageWriter EncodeAsteroidSplit(AsteroidSplitPayload p)
    {
        var w = new MessageWriter(MessageType.AsteroidSplit, 128);
        w.WriteInt(p.Id);
        w.WriteVector2(p.Position);
        w.WriteInt(p.PeerId);
        w.WriteCount(p.Fragments.Count);

        foreach (var f in p.Fragments)
        {
            w.WriteInt(f.Id);
            w.WriteByte((byte)f.Size);
            w.WriteInt(f.Variation);
            w.WriteVector2(f.Position);
            w.WriteVector2(f.Velocity);
            w.WriteFloat(f.Rotation);
        }

        return w;
    }

    public static MessageWriter EncodeGameplayEvent(GameplayEventType type, Vector2 position)
    {
        var w = new MessageWriter(MessageType.GameplayEvent, 16);
        w.WriteByte((byte)type);
        w.WriteVector2(position);
        return w;
    }

    public static MessageWriter EncodeMatchCompleted(MatchResult result)
    {
        var w = new MessageWriter(MessageType.MatchCompleted, 512);
        w.WriteByte((byte)result.Reason);
        w.WriteString(result.GameMode);
        w.WriteFloat(result.Elapsed);
        w.WriteCount(result.Standings.Count);

        foreach (var s in result.Standings)
        {
            w.WriteInt(s.PeerId);
            w.WriteString(s.DisplayName);
            w.WriteInt(s.Score);
            w.WriteInt(s.Placement);
        }

        return w;
    }

    public static MessageWriter EncodeShipInput(Vector2 movement, Vector2 fire, bool deployMine, int sequence)
    {
        var w = new MessageWriter(MessageType.ShipInput, 32);
        w.WriteVector2(movement);
        w.WriteVector2(fire);
        w.WriteBool(deployMine);
        w.WriteInt(sequence);
        return w;
    }

    public static MessageWriter EncodeRosterEntry(PlayerState player)
    {
        var w = new MessageWriter(MessageType.RosterEntry, 128);
        WriteRosterEntry(ref w, player);
        return w;
    }

    public static MessageWriter EncodeRosterSnapshot(IReadOnlyCollection<PlayerState> players)
    {
        var w = new MessageWriter(MessageType.RosterSnapshot, 512);
        w.WriteInt(players.Count);

        foreach (var player in players.OrderBy(player => player.PeerId))
        {
            WriteRosterEntry(ref w, player);
        }

        return w;
    }

    private static void WriteRosterEntry(ref MessageWriter w, PlayerState player)
    {
        w.WriteInt(player.PeerId);
        w.WriteString(player.DisplayName);
        w.WriteString(player.XboxUserId);
        w.WriteInt(player.ShipColorId);
        w.WriteInt(player.ShipStyleId);
        w.WriteInt(player.Score);
        w.WriteBool(player.IsReady);
        w.WriteBool(player.InGame);
        w.WriteInt(player.ShipId);
    }

    // --- Decode -------------------------------------------------------------

    public static bool TryDecodeMatchCreated(ref MessageReader r, out MatchCreatedPayload payload)
    {
        payload = null!;

        if (!r.TryReadInt(out var width) || !r.TryReadInt(out var height))
        {
            return false;
        }

        // 4 id + 1 size + 4 variation + 8 pos + 8 vel + 4 rot
        if (!r.TryReadCount(29, out var asteroidCount))
        {
            return false;
        }

        var asteroids = new List<AsteroidSpawn>(asteroidCount);

        for (var i = 0; i < asteroidCount; i++)
        {
            if (!r.TryReadInt(out var id)
                || !TryReadEnum(ref r, out AsteroidSize size)
                || !r.TryReadInt(out var variation)
                || !r.TryReadVector2(out var position)
                || !r.TryReadVector2(out var velocity)
                || !r.TryReadFloat(out var rotation))
            {
                return false;
            }

            asteroids.Add(new AsteroidSpawn(id, size, variation, position, velocity, rotation));
        }

        // 4 id + 4 peer + 2 empty string + 4 colour + 4 style + 8 pos + 4 rot
        if (!r.TryReadCount(30, out var shipCount))
        {
            return false;
        }

        var ships = new List<ShipSpawn>(shipCount);

        for (var i = 0; i < shipCount; i++)
        {
            if (!r.TryReadInt(out var id)
                || !r.TryReadInt(out var peerId)
                || !r.TryReadInt(out var colorId)
                || !r.TryReadInt(out var styleId)
                || !r.TryReadVector2(out var position)
                || !r.TryReadFloat(out var rotation))
            {
                return false;
            }

            ships.Add(new ShipSpawn(id, peerId, colorId, styleId, position, rotation));
        }

        if (!r.TryReadCount(5, out var powerUpCount))
        {
            return false;
        }

        var powerUps = new List<PowerUpSpawn>(powerUpCount);

        for (var i = 0; i < powerUpCount; i++)
        {
            if (!r.TryReadInt(out var id) || !TryReadEnum(ref r, out PowerUpType type))
            {
                return false;
            }

            powerUps.Add(new PowerUpSpawn(id, type));
        }

        payload = new MatchCreatedPayload(width, height, asteroids, ships, powerUps);
        return true;
    }

    public static bool TryDecodeMatchStarting(ref MessageReader r, out MatchStartingPayload payload)
    {
        payload = null!;

        if (!r.TryReadCount(24, out var count))
        {
            return false;
        }

        var resets = new List<ResetEntry>(count);

        for (var i = 0; i < count; i++)
        {
            if (!r.TryReadInt(out var id)
                || !r.TryReadVector2(out var position)
                || !r.TryReadVector2(out var velocity)
                || !r.TryReadFloat(out var rotation))
            {
                return false;
            }

            resets.Add(new ResetEntry(id, position, velocity, rotation));
        }

        payload = new MatchStartingPayload(resets);
        return true;
    }

    public static bool TryDecodeWorldSnapshot(ref MessageReader r, out WorldSnapshot snapshot)
    {
        snapshot = null!;

        if (!r.TryReadInt(out var frame) || !r.TryReadCount(32, out var count))
        {
            return false;
        }

        var objects = new List<SnapshotEntry>(count);

        for (var i = 0; i < count; i++)
        {
            if (!r.TryReadInt(out var id)
                || !r.TryReadVector2(out var position)
                || !r.TryReadVector2(out var velocity)
                || !r.TryReadFloat(out var rotation)
                || !r.TryReadFloat(out var health)
                || !r.TryReadFloat(out var shield))
            {
                return false;
            }

            objects.Add(new SnapshotEntry(id, position, velocity, rotation, health, shield));
        }

        snapshot = new WorldSnapshot(frame, objects);
        return true;
    }

    public static bool TryDecodeProjectileSpawned(ref MessageReader r, out ProjectileSpawnedPayload payload)
    {
        payload = default;

        if (!r.TryReadInt(out var id)
            || !TryReadEnum(ref r, out ProjectileType type)
            || !r.TryReadInt(out var ownerId)
            || !r.TryReadVector2(out var position)
            || !r.TryReadVector2(out var velocity)
            || !r.TryReadFloat(out var rotation)
            || !TryReadShotSpec(ref r, out var spec))
        {
            return false;
        }

        payload = new ProjectileSpawnedPayload(id, type, ownerId, position, velocity, rotation, spec);
        return true;
    }

    public static bool TryDecodeProjectileDetonated(ref MessageReader r, out ProjectileDetonatedPayload payload)
    {
        payload = null!;

        if (!r.TryReadInt(out var id)
            || !r.TryReadVector2(out var position)
            || !r.TryReadCount(25, out var count))
        {
            return false;
        }

        var hits = new List<DetonationHit>(count);

        for (var i = 0; i < count; i++)
        {
            if (!r.TryReadInt(out var hitId)
                || !r.TryReadFloat(out var health)
                || !r.TryReadFloat(out var shield)
                || !r.TryReadVector2(out var hitPosition)
                || !r.TryReadVector2(out var hitVelocity)
                || !r.TryReadBool(out var isActive))
            {
                return false;
            }

            hits.Add(new DetonationHit
            {
                Id = hitId,
                Health = health,
                Shield = shield,
                Position = hitPosition,
                Velocity = hitVelocity,
                IsActive = isActive,
            });
        }

        payload = new ProjectileDetonatedPayload(id, position, hits);
        return true;
    }

    public static bool TryDecodePowerUpSpawned(ref MessageReader r, out PowerUpSpawnedPayload payload)
    {
        payload = default;

        if (!r.TryReadInt(out var id)
            || !TryReadEnum(ref r, out PowerUpType powerUpType)
            || !r.TryReadVector2(out var position))
        {
            return false;
        }

        payload = new PowerUpSpawnedPayload(id, powerUpType, position);
        return true;
    }

    public static bool TryDecodePowerUpCollected(ref MessageReader r, out PowerUpCollectedPayload payload)
    {
        payload = default;

        if (!r.TryReadInt(out var id)
            || !r.TryReadInt(out var collectorId)
            || !TryReadEnum(ref r, out PowerUpType powerUp))
        {
            return false;
        }

        payload = new PowerUpCollectedPayload(id, collectorId, powerUp);
        return true;
    }

    public static bool TryDecodeShipSpawned(ref MessageReader r, out ShipSpawnedPayload payload)
    {
        payload = default;

        if (!r.TryReadInt(out var shipId)
            || !r.TryReadInt(out var peerId)
            || !r.TryReadVector2(out var position))
        {
            return false;
        }

        payload = new ShipSpawnedPayload(shipId, peerId, position);
        return true;
    }

    public static bool TryDecodeShipDestroyed(ref MessageReader r, out ShipDestroyedPayload payload)
    {
        payload = default;

        if (!r.TryReadInt(out var shipId) || !r.TryReadInt(out var peerId))
        {
            return false;
        }

        payload = new ShipDestroyedPayload(shipId, peerId);
        return true;
    }

    public static bool TryDecodeAsteroidSplit(ref MessageReader r, out AsteroidSplitPayload payload)
    {
        payload = null!;

        if (!r.TryReadInt(out var id)
            || !r.TryReadVector2(out var position)
            || !r.TryReadInt(out var peerId)
            // 4 id + 1 size + 4 variation + 8 pos + 8 vel + 4 rot
            || !r.TryReadCount(29, out var count))
        {
            return false;
        }

        var fragments = new List<AsteroidFragment>(count);

        for (var i = 0; i < count; i++)
        {
            if (!r.TryReadInt(out var fragmentId)
                || !TryReadEnum(ref r, out AsteroidSize size)
                || !r.TryReadInt(out var variation)
                || !r.TryReadVector2(out var fragmentPosition)
                || !r.TryReadVector2(out var velocity)
                || !r.TryReadFloat(out var rotation))
            {
                return false;
            }

            fragments.Add(new AsteroidFragment(
                fragmentId, size, variation, fragmentPosition, velocity, rotation));
        }

        payload = new AsteroidSplitPayload(id, position, peerId, fragments);
        return true;
    }

    public static bool TryDecodeGameplayEvent(
        ref MessageReader r,
        out GameplayEventType type,
        out Vector2 position)
    {
        position = Vector2.Zero;
        return TryReadEnum(ref r, out type) && r.TryReadVector2(out position);
    }

    public static bool TryDecodeMatchCompleted(ref MessageReader r, out MatchResult result)
    {
        result = null!;

        if (!TryReadEnum(ref r, out MatchEndReason reason)
            || !r.TryReadString(out var gameMode)
            || !r.TryReadFloat(out var elapsed)
            || !r.TryReadCount(14, out var count))
        {
            return false;
        }

        var standings = new List<MatchStanding>(count);

        for (var i = 0; i < count; i++)
        {
            if (!r.TryReadInt(out var peerId)
                || !r.TryReadString(out var name)
                || !r.TryReadInt(out var score)
                || !r.TryReadInt(out var placement))
            {
                return false;
            }

            standings.Add(new MatchStanding(peerId, name, score, placement));
        }

        result = new MatchResult(reason, gameMode, elapsed, standings);
        return true;
    }

    public static bool TryDecodeShipInput(
        ref MessageReader r,
        out Vector2 movement,
        out Vector2 fire,
        out bool deployMine,
        out int sequence)
    {
        fire = Vector2.Zero;
        deployMine = false;
        sequence = 0;

        return r.TryReadVector2(out movement)
            && r.TryReadVector2(out fire)
            && r.TryReadBool(out deployMine)
            && r.TryReadInt(out sequence);
    }

    public static bool TryDecodeRosterEntry(ref MessageReader r, out PlayerState player)
    {
        player = null!;

        if (!r.TryReadInt(out var peerId)
            || !r.TryReadString(out var displayName)
            || !r.TryReadString(out var xboxUserId)
            || !r.TryReadInt(out var colorId)
            || !r.TryReadInt(out var styleId)
            || !r.TryReadInt(out var score)
            || !r.TryReadBool(out var isReady)
            || !r.TryReadBool(out var inGame)
            || !r.TryReadInt(out var shipId))
        {
            return false;
        }

        player = new PlayerState
        {
            PeerId = peerId,
            DisplayName = displayName,
            XboxUserId = xboxUserId,
            ShipColorId = colorId,
            ShipStyleId = styleId,
            Score = score,
            IsReady = isReady,
            InGame = inGame,
            ShipId = shipId,
        };

        return true;
    }

    public static bool TryDecodeRosterSnapshot(ref MessageReader r, out IReadOnlyList<PlayerState> players)
    {
        players = [];

        if (!r.TryReadInt(out var count) || count is < 1 or > 4)
        {
            return false;
        }

        var decoded = new List<PlayerState>(count);
        var peerIds = new HashSet<int>();

        for (var i = 0; i < count; i++)
        {
            if (!TryDecodeRosterEntry(ref r, out var player) || !peerIds.Add(player.PeerId))
            {
                return false;
            }

            decoded.Add(player);
        }

        players = decoded;
        return true;
    }

    /// <summary>
    /// Reads a byte and rejects it if it is not a defined member of the enum.
    /// </summary>
    /// <remarks>
    /// Without this an out-of-range byte becomes a valid-typed but undefined enum value
    /// that matches no <c>case</c>, so the simulation silently takes a default branch
    /// somewhere far from the packet that caused it.
    /// </remarks>
    private static bool TryReadEnum<TEnum>(ref MessageReader r, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;

        if (!r.TryReadByte(out var raw))
        {
            return false;
        }

        value = (TEnum)Enum.ToObject(typeof(TEnum), raw);
        return Enum.IsDefined(value);
    }
}
