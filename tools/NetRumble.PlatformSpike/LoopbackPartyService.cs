using NetRumble.Platform;

/// <summary>
/// A two-peer, in-process <see cref="IPartyService"/>: a host and one client, with sends
/// delivered straight into the other side's <see cref="IPartyService.MessageReceived"/>.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="LoopbackHub"/>, which hands payload objects across by reference, this
/// double drives the <b>real byte path</b> - encode, transmit, decode - so the checks
/// built on it cover the wire format and the authority guard rather than just the
/// replication logic.
/// </para>
/// <para>
/// It also lets a test send raw bytes as an arbitrary peer, which is how the
/// authority-spoofing and malformed-packet checks are written. A real Party session cannot
/// be made to do that on demand.
/// </para>
/// </remarks>
internal sealed class LoopbackPartyService : IPartyService
{
    private LoopbackPartyService? _peer;

    private LoopbackPartyService(int localPeerId, bool isHost)
    {
        LocalPeerId = localPeerId;
        IsHost = isHost;
    }

    public const int ClientPeerId = 2;

    /// <summary>Creates a connected host/client pair.</summary>
    public static (LoopbackPartyService Host, LoopbackPartyService Client) CreatePair()
    {
        var host = new LoopbackPartyService(IPartyService.HostPeerId, isHost: true);
        var client = new LoopbackPartyService(ClientPeerId, isHost: false);
        host._peer = client;
        client._peer = host;
        return (host, client);
    }

    /// <summary>Bytes sent by this peer, for size and traffic-shape assertions.</summary>
    public List<byte[]> Sent { get; } = [];

    /// <summary>When true, sends are dropped instead of delivered.</summary>
    public bool DropSends { get; set; }

    public bool HasNetwork => _peer is not null;

    public bool IsHost { get; }

    public int LocalPeerId { get; }

    public string ConnectionString => "loopback";

    public string JoinCode => "LOOPB";

    public bool ChatAllowed { get; set; } = true;

    public bool IsSelfMuted { get; set; }

    public event Action<int>? PeerJoined;

    public event Action<int>? PeerLeft;

    public event Action<PlatformResult>? NetworkDestroyed;

    public event PartyMessageHandler? MessageReceived;

    public event Action? ChatChanged;

    public event Action<int, string, ChatTextKind>? ChatTextReceived;

    public void Send(int peerId, ReadOnlySpan<byte> payload, MessageDelivery delivery)
    {
        Sent.Add(payload.ToArray());

        if (DropSends || _peer is null)
        {
            return;
        }

        // Both the broadcast target and a direct send resolve to the single other peer.
        if (peerId is IPartyService.PartyBroadcast || peerId == _peer.LocalPeerId)
        {
            _peer.Deliver(LocalPeerId, payload);
        }
    }

    /// <summary>Test hook: delivers raw bytes to this peer as though they came from <paramref name="senderPeerId"/>.</summary>
    public void Deliver(int senderPeerId, ReadOnlySpan<byte> payload)
        => MessageReceived?.Invoke(senderPeerId, payload);

    /// <summary>Test hook: announces that a peer connected.</summary>
    public void RaisePeerJoined(int peerId) => PeerJoined?.Invoke(peerId);

    /// <summary>Test hook: announces that a peer dropped.</summary>
    public void RaisePeerLeft(int peerId) => PeerLeft?.Invoke(peerId);

    /// <summary>Test hook: simulates the transport losing the network outright.</summary>
    public void RaiseNetworkDestroyed(PlatformResult result) => NetworkDestroyed?.Invoke(result);

    // --- Unused by the transport checks -------------------------------------

    public Task<PlatformResult<string>> HostAsync(
        int maxPlayers,
        string gameMode,
        string protocolVersion,
        CancellationToken cancellationToken = default)
        => Task.FromResult(PlatformResult<string>.Ok(JoinCode));

    public Task<PlatformResult> JoinAsync(
        string joinCode,
        string protocolVersion,
        CancellationToken cancellationToken = default)
        => Task.FromResult(PlatformResult.Ok());

    public Task<PlatformResult> JoinByConnectionStringAsync(
        string connectionString,
        string protocolVersion,
        CancellationToken cancellationToken = default)
        => Task.FromResult(PlatformResult.Ok());

    public Task LeaveAsync()
    {
        _peer = null;
        NetworkDestroyed?.Invoke(PlatformResult.Ok());
        return Task.CompletedTask;
    }

    public void SetPeerRestrictions(int peerId, bool allowVoice, bool allowText) => ChatChanged?.Invoke();

    public void SetPeerMuted(int peerId, bool muted) => ChatChanged?.Invoke();

    public bool IsPeerMuted(int peerId) => false;

    public ChatIndicator GetChatIndicator(int peerId) => ChatIndicator.None;

    public void ClearChatRestrictions() => ChatChanged?.Invoke();

    public PlatformResult SendChatText(string message)
    {
        ChatTextReceived?.Invoke(LocalPeerId, message, ChatTextKind.Typed);
        return PlatformResult.Ok();
    }
}
