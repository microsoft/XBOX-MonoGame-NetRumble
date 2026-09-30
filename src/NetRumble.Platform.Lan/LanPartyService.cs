using NetRumble.Platform.Networking;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using NetRumble.Platform.Diagnostics;

namespace NetRumble.Platform.Lan;

/// <summary>
/// Tunables for <see cref="LanPartyService"/>. Defaults are what a normal run uses; the
/// spike overrides the ports so several instances can share one machine.
/// </summary>
public sealed record LanPartyOptions
{
    /// <summary>UDP port the host listens on for gameplay. 0 picks an ephemeral port.</summary>
    public int GamePort { get; init; }

    /// <summary>Port join-code discovery broadcasts go to.</summary>
    public int DiscoveryPort { get; init; } = LanProtocol.DiscoveryPort;

    /// <summary>
    /// When false the host does not open the discovery socket, and join codes cannot be
    /// resolved - a caller must use <see cref="LanPartyService.JoinByConnectionStringAsync"/>.
    /// </summary>
    /// <remarks>
    /// Exists for automated checks: two instances in one process can connect directly by
    /// endpoint without depending on the machine's broadcast behaviour, which varies with
    /// the firewall profile and is not something a test should be asserting about.
    /// </remarks>
    public bool EnableDiscovery { get; init; } = true;
}

/// <summary>
/// A real <see cref="IPartyService"/> over UDP, for LAN play and for exercising the netcode
/// across a genuine process boundary.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Every other implementation of this interface is either a no-op
/// (<c>OfflinePartyService</c>), blocked on credentials this repository does not have
/// (<c>GameCorePartyService</c>), or an in-process double (<c>LoopbackPartyService</c>).
/// Until this class, no packet the match protocol produced had ever been through a socket,
/// so nothing proved that a <c>WorldSnapshot</c> fits a datagram, that reordering is
/// handled, or that a dropped roster message is recovered.
/// </para>
/// <para>
/// <b>Topology is a star, and that is not a simplification.</b> The host is the authority
/// and <c>PartyMatchNetwork</c> only ever sends client-to-host or host-to-broadcast - a
/// client never addresses another client. So a hub topology carries the entire protocol
/// with no relaying, and the host's authority check stays exactly where it already is.
/// </para>
/// <para>
/// <b>Threading.</b> A background thread does nothing but block on the socket and enqueue
/// what arrives. Every piece of state in this class, and every event it raises, is touched
/// only from <see cref="Pump"/>. That is what satisfies <see cref="IPlatformRuntime"/>'s
/// contract that game code never needs a lock - the same guarantee the GDK provider gets
/// from a manual-dispatch task queue.
/// </para>
/// <para>
/// <b>Trust model.</b> A peer is identified by the endpoint its datagrams come from, which
/// is appropriate for a LAN and not for the open internet: there is no handshake secret, so
/// an attacker who can forge a source address can impersonate a peer. The authority check
/// in <c>PartyMatchNetwork</c> still holds - only the host's endpoint may send host
/// messages - but that is the limit of what this transport enforces.
/// </para>
/// </remarks>
public sealed class LanPartyService : IPartyService, IDisposable
{
    private const long HeartbeatIntervalMs = 1_000;
    private const long LinkTimeoutMs = 8_000;
    private const long ConnectRetryIntervalMs = 250;
    private const long ConnectTimeoutMs = 5_000;

    /// <summary>Longest text chat line carried, in UTF-8 bytes.</summary>
    private const int MaxChatBytes = 512;

    /// <summary>
    /// Datagrams the receive threads may have queued for the next <see cref="Pump"/>
    /// before they start dropping. See the drop site in the receive loop.
    /// </summary>
    private const int MaxInboxDepth = 4096;

    private readonly LanPartyOptions _options;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ConcurrentQueue<Inbound> _inbox = new();    private readonly Dictionary<int, LanLink> _links = [];
    private readonly List<byte[]> _delivered = [];
    private readonly HashSet<int> _mutedPeers = [];
    private readonly Dictionary<int, ChatRestriction> _restrictions = [];

    private Socket? _gameSocket;
    private Socket? _discoverySocket;
    private Thread? _gameReceiver;
    private Thread? _discoveryReceiver;
    private volatile bool _running;

    private int _maxPlayers = 2;
    private string _gameMode = string.Empty;

    /// <summary>
    /// The wire contract this instance speaks, supplied by the caller that hosted or
    /// joined. Truncated on the way in so it always survives a round trip through the
    /// handshake unchanged - a silently truncated version would look like a mismatch to
    /// the host and like a match to the joiner.
    /// </summary>
    private string _protocolVersion = string.Empty;
    private uint _connectToken;
    private PendingConnect? _pendingConnect;
    private bool _disposed;

    public LanPartyService(LanPartyOptions? options = null) => _options = options ?? new LanPartyOptions();

    public bool HasNetwork { get; private set; }

    public bool IsHost { get; private set; }

    public int LocalPeerId { get; private set; }

    public string ConnectionString { get; private set; } = string.Empty;

    public string JoinCode { get; private set; } = string.Empty;

    /// <summary>The UDP port this instance is bound to. 0 until a session exists.</summary>
    public int BoundPort => (_gameSocket?.LocalEndPoint as IPEndPoint)?.Port ?? 0;

    /// <summary>Peers currently connected, not counting this one.</summary>
    public int PeerCount => _links.Count;

    public bool ChatAllowed { get; set; } = true;

    public bool IsSelfMuted { get; set; }

    public event Action<int>? PeerJoined;

    public event Action<int>? PeerLeft;

    public event Action<PlatformResult>? NetworkDestroyed;

    public event PartyMessageHandler? MessageReceived;

    public event Action? ChatChanged;

    public event Action<int, string, ChatTextKind>? ChatTextReceived;

    // --- Session lifecycle --------------------------------------------------

    public Task<PlatformResult<string>> HostAsync(
        int maxPlayers,
        string gameMode,
        string protocolVersion,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(PlatformResult<string>.Canceled());
        }

        if (HasNetwork)
        {
            return Task.FromResult(PlatformResult<string>.Fail(
                PlatformStatus.Failed,
                "A session is already running.",
                "LanPartyService.HostAsync called with a live network."));
        }

        try
        {
            OpenGameSocket(_options.GamePort);
        }
        catch (SocketException ex)
        {
            return Task.FromResult(PlatformResult<string>.Fail(
                PlatformStatus.NetworkFailure,
                "Could not open a network port for the match.",
                ex.Message));
        }

        _maxPlayers = Math.Max(2, maxPlayers);
        _gameMode = Truncate(gameMode, 32);
        _protocolVersion = Truncate(protocolVersion, NRProtocol.MaxLength);
        IsHost = true;
        LocalPeerId = IPartyService.HostPeerId;
        HasNetwork = true;
        JoinCode = LanProtocol.NewJoinCode();
        ConnectionString = $"lan://{LocalAddress()}:{BoundPort}";

        if (_options.EnableDiscovery && !TryOpenDiscoverySocket())
        {
            // A refused discovery port is not fatal: the match still runs, and an invite
            // carrying the connection string still joins it. Only code lookup is lost, so
            // the code is cleared rather than advertised as something that cannot resolve.
            JoinCode = string.Empty;
        }

        return Task.FromResult(PlatformResult<string>.Ok(JoinCode));
    }

    public Task<PlatformResult> JoinAsync(
        string joinCode,
        string protocolVersion,
        CancellationToken cancellationToken = default)
    {
        if (!LanProtocol.IsJoinCode(joinCode))
        {
            return Task.FromResult(PlatformResult.Fail(
                PlatformStatus.Failed,
                "That join code is not valid.",
                $"Rejected join code '{joinCode}'."));
        }

        _protocolVersion = Truncate(protocolVersion, NRProtocol.MaxLength);
        return BeginJoin(joinCode.ToUpperInvariant(), target: null, cancellationToken);
    }

    public Task<PlatformResult> JoinByConnectionStringAsync(
        string connectionString,
        string protocolVersion,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseConnectionString(connectionString, out var target))
        {
            return Task.FromResult(PlatformResult.Fail(
                PlatformStatus.Failed,
                "That invite could not be read.",
                $"Unparsable LAN connection string '{connectionString}'."));
        }

        _protocolVersion = Truncate(protocolVersion, NRProtocol.MaxLength);
        return BeginJoin(joinCode: string.Empty, target, cancellationToken);
    }

    public Task LeaveAsync()
    {
        if (HasNetwork)
        {
            foreach (var link in _links.Values)
            {
                SendControl(link.EndPoint, LanPacketKind.Disconnect);
                link.Closed = true;
            }
        }

        // Deliberately does not raise NetworkDestroyed. That event means the transport went
        // away underneath the match, which the lobby turns into a "connection lost" dialog;
        // a player who chose to leave has already seen the screen change.
        Teardown();
        return Task.CompletedTask;
    }

    // --- Messaging ----------------------------------------------------------

    /// <summary>
    /// The thread <see cref="Pump"/> last ran on, so <see cref="Send"/> can tell when it
    /// is being called from somewhere that will corrupt the link state.
    /// </summary>
    /// <remarks>
    /// Read rather than asserted at construction because the pump thread is whichever
    /// thread drives the game loop, which this service is in no position to know.
    /// </remarks>
    private int _pumpThreadId;

    public void Send(int peerId, ReadOnlySpan<byte> payload, MessageDelivery delivery)
    {
        if (!HasNetwork || payload.IsEmpty)
        {
            return;
        }

        // This transport is single-threaded by design - the receive threads do nothing but
        // enqueue - and sending mutates the same reliable-message dictionaries that
        // ServiceLinks walks. A send from anywhere else is a crash waiting for the right
        // frame: it was found exactly that way, by a LAN client dying in
        // LanLink.Retransmit with "collection was modified" while the lobby's join
        // continuation sent from a thread pool thread. Recorded rather than thrown,
        // because the point is to name the caller, and once per process because a caller
        // that does it once does it every frame.
        if (_pumpThreadId != 0 && _pumpThreadId != Environment.CurrentManagedThreadId)
        {
            CrashLog.MarkOnce(
                "lan-send-thread",
                $"party: Send off the pump thread ({Environment.CurrentManagedThreadId} vs {_pumpThreadId})"
                + Environment.NewLine
                + Environment.StackTrace);
        }

        var now = _clock.ElapsedMilliseconds;

        if (peerId == IPartyService.PartyBroadcast)
        {
            foreach (var link in _links.Values)
            {
                link.Send(payload, delivery, now, Transmit);
            }

            return;
        }

        if (_links.TryGetValue(peerId, out var target))
        {
            target.Send(payload, delivery, now, Transmit);
        }
    }

    /// <summary>
    /// Drains the socket, delivers what arrived, and drives retransmission and timeouts.
    /// Must be called once per frame from the thread that owns the game state.
    /// </summary>
    public void Pump()
    {
        if (!_running)
        {
            return;
        }

        _pumpThreadId = Environment.CurrentManagedThreadId;

        var now = _clock.ElapsedMilliseconds;

        while (_inbox.TryDequeue(out var inbound))
        {
            ProcessDatagram(inbound, now);
        }

        DriveConnect(now);
        ServiceLinks(now);
    }

    // --- Chat ---------------------------------------------------------------

    public void SetPeerRestrictions(int peerId, bool allowVoice, bool allowText)
    {
        _restrictions[peerId] = new ChatRestriction(allowVoice, allowText);
        ChatChanged?.Invoke();
    }

    public void SetPeerMuted(int peerId, bool muted)
    {
        var changed = muted ? _mutedPeers.Add(peerId) : _mutedPeers.Remove(peerId);

        if (changed)
        {
            ChatChanged?.Invoke();
        }
    }

    public bool IsPeerMuted(int peerId) => _mutedPeers.Contains(peerId);

    /// <summary>
    /// Mirrors <c>party_service.gd</c>'s "no chat control means None" fallback.
    /// </summary>
    /// <remarks>
    /// <see cref="ChatIndicator.Talking"/> is unreachable here, as it was through the Godot
    /// addon: this transport carries text chat but no voice, so there is no audio level to
    /// report. The indicator therefore says whether a peer's chat would reach this player,
    /// not whether they are speaking.
    /// </remarks>
    public ChatIndicator GetChatIndicator(int peerId)
    {
        if (!HasNetwork || !ChatAllowed)
        {
            return ChatIndicator.None;
        }

        if (peerId == LocalPeerId)
        {
            return IsSelfMuted ? ChatIndicator.Muted : ChatIndicator.Available;
        }

        if (IsPeerMuted(peerId)
            || (_restrictions.TryGetValue(peerId, out var restriction) && !restriction.AllowVoice))
        {
            return ChatIndicator.Muted;
        }

        return ChatIndicator.Available;
    }

    public void ClearChatRestrictions()
    {
        _mutedPeers.Clear();
        _restrictions.Clear();
        ChatChanged?.Invoke();
    }

    public PlatformResult SendChatText(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return PlatformResult.Fail(PlatformStatus.Failed, "There is nothing to send.");
        }

        // XR-018 wants the refusal to say why, so the three reasons stay distinct rather
        // than collapsing into one "chat unavailable".
        if (!ChatAllowed)
        {
            return PlatformResult.Fail(
                PlatformStatus.NoPrivilege,
                "Your account settings do not allow chat.");
        }

        if (!HasNetwork)
        {
            return PlatformResult.Fail(PlatformStatus.Failed, "You are not in a match.");
        }

        if (IsSelfMuted)
        {
            return PlatformResult.Fail(PlatformStatus.Failed, "You are muted.");
        }

        var trimmed = Truncate(message, MaxChatBytes / 4);

        // Built per link rather than once, because each link stamps its own session
        // token. The body is identical; only the prefix differs.
        foreach (var link in _links.Values)
        {
            var payload = new byte[LanProtocol.LinkPrefixSize + 4 + 2 + MaxChatBytes];
            var span = payload.AsSpan();
            var offset = LanProtocol.WriteLinkPrefix(span, LanPacketKind.ChatText, link.SessionToken);
            BinaryPrimitives.WriteInt32LittleEndian(span[offset..], LocalPeerId);
            offset += 4;
            offset += LanProtocol.WriteString(span[offset..], trimmed);

            Transmit(link.EndPoint, payload, offset);
        }

        // Echoed locally so the sender sees their own line, matching the Godot chat log.
        ChatTextReceived?.Invoke(LocalPeerId, trimmed, ChatTextKind.Typed);
        return PlatformResult.Ok();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Teardown();
    }

    // --- Connect ------------------------------------------------------------

    private Task<PlatformResult> BeginJoin(
        string joinCode,
        IPEndPoint? target,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(PlatformResult.Canceled());
        }

        if (HasNetwork || _pendingConnect is not null)
        {
            return Task.FromResult(PlatformResult.Fail(
                PlatformStatus.Failed,
                "A session is already running.",
                "LanPartyService join attempted with a live network."));
        }

        try
        {
            OpenGameSocket(port: 0);
        }
        catch (SocketException ex)
        {
            return Task.FromResult(PlatformResult.Fail(
                PlatformStatus.NetworkFailure,
                "Could not open a network port for the match.",
                ex.Message));
        }

        // Cryptographic, because this value is the only thing authenticating the
        // ConnectAccept that decides who the host is. Random.Shared is a xoshiro PRNG
        // whose state is recoverable from a handful of observed outputs, and Next(1, max)
        // gives 31 bits rather than 32.
        _connectToken = BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4));

        var pending = new PendingConnect(
            joinCode,
            target,
            _clock.ElapsedMilliseconds + ConnectTimeoutMs,
            cancellationToken);

        _pendingConnect = pending;
        return pending.Completion.Task;
    }

    /// <summary>Retries discovery or the connect request until it lands or the deadline passes.</summary>
    private void DriveConnect(long nowMs)
    {
        var pending = _pendingConnect;

        if (pending is null)
        {
            return;
        }

        if (pending.CancellationToken.IsCancellationRequested)
        {
            FinishConnect(PlatformResult.Canceled());
            return;
        }

        if (nowMs >= pending.DeadlineMs)
        {
            FinishConnect(pending.Target is null
                ? PlatformResult.Fail(
                    PlatformStatus.TimedOut,
                    "No match was found for that join code.",
                    $"No DiscoverReply for '{pending.JoinCode}' within {ConnectTimeoutMs} ms.")
                : PlatformResult.Fail(
                    PlatformStatus.TimedOut,
                    "The host did not respond.",
                    $"No ConnectAccept from {pending.Target} within {ConnectTimeoutMs} ms."));

            return;
        }

        if (nowMs - pending.LastAttemptMs < ConnectRetryIntervalMs)
        {
            return;
        }

        pending.LastAttemptMs = nowMs;

        if (pending.Target is null)
        {
            BroadcastDiscovery(pending.JoinCode);
        }
        else
        {
            SendConnect(pending.Target);
        }
    }

    private void FinishConnect(PlatformResult result)
    {
        var pending = _pendingConnect;
        _pendingConnect = null;

        if (pending is null)
        {
            return;
        }

        if (result.Failed)
        {
            Teardown();
        }

        pending.Completion.TrySetResult(result);
    }

    private void BroadcastDiscovery(string joinCode)
    {
        var datagram = new byte[LanProtocol.PrefixSize + 2 + LanProtocol.JoinCodeLength];
        var span = datagram.AsSpan();
        var offset = LanProtocol.WritePrefix(span, LanPacketKind.DiscoverQuery);
        offset += LanProtocol.WriteString(span[offset..], joinCode);

        foreach (var address in BroadcastTargets())
        {
            Transmit(new IPEndPoint(address, _options.DiscoveryPort), datagram, offset);
        }
    }

    private void SendConnect(IPEndPoint target)
    {
        var datagram = new byte[LanProtocol.PrefixSize + 4 + 2 + NRProtocol.MaxLength];
        var span = datagram.AsSpan();
        var offset = LanProtocol.WritePrefix(span, LanPacketKind.Connect);
        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], _connectToken);
        offset += 4;

        // The joiner states its wire contract here rather than in discovery, because this
        // is the one packet on both join paths - a code lookup and an invite's connection
        // string - and it is the last exchange before either side sends match traffic.
        offset += LanProtocol.WriteString(span[offset..], _protocolVersion);
        Transmit(target, datagram, offset);
    }

    // --- Inbound ------------------------------------------------------------

    private void ProcessDatagram(Inbound inbound, long nowMs)
    {
        if (!LanProtocol.TryReadPrefix(inbound.Data.AsSpan(0, inbound.Length), out var kind, out var body))
        {
            return;
        }

        // The discovery socket is a well-known, ReuseAddress-shared port. It answers
        // discovery and nothing else, so an attacker cannot reach the handshake or link
        // paths without first finding the ephemeral game port.
        if (inbound.Discovery != LanProtocol.IsDiscovery(kind))
        {
            return;
        }

        // A link-scoped datagram must carry this link's session token. Source address
        // alone used to be the whole of a peer's identity, which made every one of the
        // paths below forgeable by anything that could send a UDP packet.
        if (LanProtocol.IsLinkScoped(kind))
        {
            if (!LanProtocol.TryReadSessionToken(ref body, out var token) || !IsLinkToken(inbound.From, token))
            {
                return;
            }
        }

        switch (kind)
        {
            case LanPacketKind.DiscoverQuery when IsHost:
                OnDiscoverQuery(body, inbound.From);
                break;

            case LanPacketKind.DiscoverReply:
                OnDiscoverReply(body, inbound.From);
                break;

            case LanPacketKind.Connect when IsHost:
                OnConnect(body, inbound.From, nowMs);
                break;

            case LanPacketKind.ConnectAccept:
                OnConnectAccept(body, inbound.From, nowMs);
                break;

            case LanPacketKind.ConnectReject:
                OnConnectReject(body, inbound.From);
                break;

            case LanPacketKind.Disconnect:
                OnDisconnect(inbound.From);
                break;

            case LanPacketKind.Heartbeat:
                Touch(inbound.From, nowMs);
                break;

            case LanPacketKind.Payload:
                OnPayload(body, inbound.From, nowMs);
                break;

            case LanPacketKind.Ack:
                OnAck(body, inbound.From, nowMs);
                break;

            case LanPacketKind.ChatText:
                OnChatText(body, inbound.From, nowMs);
                break;

            default:
                break;
        }
    }

    private void OnDiscoverQuery(ReadOnlySpan<byte> body, IPEndPoint from)
    {
        if (JoinCode.Length == 0
            || !LanProtocol.TryReadString(ref body, LanProtocol.JoinCodeLength, out var code)
            || !string.Equals(code, JoinCode, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var datagram = new byte[LanProtocol.PrefixSize + 2 + LanProtocol.JoinCodeLength + 6 + 2 + 256];
        var span = datagram.AsSpan();
        var offset = LanProtocol.WritePrefix(span, LanPacketKind.DiscoverReply);
        offset += LanProtocol.WriteString(span[offset..], JoinCode);
        BinaryPrimitives.WriteUInt16LittleEndian(span[offset..], (ushort)BoundPort);
        offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(span[offset..], (ushort)(_links.Count + 1));
        offset += 2;
        BinaryPrimitives.WriteUInt16LittleEndian(span[offset..], (ushort)_maxPlayers);
        offset += 2;
        offset += LanProtocol.WriteString(span[offset..], _gameMode);

        // Answered from the discovery socket so the reply's source port matches the port the
        // query was sent to, which is what lets a client behind a stateful firewall see it.
        TransmitFrom(_discoverySocket, from, datagram, offset);
    }

    private void OnDiscoverReply(ReadOnlySpan<byte> body, IPEndPoint from)
    {
        var pending = _pendingConnect;

        if (pending is null || pending.Target is not null)
        {
            return;
        }

        if (!LanProtocol.TryReadString(ref body, LanProtocol.JoinCodeLength, out var code)
            || !string.Equals(code, pending.JoinCode, StringComparison.OrdinalIgnoreCase)
            || body.Length < 2)
        {
            return;
        }

        var gamePort = BinaryPrimitives.ReadUInt16LittleEndian(body);

        if (gamePort == 0)
        {
            return;
        }

        // The host's game port comes from the reply, but its address comes from where the
        // reply actually arrived from - a host with several NICs would otherwise advertise
        // the wrong one of them.
        pending.Target = new IPEndPoint(from.Address, gamePort);
        pending.LastAttemptMs = 0;
    }

    private void OnConnect(ReadOnlySpan<byte> body, IPEndPoint from, long nowMs)
    {
        if (body.Length < 4)
        {
            return;
        }

        var token = BinaryPrimitives.ReadUInt32LittleEndian(body);
        var existing = FindLink(from);

        // Read before the retry branch so a peer cannot get in by resending a Connect that
        // omits the version. An unreadable or absent version reads as empty, which is
        // exactly what a build older than this check sends, and is refused as such.
        var versionBody = body[4..];

        if (!LanProtocol.TryReadString(ref versionBody, NRProtocol.MaxLength, out var peerVersion))
        {
            peerVersion = string.Empty;
        }

        if (!string.Equals(peerVersion, _protocolVersion, StringComparison.Ordinal))
        {
            // Refused rather than warned: the alternative is a peer that connects, joins
            // the roster, and then silently misreads every message the match sends it.
            SendConnectReject(from, token, NRProtocol.MismatchMessage(_protocolVersion, peerVersion));
            return;
        }

        if (existing is not null)
        {
            // A retry whose accept was lost. Re-answer with the same peer id and the same
            // session token rather than allocating a second one for a peer that is
            // already in the roster - reissuing the token would invalidate the link the
            // peer may already be using.
            SendConnectAccept(from, token, existing.PeerId, existing.SessionToken);
            existing.LastHeardMs = nowMs;
            return;
        }

        if (_links.Count + 1 >= _maxPlayers)
        {
            SendConnectReject(from, token, "This match is full.");
            return;
        }

        var peerId = NextPeerId();
        var sessionToken = LanProtocol.NewSessionToken();
        var link = new LanLink(peerId, from) { LastHeardMs = nowMs, SessionToken = sessionToken };
        _links[peerId] = link;

        SendConnectAccept(from, token, peerId, sessionToken);
        PeerJoined?.Invoke(peerId);
    }

    private void OnConnectAccept(ReadOnlySpan<byte> body, IPEndPoint from, long nowMs)
    {
        var pending = _pendingConnect;

        // The sender must be the endpoint we actually sent Connect to, exactly as
        // OnConnectReject below has always required. Without this, any machine that wins
        // the discovery race - or simply guesses - can answer as the host and be installed
        // as the authority for the whole match.
        if (pending?.Target is null || body.Length < 16 || !from.Equals(pending.Target))
        {
            return;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(body) != _connectToken)
        {
            return;
        }

        var assignedPeerId = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
        var sessionToken = BinaryPrimitives.ReadUInt64LittleEndian(body[8..]);

        if (assignedPeerId <= IPartyService.HostPeerId || sessionToken == 0)
        {
            return;
        }

        IsHost = false;
        LocalPeerId = assignedPeerId;
        HasNetwork = true;
        JoinCode = pending.JoinCode;
        ConnectionString = $"lan://{from.Address}:{from.Port}";
        _links[IPartyService.HostPeerId] = new LanLink(IPartyService.HostPeerId, from)
        {
            LastHeardMs = nowMs,
            SessionToken = sessionToken,
        };

        FinishConnect(PlatformResult.Ok());

        // Raised after the connect task completes, and deliberately raised at all: the
        // host announces the client, so the client must announce the host, or the event
        // means "a peer appeared" on one side and "a client appeared" on the other. An
        // autopiloted client waiting for a peer sat forever on exactly that asymmetry.
        // PartyMatchNetwork.OnPeerJoined ignores it when not the host, so this changes
        // no roster behaviour; it makes the transport contract honest.
        PeerJoined?.Invoke(IPartyService.HostPeerId);
    }

    private void OnConnectReject(ReadOnlySpan<byte> body, IPEndPoint from)
    {
        var pending = _pendingConnect;

        if (pending?.Target is null || body.Length < 4 || !from.Equals(pending.Target))
        {
            return;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(body) != _connectToken)
        {
            return;
        }

        var reasonBody = body[4..];
        var reason = LanProtocol.TryReadString(ref reasonBody, 256, out var text) && text.Length > 0
            ? text
            : "The host refused the connection.";

        FinishConnect(PlatformResult.Fail(PlatformStatus.Failed, reason, $"ConnectReject from {from}."));
    }

    private void OnDisconnect(IPEndPoint from)
    {
        var link = FindLink(from);

        if (link is null)
        {
            return;
        }

        if (IsHost)
        {
            DropPeer(link.PeerId);
            return;
        }

        LoseNetwork(PlatformResult.Fail(
            PlatformStatus.NetworkFailure,
            "The host ended the match.",
            $"Disconnect from {from}."));
    }

    private void OnPayload(ReadOnlySpan<byte> body, IPEndPoint from, long nowMs)
    {
        var link = FindLink(from);

        if (link is null)
        {
            return;
        }

        link.LastHeardMs = nowMs;
        _delivered.Clear();
        link.OnPayload(body, nowMs, _delivered, out var ackSequence, out var ackFragment);

        if (ackFragment >= 0)
        {
            SendAck(from, ackSequence, ackFragment);
        }

        foreach (var message in _delivered)
        {
            MessageReceived?.Invoke(link.PeerId, message);
        }

        _delivered.Clear();
    }

    private void OnAck(ReadOnlySpan<byte> body, IPEndPoint from, long nowMs)
    {
        var link = FindLink(from);

        if (link is null || body.Length < 6)
        {
            return;
        }

        link.LastHeardMs = nowMs;
        link.OnAck(
            BinaryPrimitives.ReadUInt32LittleEndian(body),
            BinaryPrimitives.ReadUInt16LittleEndian(body[4..]));
    }

    private void OnChatText(ReadOnlySpan<byte> body, IPEndPoint from, long nowMs)
    {
        var link = FindLink(from);

        if (link is null || body.Length < 4)
        {
            return;
        }

        link.LastHeardMs = nowMs;
        var text = body[4..];

        if (!LanProtocol.TryReadString(ref text, MaxChatBytes, out var message) || message.Length == 0)
        {
            return;
        }

        // The sender's own peer id is taken from the link, never from the packet: a peer
        // that put someone else's id in the body would otherwise put words in their mouth.
        if (!ChatAllowed
            || IsPeerMuted(link.PeerId)
            || (_restrictions.TryGetValue(link.PeerId, out var restriction) && !restriction.AllowText))
        {
            return;
        }

        ChatTextReceived?.Invoke(link.PeerId, message, ChatTextKind.Typed);
    }

    private void Touch(IPEndPoint from, long nowMs)
    {
        var link = FindLink(from);

        if (link is not null)
        {
            link.LastHeardMs = nowMs;
        }
    }

    // --- Link servicing -----------------------------------------------------

    private void ServiceLinks(long nowMs)
    {
        List<int>? dropped = null;

        foreach (var link in _links.Values)
        {
            link.ExpireReassemblies(nowMs);

            if (nowMs - link.LastHeartbeatMs >= HeartbeatIntervalMs)
            {
                link.LastHeartbeatMs = nowMs;
                SendControl(link.EndPoint, LanPacketKind.Heartbeat);
            }

            var alive = link.Retransmit(nowMs, Transmit);

            if (!alive || nowMs - link.LastHeardMs > LinkTimeoutMs)
            {
                (dropped ??= []).Add(link.PeerId);
            }
        }

        if (dropped is null)
        {
            return;
        }

        foreach (var peerId in dropped)
        {
            if (IsHost)
            {
                DropPeer(peerId);
            }
            else
            {
                LoseNetwork(PlatformResult.Fail(
                    PlatformStatus.NetworkFailure,
                    "The connection to the host was lost.",
                    $"No traffic from the host for {LinkTimeoutMs} ms."));
            }
        }
    }

    private void DropPeer(int peerId)
    {
        if (!_links.Remove(peerId, out var link))
        {
            return;
        }

        link.Closed = true;
        _mutedPeers.Remove(peerId);
        _restrictions.Remove(peerId);
        PeerLeft?.Invoke(peerId);
    }

    private void LoseNetwork(PlatformResult reason)
    {
        if (!HasNetwork)
        {
            return;
        }

        // The peers really are gone, so say so before saying the network is. A host drops
        // a departing client through DropPeer and raises PeerLeft for it; a client losing
        // the host reached only this path, so PeerLeft fired on one end of the very same
        // departure and not the other - the same asymmetry PeerJoined had, and the reason
        // the event could not be trusted to mean "a peer left" on both ends.
        //
        // Raised before Teardown, because Teardown clears the links and a handler asking
        // who is left should see the roster the event describes. LeaveAsync deliberately
        // does not come through here: the local player leaving is not a peer departing.
        foreach (var peerId in _links.Keys.ToArray())
        {
            DropPeer(peerId);
        }

        Teardown();
        NetworkDestroyed?.Invoke(reason);
    }

    private int NextPeerId()
    {
        var peerId = IPartyService.HostPeerId + 1;

        while (_links.ContainsKey(peerId))
        {
            peerId++;
        }

        return peerId;
    }

    /// <summary>
    /// Whether <paramref name="token"/> is the session token of the link at
    /// <paramref name="from"/>. Both halves must match: the right token from the wrong
    /// address is as wrong as the right address with no token.
    /// </summary>
    private bool IsLinkToken(IPEndPoint from, ulong token)
    {
        var link = FindLink(from);
        return link is not null && link.SessionToken != 0 && link.SessionToken == token;
    }

    private LanLink? FindLink(IPEndPoint from)
    {
        foreach (var link in _links.Values)
        {
            if (link.EndPoint.Equals(from))
            {
                return link;
            }
        }

        return null;
    }

    // --- Sockets ------------------------------------------------------------

    private void OpenGameSocket(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            EnableBroadcast = true,
        };

        // Without this a single ICMP "port unreachable" - which arrives routinely while a
        // host is still starting up - makes the *next* unrelated ReceiveFrom throw on
        // Windows, killing the receive thread for a peer that was never connected.
        DisableConnectionReset(socket);
        socket.Bind(new IPEndPoint(IPAddress.Any, port));

        _gameSocket = socket;
        _running = true;
        _gameReceiver = StartReceiver(socket, "NetRumble LAN receive", discovery: false);
    }

    private bool TryOpenDiscoverySocket()
    {
        try
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            {
                EnableBroadcast = true,
                ExclusiveAddressUse = false,
            };

            // Shared so two instances of the game on one machine can both host and both
            // still see a broadcast query, which is exactly the two-window case a developer
            // uses to test a match.
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            DisableConnectionReset(socket);
            socket.Bind(new IPEndPoint(IPAddress.Any, _options.DiscoveryPort));

            _discoverySocket = socket;
            _discoveryReceiver = StartReceiver(socket, "NetRumble LAN discovery", discovery: true);
            return true;
        }
        catch (SocketException)
        {
            _discoverySocket = null;
            return false;
        }
    }

    private Thread StartReceiver(Socket socket, string name, bool discovery)
    {
        var thread = new Thread(() => ReceiveLoop(socket, discovery))
        {
            IsBackground = true,
            Name = name,
        };

        thread.Start();
        return thread;
    }

    /// <summary>
    /// Blocks on the socket and does nothing but enqueue. Deliberately holds no reference
    /// to any game state, which is what keeps the whole class single-threaded from
    /// <see cref="Pump"/>'s point of view.
    /// </summary>
    private void ReceiveLoop(Socket socket, bool discovery)
    {
        var remote = new IPEndPoint(IPAddress.Any, 0);

        while (_running)
        {
            try
            {
                var buffer = new byte[LanProtocol.MaxDatagram];
                EndPoint from = remote;
                var received = socket.ReceiveFrom(buffer, ref from);

                // Dropped rather than queued past the cap. The network thread fills this
                // at line rate while Pump drains it once a frame, so an unbounded queue
                // lets any sender on the network grow this process's memory as fast as it
                // can send. Dropping is the right failure for a transport that is already
                // lossy by design - the reliable channel retransmits what mattered.
                if (received > 0 && from is IPEndPoint sender && _inbox.Count < MaxInboxDepth)
                {
                    _inbox.Enqueue(new Inbound(new IPEndPoint(sender.Address, sender.Port), buffer, received, discovery));
                }
            }
            catch (SocketException)
            {
                // A closed socket during teardown, or a transient per-datagram error. The
                // loop exits on _running rather than on the exception so a single bad
                // packet cannot take the transport down.
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Sends a bodyless link-scoped control datagram - Disconnect or Heartbeat - stamped
    /// with the link's session token so the far end will accept it.
    /// </summary>
    private void SendControl(IPEndPoint target, LanPacketKind kind)
    {
        var link = FindLink(target);

        if (link is null)
        {
            return;
        }

        var datagram = new byte[LanProtocol.LinkPrefixSize];
        LanProtocol.WriteLinkPrefix(datagram, kind, link.SessionToken);
        Transmit(target, datagram, datagram.Length);
    }

    private void SendAck(IPEndPoint target, uint sequence, int fragmentIndex)
    {
        var link = FindLink(target);

        if (link is null)
        {
            return;
        }

        var datagram = new byte[LanProtocol.LinkPrefixSize + 6];
        var span = datagram.AsSpan();
        var offset = LanProtocol.WriteLinkPrefix(span, LanPacketKind.Ack, link.SessionToken);
        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(offset + 4)..], (ushort)fragmentIndex);
        Transmit(target, datagram, offset + 6);
    }

    private void SendConnectAccept(IPEndPoint target, uint token, int peerId, ulong sessionToken)
    {
        var datagram = new byte[LanProtocol.PrefixSize + 16];
        var span = datagram.AsSpan();
        var offset = LanProtocol.WritePrefix(span, LanPacketKind.ConnectAccept);
        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], token);
        BinaryPrimitives.WriteInt32LittleEndian(span[(offset + 4)..], peerId);

        // The one time the session token crosses the wire in the clear, to the endpoint
        // that sent the Connect this is answering.
        BinaryPrimitives.WriteUInt64LittleEndian(span[(offset + 8)..], sessionToken);
        Transmit(target, datagram, offset + 16);
    }

    private void SendConnectReject(IPEndPoint target, uint token, string reason)
    {
        var datagram = new byte[LanProtocol.PrefixSize + 4 + 2 + 256];
        var span = datagram.AsSpan();
        var offset = LanProtocol.WritePrefix(span, LanPacketKind.ConnectReject);
        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], token);
        offset += 4;
        offset += LanProtocol.WriteString(span[offset..], reason);
        Transmit(target, datagram, offset);
    }

    private void Transmit(IPEndPoint target, byte[] datagram, int length)
        => TransmitFrom(_gameSocket, target, datagram, length);

    private static void TransmitFrom(Socket? socket, IPEndPoint target, byte[] datagram, int length)
    {
        if (socket is null)
        {
            return;
        }

        try
        {
            socket.SendTo(datagram, 0, length, SocketFlags.None, target);
        }
        catch (SocketException)
        {
            // Unreachable host, or a full send buffer. Both are the transport's own problem
            // to recover from: reliable traffic retransmits, and the rest is droppable by
            // definition. Throwing here would take out a caller that is mid-frame.
        }
        catch (ObjectDisposedException)
        {
            // Raced with teardown.
        }
    }

    private void Teardown()
    {
        _pendingConnect?.Completion.TrySetResult(PlatformResult.Canceled());
        _pendingConnect = null;

        _running = false;
        HasNetwork = false;
        IsHost = false;
        LocalPeerId = 0;
        ConnectionString = string.Empty;
        JoinCode = string.Empty;
        _maxPlayers = 2;
        _gameMode = string.Empty;
        _protocolVersion = string.Empty;
        _connectToken = 0;

        _links.Clear();
        _mutedPeers.Clear();
        _restrictions.Clear();
        _delivered.Clear();

        CloseSocket(ref _gameSocket);
        CloseSocket(ref _discoverySocket);

        JoinReceiver(ref _gameReceiver);
        JoinReceiver(ref _discoveryReceiver);

        while (_inbox.TryDequeue(out _))
        {
        }
    }

    private static void CloseSocket(ref Socket? socket)
    {
        if (socket is null)
        {
            return;
        }

        try
        {
            socket.Close();
        }
        catch (SocketException)
        {
        }

        socket = null;
    }

    private static void JoinReceiver(ref Thread? thread)
    {
        // Bounded: the receive thread is blocked in ReceiveFrom and wakes when the socket
        // closes, but a wait with no timeout here would hang shutdown if it did not.
        thread?.Join(TimeSpan.FromMilliseconds(500));
        thread = null;
    }

    private static void DisableConnectionReset(Socket socket)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const int SioUdpConnectionReset = -1744830452; // _WSAIOW(IOC_VENDOR, 12)

        try
        {
            socket.IOControl(SioUdpConnectionReset, [0, 0, 0, 0], null);
        }
        catch (SocketException)
        {
        }
    }

    /// <summary>Addresses a discovery query is sent to.</summary>
    /// <remarks>
    /// The limited broadcast address covers a normal LAN. Loopback is included as well so
    /// two instances on one machine find each other even when the adapter does not loop a
    /// broadcast back, which is the usual behaviour on Windows.
    /// </remarks>
    private static IEnumerable<IPAddress> BroadcastTargets()
        => [IPAddress.Broadcast, IPAddress.Loopback];

    private static string LocalAddress()
    {
        try
        {
            // No traffic is sent: connecting a UDP socket only asks the routing table which
            // local address would be used to reach the outside world, which is the one a
            // peer on the LAN can reach back on.
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(new IPEndPoint(IPAddress.Parse("203.0.113.1"), 9));
            return (probe.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? "127.0.0.1";
        }
        catch (SocketException)
        {
            return "127.0.0.1";
        }
    }

    /// <summary>Parses <c>lan://host:port</c>, the shape <see cref="ConnectionString"/> produces.</summary>
    internal static bool TryParseConnectionString(string? connectionString, out IPEndPoint endPoint)
    {
        endPoint = new IPEndPoint(IPAddress.Loopback, 0);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        var text = connectionString.Trim();

        if (text.StartsWith("lan://", StringComparison.OrdinalIgnoreCase))
        {
            text = text["lan://".Length..];
        }

        var separator = text.LastIndexOf(':');

        if (separator <= 0 || separator == text.Length - 1)
        {
            return false;
        }

        if (!int.TryParse(text[(separator + 1)..], out var port) || port is <= 0 or > 65535)
        {
            return false;
        }

        var host = text[..separator];

        if (!IPAddress.TryParse(host, out var address))
        {
            try
            {
                address = Array.Find(Dns.GetHostAddresses(host), a => a.AddressFamily == AddressFamily.InterNetwork)
                    ?? IPAddress.None;
            }
            catch (SocketException)
            {
                return false;
            }

            if (address.Equals(IPAddress.None))
            {
                return false;
            }
        }

        endPoint = new IPEndPoint(address, port);
        return true;
    }

    /// <summary>
    /// Caps a string by character count so the byte length it encodes to stays inside the
    /// datagram it is written into. Conservative on purpose: a UTF-8 character is at most
    /// four bytes, so the callers size their buffers at four times the limit.
    /// </summary>
    private static string Truncate(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length > maxChars ? value[..maxChars] : value;
    }

    private readonly record struct Inbound(IPEndPoint From, byte[] Data, int Length, bool Discovery);

    private readonly record struct ChatRestriction(bool AllowVoice, bool AllowText);

    private sealed class PendingConnect(
        string joinCode,
        IPEndPoint? target,
        long deadlineMs,
        CancellationToken cancellationToken)
    {
        public string JoinCode { get; } = joinCode;

        public IPEndPoint? Target { get; set; } = target;

        public long DeadlineMs { get; } = deadlineMs;

        public long LastAttemptMs { get; set; }

        public CancellationToken CancellationToken { get; } = cancellationToken;

        public TaskCompletionSource<PlatformResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
