using System.Numerics;
using NetRumble.Core.Tuning;
using NetRumble.Platform;

namespace NetRumble.Core.Net;

/// <summary>
/// A single-player <see cref="IMatchNetwork"/>: the local player is the only peer, every
/// broadcast is dropped, and no inbound event ever fires.
/// </summary>
/// <remarks>
/// This is what makes a practice match playable with no transport at all. It reports
/// <see cref="IsHost"/> as <c>true</c> so the director runs the full authoritative state
/// machine, and <see cref="IsOffline"/> as <c>true</c> so the last-player-standing rule
/// does not immediately end a legitimate one-player session.
/// </remarks>
public sealed class OfflineMatchNetwork : IMatchNetwork
{
    private static readonly string[] BotNames =
        ["Vega", "Rigel", "Altair", "Mira", "Antares", "Deneb", "Polaris"];

    private readonly Dictionary<int, PlayerState> _players = [];

    /// <summary>Creates an offline session containing a single local player.</summary>
    /// <param name="localPlayer">
    /// The local player. Its <see cref="PlayerState.PeerId"/> is forced to
    /// <see cref="NRConst.HostPeerId"/> and it is marked as local and in-game, because an
    /// offline session has no lobby handshake to do that.
    /// </param>
    public OfflineMatchNetwork(PlayerState localPlayer)
    {
        ArgumentNullException.ThrowIfNull(localPlayer);

        localPlayer.PeerId = NRConst.HostPeerId;
        localPlayer.IsLocalPlayer = true;
        localPlayer.InGame = true;

        LocalPlayer = localPlayer;
        _players[localPlayer.PeerId] = localPlayer;
    }

    /// <inheritdoc />
    public bool IsHost => true;

    /// <inheritdoc />
    public bool IsOffline => true;

    /// <inheritdoc />
    public int LocalPeerId => NRConst.HostPeerId;

    /// <inheritdoc />
    public IReadOnlyDictionary<int, PlayerState> Players => _players;

    /// <inheritdoc />
    public PlayerState? LocalPlayer { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Roster order: the human first, then bots by descending peer id. Bot ids count
    /// downwards from <see cref="NRConst.BotPeerIdBase"/>, so descending puts the
    /// first-added bot at the top of the list and keeps the lobby rows from reshuffling
    /// as the stepper adds another.
    /// </remarks>
    public IReadOnlyList<PlayerState> SortedPlayers()
        => [.. _players.Values
            .OrderBy(p => p.IsBot)
            .ThenBy(p => p.IsBot ? -p.PeerId : p.PeerId)];

    /// <inheritdoc />
    public IReadOnlyList<PlayerState> PlayersByScore()
        => [.. _players.Values.OrderByDescending(p => p.Score).ThenBy(p => p.PeerId)];

    /// <summary>Every AI opponent currently in the roster, in <see cref="SortedPlayers"/> order.</summary>
    public IReadOnlyList<PlayerState> BotPlayers()
        => [.. _players.Values.Where(p => p.IsBot).OrderByDescending(p => p.PeerId)];

    /// <summary>How many AI opponents are in the roster.</summary>
    public int BotCount => _players.Values.Count(p => p.IsBot);

    /// <summary>
    /// Brings the roster's bot count to <paramref name="count"/>, adding or removing NPC
    /// opponents as needed.
    /// </summary>
    /// <remarks>
    /// Bots are created ready and in-game. Both flags gate the match start - the lobby
    /// waits on everyone being ready and the director waits on every player's
    /// <see cref="PlayerState.InGame"/> - and a bot has nothing to ready up or load, so
    /// leaving them false would simply hang the practice match forever.
    /// </remarks>
    public void SyncPracticeBots(int count)
    {
        var wanted = Math.Clamp(count, 0, NRConst.MaxPracticeBots);
        var existing = BotPlayers();

        for (var index = wanted; index < existing.Count; index++)
        {
            _players.Remove(existing[index].PeerId);
        }

        for (var index = existing.Count; index < wanted; index++)
        {
            var state = new PlayerState
            {
                PeerId = NRConst.BotPeerIdBase - index,
                DisplayName = BotDisplayName(index),
                IsBot = true,
                IsReady = true,
                InGame = true,
                ShipStyleId = index % 4,
            };

            state.ShipColorId = FreeShipColor();
            _players[state.PeerId] = state;
        }
    }

    /// <summary>
    /// Picks a hull colour nobody in the roster is using yet, so the player can always
    /// tell which ship is theirs. Falls back to a wrap-around once the palette runs out.
    /// </summary>
    private int FreeShipColor()
    {
        var taken = _players.Values.Select(p => p.ShipColorId).ToHashSet();
        var paletteSize = Math.Max(TuningLibrary.Palette.Size, 1);

        for (var colorId = 0; colorId < paletteSize; colorId++)
        {
            if (!taken.Contains(colorId))
            {
                return colorId;
            }
        }

        return _players.Count % paletteSize;
    }

    private static string BotDisplayName(int index)
        => index < BotNames.Length ? BotNames[index] : $"Bot {index + 1}";

    /// <inheritdoc />
    public void SetMatchState(MatchState state)
    {
    }

    /// <inheritdoc />
    public void ResetForNextMatch()
    {
        // No peers to tell, but the roster is the same object across matches here too,
        // so the local player would otherwise start a second practice match holding the
        // first one's score. Ready and in-game are left alone: bots are created ready
        // and in-game because there is nobody for them to announce themselves to, and
        // the local player's in-game flag is set in the constructor for the same reason
        // - an offline session has no SendLoaded, so clearing it would leave the next
        // match waiting for a report that is never sent. Only the ready flag is worth
        // clearing, and only for the player who can set it again.
        foreach (var player in _players.Values)
        {
            player.Score = 0;
            player.ShipId = 0;

            if (!player.IsBot)
            {
                player.IsReady = false;
            }
        }
    }

    /// <inheritdoc />
    public void BroadcastCountdown(int secondsRemaining)
    {
    }

    /// <inheritdoc />
    public void BroadcastMatchClock(float elapsed)
    {
    }

    /// <inheritdoc />
    public void BroadcastShipDestroyed(ShipDestroyedPayload payload)
    {
    }

    /// <inheritdoc />
    public void BroadcastShipSpawned(ShipSpawnedPayload payload)
    {
    }

    /// <inheritdoc />
    public void BroadcastScoreUpdated(int peerId, int score, int delta)
    {
    }

    /// <inheritdoc />
    public void BroadcastMatchCompleted(MatchResult result)
    {
    }

    /// <inheritdoc />
    /// <remarks>Nothing to mirror to: a solo session is its own authority.</remarks>
    public void SendShipInput(Vector2 movement, Vector2 fire, bool deployMine, int sequence)
    {
    }

    /// <inheritdoc />
    public void BroadcastMatchCreated(MatchCreatedPayload payload)
    {
    }

    /// <inheritdoc />
    public void BroadcastMatchStarting(MatchStartingPayload payload)
    {
    }

    /// <inheritdoc />
    public void BroadcastWorldSnapshot(WorldSnapshot snapshot)
    {
    }

    /// <inheritdoc />
    public void BroadcastProjectileSpawned(ProjectileSpawnedPayload payload)
    {
    }

    /// <inheritdoc />
    public void BroadcastProjectileDetonated(ProjectileDetonatedPayload payload)
    {
    }

    /// <inheritdoc />
    public void BroadcastPowerUpSpawned(PowerUpSpawnedPayload payload)
    {
    }

    /// <inheritdoc />
    public void BroadcastPowerUpCollected(PowerUpCollectedPayload payload)
    {
    }

    /// <inheritdoc />
    public void BroadcastAsteroidSplit(AsteroidSplitPayload payload)
    {
    }

    /// <inheritdoc />
    public void BroadcastGameplayEvent(GameplayEventType eventType, Vector2 position)
    {
    }

    /// <inheritdoc />
    /// <remarks>
    /// A one-player session has nobody to send its own ready state to and nobody else's
    /// row to update, so this only needs to keep <see cref="LocalPlayer"/> itself correct.
    /// </remarks>
    public void PublishLocalReady(bool ready)
    {
        if (LocalPlayer is { } local)
        {
            local.IsReady = ready;
        }
    }

    /// <inheritdoc />
    public void PublishLocalAppearance(int colorId, int styleId)
    {
        if (LocalPlayer is { } local)
        {
            local.ShipColorId = colorId;
            local.ShipStyleId = styleId;
        }
    }

#pragma warning disable CS0067 // Offline sessions never raise inbound network events.

    /// <inheritdoc />
    public event Action<MatchState>? MatchStateChanged;

    /// <inheritdoc />
    public event Action<int>? CountdownChanged;

    /// <inheritdoc />
    public event Action<float>? MatchClockReceived;

    /// <inheritdoc />
    public event Action<MatchResult>? MatchCompletedReceived;

    /// <inheritdoc />
    public event Action<MatchCreatedPayload>? MatchCreated;

    /// <inheritdoc />
    public event Action<MatchStartingPayload>? MatchStarting;

    /// <inheritdoc />
    public event Action<WorldSnapshot>? WorldSnapshotReceived;

    /// <inheritdoc />
    public event Action<ProjectileSpawnedPayload>? ProjectileSpawnedReceived;

    /// <inheritdoc />
    public event Action<ProjectileDetonatedPayload>? ProjectileDetonatedReceived;

    /// <inheritdoc />
    public event Action<PowerUpSpawnedPayload>? PowerUpSpawnedReceived;

    /// <inheritdoc />
    public event Action<PowerUpCollectedPayload>? PowerUpCollectedReceived;

    /// <inheritdoc />
    public event Action<ShipSpawnedPayload>? ShipSpawnedReceived;

    /// <inheritdoc />
    public event Action<ShipDestroyedPayload>? ShipDestroyedReceived;

    /// <inheritdoc />
    public event Action<AsteroidSplitPayload>? AsteroidSplitReceived;

    /// <inheritdoc />
    public event Action<GameplayEventType, Vector2>? GameplayEventReceived;

    /// <inheritdoc />
    public event Action<int, Vector2, Vector2, bool, int>? ShipInputReceived;

    /// <inheritdoc />
    public event Action<int>? PlayerLeft;

    /// <inheritdoc />
    public event Action? RosterChanged;

    /// <inheritdoc />
    public event Action<PlatformResult>? ConnectionLost;

#pragma warning restore CS0067
}
