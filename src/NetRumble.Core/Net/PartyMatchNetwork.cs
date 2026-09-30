using System.Numerics;
using NetRumble.Core.Net.Wire;
using NetRumble.Platform;
using NetRumble.Platform.Diagnostics;

namespace NetRumble.Core.Net;

/// <summary>
/// Implements <see cref="IMatchNetwork"/> over a Party session.
/// </summary>
/// <remarks>
/// <para>
/// This is the layer Godot got for free. There, <c>NetManager</c> declared 27 <c>@rpc</c>
/// functions and the engine's high-level <c>MultiplayerAPI</c> handled dispatch,
/// serialisation, reliability selection and the authority check. MonoGame has none of
/// that, so this class reproduces all four explicitly over
/// <see cref="IPartyService.Send"/>.
/// </para>
/// <para>
/// <b>The authority check is security-relevant, not a formality.</b> In GDScript,
/// <c>@rpc("authority", ...)</c> made the engine drop a message that did not come from the
/// host. Party delivers every message with its true sender peer id, so
/// <see cref="OnMessageReceived"/> reproduces that: a world snapshot, score update or
/// match-completed packet is only honoured when it came from
/// <see cref="IPartyService.HostPeerId"/>. Without it any peer could rewrite the score,
/// teleport ships or end the match.
/// </para>
/// <para>
/// Conversely <c>@rpc("any_peer")</c> messages - ship input and the lobby submissions -
/// are accepted from anyone but are always attributed to the sender's own peer id rather
/// than one carried in the payload, so a client cannot drive another player's ship.
/// </para>
/// </remarks>
public sealed class PartyMatchNetwork : IMatchNetwork, IDisposable
{
    private readonly IPartyService _party;
    private readonly Dictionary<int, PlayerState> _players = [];
    private bool _disposed;

    /// <summary>
    /// The host's own record of the authoritative match state, tracked purely to let
    /// <see cref="ApplySubmittedIdentity"/> refuse a peer that connects after the lobby
    /// has moved on (XR-003). A client's copy of this class never calls
    /// <see cref="SetMatchState"/> - that method already no-ops for it - so this field
    /// only ever means anything on the host.
    /// </summary>
    private MatchState _matchState = MatchState.PlayersJoining;

    public PartyMatchNetwork(IPartyService party, PlayerState localPlayer)
    {
        ArgumentNullException.ThrowIfNull(party);
        ArgumentNullException.ThrowIfNull(localPlayer);

        _party = party;
        LocalPlayerState = localPlayer;

        localPlayer.PeerId = party.LocalPeerId;
        localPlayer.IsLocalPlayer = true;
        _players[localPlayer.PeerId] = localPlayer;

        _party.MessageReceived += OnMessageReceived;
        _party.PeerJoined += OnPeerJoined;
        _party.PeerLeft += OnPeerLeft;
        _party.NetworkDestroyed += OnNetworkDestroyed;
    }

    /// <summary>The local player's roster row, owned by the caller that constructed this.</summary>
    public PlayerState LocalPlayerState { get; }

    public bool IsHost => _party.IsHost;

    public bool IsOffline => false;

    public int LocalPeerId => _party.LocalPeerId;

    public IReadOnlyDictionary<int, PlayerState> Players => _players;

    public PlayerState? LocalPlayer => _players.GetValueOrDefault(LocalPeerId);

    /// <summary>Raised whenever the roster gains, loses or updates a row.</summary>
    public event Action? RosterChanged;

    /// <inheritdoc />
    public event Action<PlatformResult>? ConnectionLost;

    /// <summary>
    /// Raised on the host when a client reports it has finished loading into the match.
    /// </summary>
    public event Action<int>? PlayerLoaded;

    public IReadOnlyList<PlayerState> SortedPlayers()
        => [.. _players.Values.OrderBy(p => p.PeerId)];

    public IReadOnlyList<PlayerState> PlayersByScore()
        => [.. _players.Values.OrderByDescending(p => p.Score).ThenBy(p => p.PeerId)];

    // --- Host: outbound -----------------------------------------------------

    public void SetMatchState(MatchState state)
    {
        if (!IsHost)
        {
            return;
        }

        // Breadcrumb added to chase the 4-player "stuck at waiting for players" hang:
        // this field/message is shared between the lobby's "match is starting, stop
        // taking joins" signal and MatchDirector's own per-phase broadcast (Setup()
        // sends PlayersJoining right after this sends Starting), so seeing every
        // transition in order is the fastest way to tell whether a client missed one.
        CrashLog.Mark($"net(host): SetMatchState {_matchState} -> {state}, roster={_players.Count}");

        _matchState = state;

        using var w = new MessageWriter(MessageType.MatchState, 8);
        w.WriteByte((byte)state);
        Broadcast(w, MessageDelivery.Reliable);
    }

    /// <summary>
    /// Clears per-match roster state so this session can run another match (XR-003).
    /// </summary>
    /// <remarks>
    /// Every peer clears its own view, host included, because a client's roster is its
    /// own copy and would otherwise keep the finished match's scores until something
    /// happened to overwrite the row. The host then republishes the whole roster, which
    /// is the authoritative answer and corrects any peer whose local clear raced a late
    /// update from the match that just ended.
    /// </remarks>
    public void ResetForNextMatch()
    {
        foreach (var player in _players.Values)
        {
            player.Score = 0;
            player.IsReady = false;
            player.InGame = false;
            player.ShipId = 0;
        }

        if (IsHost)
        {
            _matchState = MatchState.Waiting;
            BroadcastRosterSnapshot();
        }

        RosterChanged?.Invoke();
    }

    public void BroadcastCountdown(int secondsRemaining)
    {
        if (!IsHost)
        {
            return;
        }

        using var w = new MessageWriter(MessageType.Countdown, 8);
        w.WriteInt(secondsRemaining);
        Broadcast(w, MessageDelivery.Reliable);
    }

    public void BroadcastMatchClock(float elapsed)
    {
        if (!IsHost)
        {
            return;
        }

        using var w = new MessageWriter(MessageType.MatchClock, 8);
        w.WriteFloat(elapsed);
        Broadcast(w, MessageDelivery.UnreliableSequenced);
    }

    public void BroadcastScoreUpdated(int peerId, int score, int delta)
    {
        // The host keeps its own roster authoritative even when there is nobody to tell,
        // so this half runs before the IsHost gate.
        if (_players.TryGetValue(peerId, out var player))
        {
            player.Score = score;
            RosterChanged?.Invoke();
        }

        if (!IsHost)
        {
            return;
        }

        using var w = new MessageWriter(MessageType.ScoreUpdated, 16);
        w.WriteInt(peerId);
        w.WriteInt(score);
        w.WriteInt(delta);
        Broadcast(w, MessageDelivery.Reliable);
    }

    public void BroadcastMatchCompleted(MatchResult result)
    {
        if (!IsHost)
        {
            return;
        }

        using var w = MatchMessageCodec.EncodeMatchCompleted(result);
        Broadcast(w, MessageDelivery.Reliable);
    }

    public void BroadcastMatchCreated(MatchCreatedPayload payload)
    {
        if (!IsHost)
        {
            return;
        }

        // Encoding and sending are marked separately because the announce is the first
        // large reliable message of a match and the two halves fail differently: a codec
        // fault is managed and lands in this log as an exception, while a transport fault
        // is native and leaves nothing behind but the absence of the "sent" line.
        CrashLog.Mark("net: encoding MatchCreated");

        using var w = MatchMessageCodec.EncodeMatchCreated(payload);

        CrashLog.Mark($"net: sending MatchCreated, {w.Written.Length} bytes");
        Broadcast(w, MessageDelivery.Reliable);
        CrashLog.Mark("net: sent MatchCreated");
    }

    public void BroadcastMatchStarting(MatchStartingPayload payload)
    {
        if (!IsHost)
        {
            return;
        }

        using var w = MatchMessageCodec.EncodeMatchStarting(payload);

        CrashLog.Mark($"net: sending MatchStarting, {w.Written.Length} bytes");
        Broadcast(w, MessageDelivery.Reliable);
        CrashLog.Mark("net: sent MatchStarting");
    }

    public void BroadcastWorldSnapshot(WorldSnapshot snapshot)
    {
        if (!IsHost)
        {
            return;
        }

        using var w = MatchMessageCodec.EncodeWorldSnapshot(snapshot);

        // Thirty of these a second, so only the first is recorded: it is the one that
        // proves the host survived the announce and reached steady-state traffic.
        CrashLog.MarkOnce("snapshot", $"net: sending first WorldSnapshot, {w.Written.Length} bytes");
        Broadcast(w, MessageDelivery.UnreliableSequenced);
        CrashLog.MarkOnce("snapshot-sent", "net: sent first WorldSnapshot");
    }

    public void BroadcastProjectileSpawned(ProjectileSpawnedPayload payload)
    {
        if (!IsHost)
        {
            return;
        }

        using var w = MatchMessageCodec.EncodeProjectileSpawned(payload);
        Broadcast(w, MessageDelivery.Reliable);
    }

    public void BroadcastProjectileDetonated(ProjectileDetonatedPayload payload)
    {
        if (!IsHost)
        {
            return;
        }

        using var w = MatchMessageCodec.EncodeProjectileDetonated(payload);
        Broadcast(w, MessageDelivery.Reliable);
    }

    public void BroadcastPowerUpSpawned(PowerUpSpawnedPayload payload)
    {
        if (!IsHost)
        {
            return;
        }

        using var w = MatchMessageCodec.EncodePowerUpSpawned(payload);
        Broadcast(w, MessageDelivery.Reliable);
    }

    public void BroadcastPowerUpCollected(PowerUpCollectedPayload payload)
    {
        if (!IsHost)
        {
            return;
        }

        using var w = MatchMessageCodec.EncodePowerUpCollected(payload);
        Broadcast(w, MessageDelivery.Reliable);
    }

    public void BroadcastShipSpawned(ShipSpawnedPayload payload)
    {
        if (!IsHost)
        {
            return;
        }

        using var w = MatchMessageCodec.EncodeShipSpawned(payload);
        Broadcast(w, MessageDelivery.Reliable);
    }

    public void BroadcastShipDestroyed(ShipDestroyedPayload payload)
    {
        if (!IsHost)
        {
            return;
        }

        using var w = MatchMessageCodec.EncodeShipDestroyed(payload);
        Broadcast(w, MessageDelivery.Reliable);
    }

    public void BroadcastAsteroidSplit(AsteroidSplitPayload payload)
    {
        if (!IsHost)
        {
            return;
        }

        using var w = MatchMessageCodec.EncodeAsteroidSplit(payload);
        Broadcast(w, MessageDelivery.Reliable);
    }

    public void BroadcastGameplayEvent(GameplayEventType eventType, Vector2 position)
    {
        if (!IsHost)
        {
            return;
        }

        using var w = MatchMessageCodec.EncodeGameplayEvent(eventType, position);
        Broadcast(w, MessageDelivery.Unreliable);
    }

    /// <summary>Host: pushes one player's roster row to every client.</summary>
    public void BroadcastRosterEntry(PlayerState player)
    {
        ArgumentNullException.ThrowIfNull(player);

        if (!IsHost)
        {
            return;
        }

        using var w = MatchMessageCodec.EncodeRosterEntry(player);
        Broadcast(w, MessageDelivery.Reliable);
    }

    private void BroadcastRosterSnapshot()
    {
        if (!IsHost)
        {
            return;
        }

        using var w = MatchMessageCodec.EncodeRosterSnapshot(_players.Values);
        Broadcast(w, MessageDelivery.Reliable);
    }

    // --- Client: outbound ---------------------------------------------------

    /// <summary>Client: sends one frame of local input to the host.</summary>
    /// <remarks>
    /// Carries no peer id. The host attributes it to the sender Party reports, so a
    /// modified client cannot drive somebody else's ship by lying in the payload.
    /// </remarks>
    public void SendShipInput(Vector2 movement, Vector2 fire, bool deployMine, int sequence)
    {
        if (IsHost)
        {
            return;
        }

        using var w = MatchMessageCodec.EncodeShipInput(movement, fire, deployMine, sequence);
        _party.Send(IPartyService.HostPeerId, w.Written, MessageDelivery.UnreliableSequenced);
    }

    /// <summary>Reports that this peer has finished loading into the match.</summary>
    /// <remarks>
    /// The host is a player too, and <c>MatchDirector.HandlePlayersLoading</c> waits for
    /// <see cref="PlayerState.InGame"/> on <b>every</b> entry in the roster before it will
    /// start the match. Returning early here because there is nobody to send to left the
    /// host's own flag false forever, so a networked match sat in
    /// <see cref="MatchState.PlayersJoining"/> and never began - both peers showed the
    /// gameplay screen, neither ever simulated. Follows the same host-applies-and-
    /// broadcasts, client-sends shape as <see cref="PublishLocalReady"/>.
    /// </remarks>
    public void SendLoaded()
    {
        CrashLog.Mark($"net: SendLoaded peer={LocalPeerId} host={IsHost}");

        if (IsHost)
        {
            LocalPlayerState.InGame = true;
            BroadcastRosterSnapshot();
            RosterChanged?.Invoke();
            PlayerLoaded?.Invoke(LocalPeerId);
            return;
        }

        using var w = new MessageWriter(MessageType.SubmitLoaded, 4);
        _party.Send(IPartyService.HostPeerId, w.Written, MessageDelivery.Reliable);
    }

    /// <summary>Client: starts the roster handshake after joining, or answers a host request.</summary>
    public void SendIdentity()
    {
        if (IsHost)
        {
            return;
        }

        using var w = new MessageWriter(MessageType.SubmitIdentity, 128);
        w.WriteString(LocalPlayerState.DisplayName);
        w.WriteString(LocalPlayerState.XboxUserId);
        w.WriteInt(LocalPlayerState.ShipColorId);
        w.WriteInt(LocalPlayerState.ShipStyleId);
        _party.Send(IPartyService.HostPeerId, w.Written, MessageDelivery.Reliable);
    }

    /// <summary>
    /// Client: sends the local ready toggle to the host. Reproduces the receiving half
    /// of <c>NetManager</c>'s <c>_submit_ready.rpc_id(HOST_PEER_ID, ...)</c> - this was
    /// missing entirely until this port's Phase 6, which is why a non-host client's
    /// ready toggle never reached the host; see <see cref="PublishLocalReady"/>, which
    /// is what call sites use instead of this directly.
    /// </summary>
    private void SendReady(bool ready)
    {
        if (IsHost)
        {
            return;
        }

        using var w = new MessageWriter(MessageType.SubmitReady, 8);
        w.WriteBool(ready);
        _party.Send(IPartyService.HostPeerId, w.Written, MessageDelivery.Reliable);
    }

    /// <summary>
    /// Client: sends a ship colour/style change to the host. Reproduces the receiving
    /// half of <c>NetManager</c>'s <c>_submit_appearance.rpc_id(HOST_PEER_ID, ...)</c>,
    /// missing for the same reason as <see cref="SendReady"/>; see
    /// <see cref="PublishLocalAppearance"/>.
    /// </summary>
    private void SendAppearance(int colorId, int styleId)
    {
        if (IsHost)
        {
            return;
        }

        using var w = new MessageWriter(MessageType.SubmitAppearance, 12);
        w.WriteInt(colorId);
        w.WriteInt(styleId);
        _party.Send(IPartyService.HostPeerId, w.Written, MessageDelivery.Reliable);
    }

    /// <inheritdoc />
    public void PublishLocalReady(bool ready)
    {
        LocalPlayerState.IsReady = ready;

        if (IsHost)
        {
            BroadcastRosterSnapshot();
        }
        else
        {
            SendReady(ready);
        }
    }

    /// <inheritdoc />
    public void PublishLocalAppearance(int colorId, int styleId)
    {
        LocalPlayerState.ShipColorId = colorId;
        LocalPlayerState.ShipStyleId = styleId;

        if (IsHost)
        {
            BroadcastRosterSnapshot();
        }
        else
        {
            SendAppearance(colorId, styleId);
        }
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

    /// <inheritdoc />
    public event Action<AsteroidSplitPayload>? AsteroidSplitReceived;

    public event Action<GameplayEventType, Vector2>? GameplayEventReceived;

    public event Action<int, Vector2, Vector2, bool, int>? ShipInputReceived;

    public event Action<int>? PlayerLeft;

    /// <summary>
    /// Decodes one packet and raises the matching event.
    /// </summary>
    /// <remarks>
    /// Every failure path is a silent drop. A packet that fails to decode came from a peer
    /// that disagrees with this build about the wire format, or from one that is being
    /// deliberately hostile; neither is worth tearing the session down for, and neither
    /// should reach the simulation.
    /// </remarks>
    private void OnMessageReceived(int senderPeerId, ReadOnlySpan<byte> payload)
    {
        var reader = new MessageReader(payload);

        if (!reader.TryReadMessageType(out var type))
        {
            return;
        }

        var fromHost = senderPeerId == IPartyService.HostPeerId;

        switch (type)
        {
            // --- Authority-only. Reproduces @rpc("authority"). ---------------

            case MessageType.MatchState when fromHost:
                if (reader.TryReadByte(out var rawState) && Enum.IsDefined((MatchState)rawState))
                {
                    CrashLog.Mark($"net(peer={LocalPeerId}): received MatchState={(MatchState)rawState}");
                    Finish(ref reader, () => MatchStateChanged?.Invoke((MatchState)rawState));
                }

                break;

            case MessageType.Countdown when fromHost:
                if (reader.TryReadInt(out var seconds))
                {
                    Finish(ref reader, () => CountdownChanged?.Invoke(seconds));
                }

                break;

            case MessageType.JoinRefused when fromHost:
                // XR-003: the host never added this peer to its roster, so there is
                // nothing here to unwind - just tell the caller why, through the same
                // channel a dropped connection uses, since the outcome for this screen
                // is identical: stop trying to join and explain what happened.
                Finish(
                    ref reader,
                    () => ConnectionLost?.Invoke(
                        PlatformResult.Fail(
                            PlatformStatus.Failed,
                            "This match has already started.",
                            "Host refused SubmitIdentity: match state was not Waiting.")));

                break;

            case MessageType.MatchClock when fromHost:
                if (reader.TryReadFloat(out var elapsed))
                {
                    Finish(ref reader, () => MatchClockReceived?.Invoke(elapsed));
                }

                break;

            case MessageType.ScoreUpdated when fromHost:
                if (reader.TryReadInt(out var scorePeer)
                    && reader.TryReadInt(out var score)
                    && reader.TryReadInt(out _)
                    && reader.IsFullyConsumed
                    && _players.TryGetValue(scorePeer, out var scored))
                {
                    scored.Score = score;
                    RosterChanged?.Invoke();
                }

                break;

            case MessageType.MatchCompleted when fromHost:
                if (MatchMessageCodec.TryDecodeMatchCompleted(ref reader, out var result))
                {
                    Finish(ref reader, () => MatchCompletedReceived?.Invoke(result));
                }

                break;

            case MessageType.MatchCreated when fromHost:
                if (MatchMessageCodec.TryDecodeMatchCreated(ref reader, out var created))
                {
                    Finish(ref reader, () => MatchCreated?.Invoke(created));
                }

                break;

            case MessageType.MatchStarting when fromHost:
                if (MatchMessageCodec.TryDecodeMatchStarting(ref reader, out var starting))
                {
                    Finish(ref reader, () => MatchStarting?.Invoke(starting));
                }

                break;

            case MessageType.WorldSnapshot when fromHost:
                if (MatchMessageCodec.TryDecodeWorldSnapshot(ref reader, out var snapshot))
                {
                    Finish(ref reader, () => WorldSnapshotReceived?.Invoke(snapshot));
                }

                break;

            case MessageType.ProjectileSpawned when fromHost:
                if (MatchMessageCodec.TryDecodeProjectileSpawned(ref reader, out var spawned))
                {
                    Finish(ref reader, () => ProjectileSpawnedReceived?.Invoke(spawned));
                }

                break;

            case MessageType.ProjectileDetonated when fromHost:
                if (MatchMessageCodec.TryDecodeProjectileDetonated(ref reader, out var detonated))
                {
                    Finish(ref reader, () => ProjectileDetonatedReceived?.Invoke(detonated));
                }

                break;

            case MessageType.PowerUpSpawned when fromHost:
                if (MatchMessageCodec.TryDecodePowerUpSpawned(ref reader, out var powerUpSpawned))
                {
                    Finish(ref reader, () => PowerUpSpawnedReceived?.Invoke(powerUpSpawned));
                }

                break;

            case MessageType.PowerUpCollected when fromHost:
                if (MatchMessageCodec.TryDecodePowerUpCollected(ref reader, out var collected))
                {
                    Finish(ref reader, () => PowerUpCollectedReceived?.Invoke(collected));
                }

                break;

            case MessageType.ShipSpawned when fromHost:
                if (MatchMessageCodec.TryDecodeShipSpawned(ref reader, out var shipSpawned))
                {
                    Finish(ref reader, () => ShipSpawnedReceived?.Invoke(shipSpawned));
                }

                break;

            case MessageType.ShipDestroyed when fromHost:
                if (MatchMessageCodec.TryDecodeShipDestroyed(ref reader, out var shipDestroyed))
                {
                    Finish(ref reader, () => ShipDestroyedReceived?.Invoke(shipDestroyed));
                }

                break;

            case MessageType.AsteroidSplit when fromHost:
                if (MatchMessageCodec.TryDecodeAsteroidSplit(ref reader, out var asteroidSplit))
                {
                    Finish(ref reader, () => AsteroidSplitReceived?.Invoke(asteroidSplit));
                }

                break;

            case MessageType.GameplayEvent when fromHost:
                if (MatchMessageCodec.TryDecodeGameplayEvent(ref reader, out var eventType, out var position))
                {
                    Finish(ref reader, () => GameplayEventReceived?.Invoke(eventType, position));
                }

                break;

            case MessageType.RosterEntry when fromHost:
                if (MatchMessageCodec.TryDecodeRosterEntry(ref reader, out var entry)
                    && reader.IsFullyConsumed)
                {
                    ApplyRosterEntry(entry);
                }

                break;

            case MessageType.RosterSnapshot when fromHost:
                if (MatchMessageCodec.TryDecodeRosterSnapshot(ref reader, out var roster)
                    && reader.IsFullyConsumed)
                {
                    ApplyRosterSnapshot(roster);
                }

                break;

            case MessageType.PlayerLeft when fromHost:
                if (reader.TryReadInt(out var leftPeer) && reader.IsFullyConsumed)
                {
                    RemovePlayer(leftPeer);
                }

                break;

            case MessageType.RequestIdentity when fromHost:
                SendIdentity();
                break;

            // --- Any peer, host-side. Reproduces @rpc("any_peer"). -----------

            case MessageType.ShipInput when IsHost:
                if (MatchMessageCodec.TryDecodeShipInput(
                        ref reader, out var movement, out var fire, out var mine, out var sequence)
                    && reader.IsFullyConsumed)
                {
                    // Attributed to the transport's sender id, never one from the payload.
                    ShipInputReceived?.Invoke(senderPeerId, movement, fire, mine, sequence);
                }

                break;

            case MessageType.SubmitIdentity when IsHost:
                ApplySubmittedIdentity(senderPeerId, ref reader);
                break;

            case MessageType.SubmitReady when IsHost:
                if (reader.TryReadBool(out var ready)
                    && reader.IsFullyConsumed
                    && _players.TryGetValue(senderPeerId, out var readyPlayer))
                {
                    readyPlayer.IsReady = ready;
                    BroadcastRosterSnapshot();
                    RosterChanged?.Invoke();
                }

                break;

            case MessageType.SubmitAppearance when IsHost:
                if (reader.TryReadInt(out var colorId)
                    && reader.TryReadInt(out var styleId)
                    && reader.IsFullyConsumed
                    && _players.TryGetValue(senderPeerId, out var dressed))
                {
                    dressed.ShipColorId = colorId;
                    dressed.ShipStyleId = styleId;
                    BroadcastRosterSnapshot();
                    RosterChanged?.Invoke();
                }

                break;

            case MessageType.SubmitLoaded when IsHost:
                if (reader.IsFullyConsumed && _players.TryGetValue(senderPeerId, out var loaded))
                {
                    loaded.InGame = true;
                    CrashLog.Mark(
                        $"net(host): SubmitLoaded from peer={senderPeerId}, "
                        + $"inGame={_players.Values.Count(p => p.InGame)}/{_players.Count}");
                    BroadcastRosterSnapshot();
                    RosterChanged?.Invoke();
                    PlayerLoaded?.Invoke(senderPeerId);
                }
                else
                {
                    CrashLog.Mark(
                        $"net(host): SubmitLoaded from peer={senderPeerId} IGNORED - "
                        + $"consumed={reader.IsFullyConsumed}, inRoster={_players.ContainsKey(senderPeerId)}");
                }

                break;

            default:
                // Unknown type, or an authority message from a peer that is not the host.
                break;
        }
    }

    /// <summary>
    /// Raises an event only if the packet was consumed exactly.
    /// </summary>
    /// <remarks>
    /// Trailing bytes mean the sender's layout does not match this build's. Applying the
    /// part that happened to decode would put the simulation into a state neither peer
    /// intended, so the whole packet is dropped instead.
    /// </remarks>
    private static void Finish(ref MessageReader reader, Action raise)
    {
        if (reader.IsFullyConsumed)
        {
            raise();
        }
    }

    private void ApplySubmittedIdentity(int senderPeerId, ref MessageReader reader)
    {
        if (!reader.TryReadString(out var displayName)
            || !reader.TryReadString(out var xboxUserId)
            || !reader.TryReadInt(out var colorId)
            || !reader.TryReadInt(out var styleId)
            || !reader.IsFullyConsumed)
        {
            return;
        }

        var isNewPeer = !_players.TryGetValue(senderPeerId, out var player);

        // XR-003: a peer already in the roster is re-submitting its identity (a
        // reconnect, or the roster catch-up above OnPeerJoined) and is always welcome
        // back; a *new* peer arriving once the match has left the joining/warm-up phase
        // is refused instead of being silently dropped into a match already running -
        // the lobby it connected through no longer exists as far as anyone still in the
        // match is concerned.
        if (isNewPeer && !_matchState.HasMatchState(MatchState.Waiting))
        {
            CrashLog.Mark(
                $"net(host): JoinRefused peer={senderPeerId}, matchState={_matchState}, "
                + $"roster={_players.Count}");
            using var refusal = new MessageWriter(MessageType.JoinRefused, 4);
            _party.Send(senderPeerId, refusal.Written, MessageDelivery.Reliable);
            return;
        }

        CrashLog.Mark(
            $"net(host): ApplySubmittedIdentity peer={senderPeerId} isNewPeer={isNewPeer} "
            + $"name={displayName} roster(before)={_players.Count}");

        if (isNewPeer)
        {
            player = new PlayerState { PeerId = senderPeerId };
            _players[senderPeerId] = player;
        }

        player!.DisplayName = displayName;
        player.XboxUserId = xboxUserId;
        player.ShipColorId = colorId;
        player.ShipStyleId = styleId;

        // Every client receives one complete authoritative view. Besides teaching the
        // new peer about everyone, this repairs any existing client that missed an
        // earlier incremental join, leave or row update.
        BroadcastRosterSnapshot();

        RosterChanged?.Invoke();
    }

    private void ApplyRosterEntry(PlayerState entry)
    {
        if (entry.PeerId == LocalPeerId)
        {
            // The host's echo of this machine's own row must not replace the local
            // instance: LocalShip, IsLocalPlayer and any UI binding point at that object.
            LocalPlayerState.Score = entry.Score;
            LocalPlayerState.IsReady = entry.IsReady;
            LocalPlayerState.InGame = entry.InGame;
            LocalPlayerState.ShipId = entry.ShipId;
        }
        else
        {
            _players[entry.PeerId] = entry;
        }

        RosterChanged?.Invoke();
    }

    private void ApplyRosterSnapshot(IReadOnlyList<PlayerState> roster)
    {
        var authoritativeIds = roster.Select(player => player.PeerId).ToHashSet();

        if (!authoritativeIds.Contains(LocalPeerId))
        {
            return;
        }

        var removed = _players.Keys
            .Where(peerId => peerId != LocalPeerId && !authoritativeIds.Contains(peerId))
            .ToArray();

        foreach (var peerId in removed)
        {
            _players.Remove(peerId);
            PlayerLeft?.Invoke(peerId);
        }

        foreach (var entry in roster)
        {
            if (entry.PeerId == LocalPeerId)
            {
                CopyRosterState(entry, LocalPlayerState);
                LocalPlayerState.IsLocalPlayer = true;
            }
            else
            {
                _players[entry.PeerId] = entry;
            }
        }

        RosterChanged?.Invoke();
    }

    private static void CopyRosterState(PlayerState source, PlayerState destination)
    {
        destination.DisplayName = source.DisplayName;
        destination.XboxUserId = source.XboxUserId;
        destination.ShipColorId = source.ShipColorId;
        destination.ShipStyleId = source.ShipStyleId;
        destination.Score = source.Score;
        destination.IsReady = source.IsReady;
        destination.InGame = source.InGame;
        destination.ShipId = source.ShipId;
    }

    private void OnPeerJoined(int peerId)
    {
        CrashLog.Mark($"net(peer={LocalPeerId}): OnPeerJoined {peerId}, host={IsHost}");

        if (!IsHost)
        {
            return;
        }

        using var w = new MessageWriter(MessageType.RequestIdentity, 4);
        _party.Send(peerId, w.Written, MessageDelivery.Reliable);
    }

    private void OnPeerLeft(int peerId)
    {
        CrashLog.Mark($"net(peer={LocalPeerId}): OnPeerLeft {peerId}, host={IsHost}");
        RemovePlayer(peerId);

        if (!IsHost)
        {
            return;
        }

        using var w = new MessageWriter(MessageType.PlayerLeft, 8);
        w.WriteInt(peerId);
        Broadcast(w, MessageDelivery.Reliable);
        BroadcastRosterSnapshot();
    }

    private void RemovePlayer(int peerId)
    {
        if (peerId == LocalPeerId || !_players.Remove(peerId))
        {
            return;
        }

        PlayerLeft?.Invoke(peerId);
        RosterChanged?.Invoke();
    }

    private void Broadcast(in MessageWriter writer, MessageDelivery delivery)
        => _party.Send(IPartyService.PartyBroadcast, writer.Written, delivery);

    private void OnNetworkDestroyed(PlatformResult result) => ConnectionLost?.Invoke(result);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _party.MessageReceived -= OnMessageReceived;
        _party.PeerJoined -= OnPeerJoined;
        _party.PeerLeft -= OnPeerLeft;
        _party.NetworkDestroyed -= OnNetworkDestroyed;
    }
}
