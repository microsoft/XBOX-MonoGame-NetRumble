namespace NetRumble.Core;

/// <summary>
/// Global, non-designer-facing constants: world size, network cadence and match flow.
/// Ported from <c>scripts/gameplay/nr_const.gd</c>.
/// </summary>
/// <remarks>
/// Per-entity tuning (ship, asteroid, projectile and power-up stats) does not live
/// here - it is data, loaded from the tuning files that replace
/// <c>assets/tuning/*.tres</c>. Values here describe the simulation itself rather than
/// how any single entity feels.
/// </remarks>
public static class NRConst
{
    // --- Pickups -----------------------------------------------------------

    /// <summary>
    /// Seconds between drops at the extremes of the Power-Up Frequency setting. There are
    /// thirty-two pickups now, so the pacing is a player setting rather than a constant,
    /// running from a sparse drop a minute to a deliberately chaotic one every five
    /// seconds.
    /// </summary>
    public const float PowerUpSpawnTimerMin = 5.0f;

    /// <inheritdoc cref="PowerUpSpawnTimerMin"/>
    public const float PowerUpSpawnTimerMax = 60.0f;

    /// <summary>
    /// How many pickups may be live simultaneously, at those same extremes. The cap moves
    /// with the drop rate because the two describe the same thing from opposite ends: a
    /// drop a minute would never reach a high cap anyway, and a drop every five seconds
    /// needs the headroom or the field saturates and the timer idles.
    /// </summary>
    public const int MaxActivePowerUpsMin = 2;

    /// <inheritdoc cref="MaxActivePowerUpsMin"/>
    public const int MaxActivePowerUpsMax = 20;

    /// <summary>
    /// The shipped Power-Up Frequency, matching the Godot build's profile default. It
    /// sits near the top of the range deliberately: a busy field is what makes a
    /// thirty-two-entry pickup table legible in a single match.
    /// </summary>
    public const float DefaultPowerUpFrequency = 0.9f;

    /// <summary>
    /// Size of the pickup pool. Comfortably above <see cref="MaxActivePowerUpsMax"/> so a
    /// collection and a spawn landing in the same frame always have a free body to work
    /// with.
    /// </summary>
    public const int PowerUpPoolSize = 24;

    /// <summary>
    /// Seconds between drops for a normalised (0..1) Power-Up Frequency setting. The
    /// setting reads as "frequency", so 1.0 is the <em>shortest</em> interval and the
    /// mapping runs backwards down the range.
    /// </summary>
    public static float PowerUpSpawnInterval(float frequency)
        => float.Lerp(PowerUpSpawnTimerMax, PowerUpSpawnTimerMin, Math.Clamp(frequency, 0.0f, 1.0f));

    /// <summary>
    /// How many pickups may be uncollected at once for a normalised (0..1) setting.
    /// </summary>
    public static int MaxActivePowerUps(float frequency)
        => (int)MathF.Round(float.Lerp(
            MaxActivePowerUpsMin, MaxActivePowerUpsMax, Math.Clamp(frequency, 0.0f, 1.0f)));

    // --- Barrier -----------------------------------------------------------

    /// <summary>Uniform scale applied to the barrier wall and end-cap sprites.</summary>
    public const float BarrierEndScale = 1.65f;

    public const float BarrierRotationSpeed = 0.5f;

    // --- World (Game/Gameplay/World.cpp) -----------------------------------

    public const float DoubleLaserOffset = 8.0f;

    /// <summary>2.5 degrees, in radians.</summary>
    public const float TripleLaserSpread = 2.5f * MathF.PI / 180.0f;

    public const float MineSpawnDistance = 8.0f;
    public const float SpeedDamageRatio = 0.5f;
    public const int FindSpawnPointAttempts = 25;
    public const int MaxLasersPerPlayer = 90;
    public const int MaxMinesPerPlayer = 8;
    public const int MaxRocketsPerPlayer = 10;

    // --- Match flow (Game/Gameplay/GameState.h, GameStateServer.h) ----------

    /// <summary>
    /// Asteroids the world seeds itself with.
    /// </summary>
    /// <remarks>
    /// Rocks break apart instead of being indestructible scenery, so the field starts
    /// smaller than the original's 15: every one of these is a seed that multiplies.
    /// </remarks>
    public const int AsteroidCount = 12;

    /// <summary>
    /// Hard ceiling on live asteroids.
    /// </summary>
    /// <remarks>
    /// A <see cref="AsteroidSize.Huge"/> rock yields <c>SplitCount^4</c> fragments, so
    /// without a cap a field of large rocks can multiply into thousands of bodies and
    /// bury the solver. Splits past the cap destroy the rock without fragments.
    /// </remarks>
    public const int MaxAsteroids = 96;

    public const int WorldWidth = 2400;
    public const int WorldHeight = 2400;

    /// <summary>Grace period for every player to finish loading before the match proceeds.</summary>
    public const float SimulationDelayPlayersLoading = 60.0f;

    /// <summary>Single countdown after the world is laid out and before the match goes live.</summary>
    public const float SimulationDelayStarting = 5.0f;

    public const float ShipRespawnDelay = 5.0f;

    // --- Networking --------------------------------------------------------

    /// <summary>
    /// Host broadcasts an authoritative world snapshot at this rate
    /// (<c>c_worldDataUpdateInterval = 1/30</c> in GameStateServer.h).
    /// </summary>
    public const float WorldSnapshotHz = 30.0f;

    /// <summary>
    /// Host broadcasts the authoritative match clock at this rate so client HUD timers
    /// stay locked to it.
    /// </summary>
    public const float MatchClockHz = 2.0f;

    /// <summary>Clients push their input to the host at this rate.</summary>
    public const float InputSendHz = 30.0f;

    public const int DefaultPort = 7777;

    // --- Snapshot reconciliation (scripts/gameplay/world.gd) ----------------

    /// <summary>
    /// How much of the snapshot correction to apply per snapshot; the rest is closed by
    /// local dead-reckoning between snapshots, which keeps remote objects smooth.
    /// </summary>
    public const float SnapshotLerp = 0.35f;

    /// <summary>
    /// The local ship is corrected far more gently than remote objects, because the
    /// player is already looking at their own prediction; anything stronger reads as
    /// rubber-banding under latency.
    /// </summary>
    public const float LocalSnapshotLerp = 0.12f;

    /// <summary>
    /// Past this much positional error the local prediction is abandoned and the host's
    /// position is taken outright, so a desync cannot persist for the rest of the match.
    /// </summary>
    public const float LocalSnapshotSnapDistance = 250.0f;

    /// <summary>
    /// How far the local ship's facing may drift from the host's before it is
    /// corrected. 25 degrees, in radians.
    /// </summary>
    public const float LocalSnapshotRotationTolerance = 25.0f * MathF.PI / 180.0f;

    public const float SpawnPointPadding = 100.0f;

    /// <summary>Peer id of the host, from <c>NetManager.HOST_PEER_ID</c>.</summary>
    public const int HostPeerId = 1;

    // --- Practice mode -----------------------------------------------------

    /// <summary>
    /// Ceiling on AI opponents in a practice match. A match holds four players, one of
    /// which is the local player, so practice tops out at three bots.
    /// </summary>
    public const int MaxPracticeBots = 3;

    /// <summary>
    /// Practice bots are given peer ids counting down from here.
    /// </summary>
    /// <remarks>
    /// Real peer ids are always positive, so a negative id can never collide with one,
    /// and anything that looks a bot up by peer id - scoring, the roster, the world's
    /// ship-per-peer map - works with no second code path.
    /// </remarks>
    public const int BotPeerIdBase = -1000;
}
