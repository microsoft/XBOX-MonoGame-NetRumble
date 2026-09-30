using System.Numerics;
using NetRumble.Core;
using NetRumble.Core.Net;

/// <summary>
/// An in-process two-peer transport: one host and one client, with the host's broadcasts
/// delivered straight into the client's inbound events.
/// </summary>
/// <remarks>
/// <para>
/// Replication is the one part of the port that cannot be exercised by the offline
/// provider - <see cref="OfflineMatchNetwork"/> swallows every broadcast by design. This
/// double is deliberately not a socket: it keeps the checks deterministic and lets a test
/// hold messages back to reproduce reordering, which is the failure mode the snapshot
/// frame guard exists for.
/// </para>
/// <para>
/// Delivery is synchronous and immediate unless <see cref="Queue"/> is set, in which case
/// messages accumulate and are released by <see cref="Flush"/>.
/// </para>
/// </remarks>
internal sealed class LoopbackHub
{
    private readonly List<Action> _queued = [];
    private readonly Dictionary<int, PlayerState> _roster;

    public LoopbackHub()
    {
        var hostPlayer = new PlayerState
        {
            PeerId = NRConst.HostPeerId,
            DisplayName = "Host",
            ShipColorId = 0,
            ShipStyleId = 0,
        };

        var clientPlayer = new PlayerState
        {
            PeerId = ClientPeerId,
            DisplayName = "Client",
            ShipColorId = 1,
            ShipStyleId = 1,
        };

        _roster = new Dictionary<int, PlayerState>
        {
            [hostPlayer.PeerId] = hostPlayer,
            [clientPlayer.PeerId] = clientPlayer,
        };

        Host = new LoopbackNetwork(this, _roster, NRConst.HostPeerId, isHost: true);
        Client = new LoopbackNetwork(this, _roster, ClientPeerId, isHost: false);
    }

    public const int ClientPeerId = 2;

    public LoopbackNetwork Host { get; }

    public LoopbackNetwork Client { get; }

    /// <summary>The shared roster both peers see, for checks that need more than two.</summary>
    public IReadOnlyDictionary<int, PlayerState> Roster => _roster;

    /// <summary>
    /// Adds a spectatorless extra peer to the shared roster. Standings and placement need
    /// a field bigger than two before an ordering bug can show itself.
    /// </summary>
    public PlayerState AddPlayer(int peerId, string displayName, int score = 0)
    {
        var player = new PlayerState
        {
            PeerId = peerId,
            DisplayName = displayName,
            Score = score,
            InGame = true,
        };

        _roster[peerId] = player;
        return player;
    }

    /// <summary>
    /// Drops a peer from the shared roster and announces it, so the last-player-standing
    /// rule sees the roster actually shrink rather than just hearing about a departure.
    /// </summary>
    public void RemovePlayer(int peerId)
    {
        _roster.Remove(peerId);
        Host.InjectPlayerLeft(peerId);
        Client.InjectPlayerLeft(peerId);
    }

    /// <summary>When true, host broadcasts are held instead of delivered.</summary>
    public bool Queue { get; set; }

    /// <summary>Number of messages currently held.</summary>
    public int QueuedCount => _queued.Count;

    /// <summary>Delivers held messages in the given order, then clears the queue.</summary>
    public void Flush(IEnumerable<int>? order = null)
    {
        var pending = _queued.ToList();
        _queued.Clear();

        foreach (var index in order ?? Enumerable.Range(0, pending.Count))
        {
            pending[index]();
        }
    }

    internal void Send(Action deliver)
    {
        if (Queue)
        {
            _queued.Add(deliver);
        }
        else
        {
            deliver();
        }
    }
}

internal sealed class LoopbackNetwork(
    LoopbackHub hub,
    IReadOnlyDictionary<int, PlayerState> players,
    int localPeerId,
    bool isHost) : IMatchNetwork
{
    public bool IsHost => isHost;

    public bool IsOffline => false;

    public int LocalPeerId => localPeerId;

    public IReadOnlyDictionary<int, PlayerState> Players => players;

    public PlayerState? LocalPlayer => players.GetValueOrDefault(localPeerId);

    /// <summary>Count of snapshots this peer has actually applied, for the frame guard check.</summary>
    public int SnapshotsDelivered { get; private set; }

    public IReadOnlyList<PlayerState> SortedPlayers()
        => [.. players.Values.OrderBy(p => p.PeerId)];

    public IReadOnlyList<PlayerState> PlayersByScore()
        => [.. players.Values.OrderByDescending(p => p.Score).ThenBy(p => p.PeerId)];

    // --- Host: outbound -----------------------------------------------------

    public void SetMatchState(MatchState state) => Relay(c => c.MatchStateChanged?.Invoke(state));

    public void ResetForNextMatch()
    {
        foreach (var player in players.Values)
        {
            player.Score = 0;
            player.IsReady = false;
            player.InGame = false;
            player.ShipId = 0;
        }
    }

    public void BroadcastCountdown(int secondsRemaining)
        => Relay(c => c.CountdownChanged?.Invoke(secondsRemaining));

    public void BroadcastMatchClock(float elapsed) => Relay(c => c.MatchClockReceived?.Invoke(elapsed));

    public void BroadcastScoreUpdated(int peerId, int score, int delta)
    {
        if (players.TryGetValue(peerId, out var player))
        {
            player.Score = score;
        }
    }

    public void BroadcastMatchCompleted(MatchResult result)
        => Relay(c => c.MatchCompletedReceived?.Invoke(result));

    /// <inheritdoc />
    public void PublishLocalReady(bool ready)
    {
        LocalPlayer!.IsReady = ready;
        RosterChanged?.Invoke();
    }

    /// <inheritdoc />
    public void PublishLocalAppearance(int colorId, int styleId)
    {
        LocalPlayer!.ShipColorId = colorId;
        LocalPlayer!.ShipStyleId = styleId;
        RosterChanged?.Invoke();
    }

    public void BroadcastMatchCreated(MatchCreatedPayload payload)
        => Relay(c => c.MatchCreated?.Invoke(payload));

    public void BroadcastMatchStarting(MatchStartingPayload payload)
        => Relay(c => c.MatchStarting?.Invoke(payload));

    public void BroadcastWorldSnapshot(WorldSnapshot snapshot)
        => Relay(c =>
        {
            c.SnapshotsDelivered++;
            c.WorldSnapshotReceived?.Invoke(snapshot);
        });

    public void BroadcastProjectileSpawned(ProjectileSpawnedPayload payload)
        => Relay(c => c.ProjectileSpawnedReceived?.Invoke(payload));

    public void BroadcastProjectileDetonated(ProjectileDetonatedPayload payload)
        => Relay(c => c.ProjectileDetonatedReceived?.Invoke(payload));

    public void BroadcastPowerUpSpawned(PowerUpSpawnedPayload payload)
        => Relay(c => c.PowerUpSpawnedReceived?.Invoke(payload));

    public void BroadcastPowerUpCollected(PowerUpCollectedPayload payload)
        => Relay(c => c.PowerUpCollectedReceived?.Invoke(payload));

    public void BroadcastShipSpawned(ShipSpawnedPayload payload)
        => Relay(c => c.ShipSpawnedReceived?.Invoke(payload));

    public void BroadcastShipDestroyed(ShipDestroyedPayload payload)
        => Relay(c => c.ShipDestroyedReceived?.Invoke(payload));

    public void BroadcastAsteroidSplit(AsteroidSplitPayload payload)
        => Relay(c => c.AsteroidSplitReceived?.Invoke(payload));

    public void BroadcastGameplayEvent(GameplayEventType eventType, Vector2 position)
        => Relay(c => c.GameplayEventReceived?.Invoke(eventType, position));

    private void Relay(Action<LoopbackNetwork> deliver)
    {
        if (!isHost)
        {
            return;
        }

        hub.Send(() => deliver(hub.Client));
    }

    // --- Client: outbound ---------------------------------------------------

    /// <summary>
    /// Carries the client's input up to the host, attributed to the sender rather than to
    /// anything in the payload - the same rule the real transport applies.
    /// </summary>
    public void SendShipInput(Vector2 movement, Vector2 fire, bool deployMine, int sequence)
    {
        if (isHost)
        {
            return;
        }

        hub.Send(() => hub.Host.InjectShipInput(localPeerId, movement, fire, deployMine, sequence));
    }

    // --- Inbound ------------------------------------------------------------

    public event Action<MatchState>? MatchStateChanged;

    public event Action<int>? CountdownChanged;

    public event Action<float>? MatchClockReceived;

    public event Action<MatchResult>? MatchCompletedReceived;

    public event Action<MatchCreatedPayload>? MatchCreated;

    public event Action<MatchStartingPayload>? MatchStarting;

    public event Action<WorldSnapshot>? WorldSnapshotReceived;

    public event Action<ProjectileSpawnedPayload>? ProjectileSpawnedReceived;

    public event Action<ProjectileDetonatedPayload>? ProjectileDetonatedReceived;

    public event Action<PowerUpSpawnedPayload>? PowerUpSpawnedReceived;

    public event Action<PowerUpCollectedPayload>? PowerUpCollectedReceived;

    public event Action<ShipSpawnedPayload>? ShipSpawnedReceived;

    public event Action<ShipDestroyedPayload>? ShipDestroyedReceived;

    public event Action<AsteroidSplitPayload>? AsteroidSplitReceived;

    public event Action<GameplayEventType, Vector2>? GameplayEventReceived;

    public event Action<int, Vector2, Vector2, bool, int>? ShipInputReceived;

    public event Action<int>? PlayerLeft;

    /// <inheritdoc />
    public event Action? RosterChanged;

#pragma warning disable CS0067 // Never raised by the loopback double; there is no transport to lose.
    /// <inheritdoc />
    public event Action<NetRumble.Platform.PlatformResult>? ConnectionLost;
#pragma warning restore CS0067

    /// <summary>Host-side test hook: feeds a client's input in as though it arrived.</summary>
    public void InjectShipInput(int peerId, Vector2 move, Vector2 fire, bool mine, int sequence)
        => ShipInputReceived?.Invoke(peerId, move, fire, mine, sequence);

    /// <summary>Test hook: simulates a peer dropping.</summary>
    public void InjectPlayerLeft(int peerId) => PlayerLeft?.Invoke(peerId);
}
