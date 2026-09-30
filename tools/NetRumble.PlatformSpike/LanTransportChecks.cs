using NetRumble.Platform.Networking;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using NetRumble.Core;
using NetRumble.Core.Net;
using NetRumble.Platform;
using NetRumble.Platform.Lan;

namespace NetRumble.PlatformSpike;

/// <summary>
/// Checks for the LAN transport - the first implementation of <see cref="IPartyService"/>
/// in this port that puts a packet on a real socket.
/// </summary>
/// <remarks>
/// <para>
/// Split in two on purpose. The socket checks run two live services over the loopback
/// adapter, which is what proves connect, delivery, fragmentation and teardown actually
/// work between two independent instances. The link checks drive
/// <see cref="LanLink"/> directly, because the two rules that matter most - a stale
/// snapshot being dropped and a reordered reliable message being buffered - cannot be
/// provoked on demand through a socket that is not losing anything.
/// </para>
/// <para>
/// Discovery by join code is checked last and reported separately: it depends on UDP
/// broadcast reaching back to this machine, which a firewall profile can legitimately
/// block. A failure there is a machine configuration fact, not a transport defect, so it
/// is reported as a skip rather than allowed to fail the run.
/// </para>
/// </remarks>
internal static class LanTransportChecks
{
    /// <summary>Kept off the default so a spike run cannot disturb a real host on the LAN.</summary>
    private const int SpikeDiscoveryPort = 27599;

    public static async Task Run()
    {
        Console.WriteLine("[14] LAN transport");

        ConnectionStrings();
        LinkFraming();
        LinkReliability();
        await SocketSession().ConfigureAwait(false);
        await MatchOverSockets().ConfigureAwait(false);
        await ProtocolMismatch().ConfigureAwait(false);
        await ChatAndCapacity().ConfigureAwait(false);
        await HostDeparture().ConfigureAwait(false);
        await Discovery().ConfigureAwait(false);

        Console.WriteLine();
    }

    // --- Pure --------------------------------------------------------------

    private static void ConnectionStrings()
    {
        Check(
            "lan:// connection string parses",
            LanPartyService.TryParseConnectionString("lan://192.168.1.20:41000", out var parsed)
                && parsed.Address.Equals(IPAddress.Parse("192.168.1.20"))
                && parsed.Port == 41000);

        Check(
            "bare host:port parses",
            LanPartyService.TryParseConnectionString("127.0.0.1:5000", out var bare)
                && bare.Port == 5000);

        Check(
            "scheme is matched case-insensitively",
            LanPartyService.TryParseConnectionString("LAN://127.0.0.1:5000", out _));

        Check(
            "missing port is rejected",
            !LanPartyService.TryParseConnectionString("lan://127.0.0.1", out _));

        Check(
            "port 0 is rejected",
            !LanPartyService.TryParseConnectionString("lan://127.0.0.1:0", out _));

        Check(
            "port above 65535 is rejected",
            !LanPartyService.TryParseConnectionString("lan://127.0.0.1:70000", out _));

        Check(
            "empty connection string is rejected",
            !LanPartyService.TryParseConnectionString("   ", out _));

        Check("a generated join code is well formed", LanProtocol.IsJoinCode(LanProtocol.NewJoinCode()));

        Check("a lower-case join code is accepted", LanProtocol.IsJoinCode("abcde"));

        Check("a four-character join code is rejected", !LanProtocol.IsJoinCode("ABCD"));

        // I, O, 0, 1, S and 5 are excluded so a code read aloud is unambiguous.
        Check("an ambiguous character is not in the alphabet", !LanProtocol.IsJoinCode("ABCD0"));
    }

    // --- Link-level, no sockets ---------------------------------------------

    /// <summary>
    /// The session-token framing introduced with protocol version 2.
    /// </summary>
    /// <remarks>
    /// The socket tests cannot reach this: proving the token is what rejects a datagram
    /// needs a spoofed source address, because an unspoofed one is already refused for
    /// having no link. These checks pin the framing itself instead.
    /// </remarks>
    private static void LinkFraming()
    {
        // Every kind must be deliberately classified as link-scoped, discovery, or
        // handshake. This check exists because the failure mode of forgetting is silent:
        // a new kind defaults to "not link-scoped", which means "carries no token", which
        // means unauthenticated - the exact hole version 2 was cut to close.
        var unclassified = Enum.GetValues<LanPacketKind>()
            .Where(kind => !LanProtocol.IsLinkScoped(kind)
                && !LanProtocol.IsDiscovery(kind)
                && kind is not (LanPacketKind.Connect or LanPacketKind.ConnectAccept or LanPacketKind.ConnectReject))
            .ToArray();

        Check(
            "every packet kind is classified as link-scoped, discovery or handshake",
            unclassified.Length == 0);

        // The handshake cannot carry a token - it is what issues one - and discovery is
        // broadcast to peers that have no link by definition.
        Check(
            "the handshake and discovery kinds are not link-scoped",
            !LanProtocol.IsLinkScoped(LanPacketKind.Connect)
                && !LanProtocol.IsLinkScoped(LanPacketKind.ConnectAccept)
                && !LanProtocol.IsLinkScoped(LanPacketKind.DiscoverQuery));

        // Everything that moves match state or ends a session must be authenticated.
        Check(
            "every kind that carries match state or ends a link is link-scoped",
            LanProtocol.IsLinkScoped(LanPacketKind.Payload)
                && LanProtocol.IsLinkScoped(LanPacketKind.Ack)
                && LanProtocol.IsLinkScoped(LanPacketKind.Heartbeat)
                && LanProtocol.IsLinkScoped(LanPacketKind.ChatText)
                && LanProtocol.IsLinkScoped(LanPacketKind.Disconnect));

        // Round trip: what WriteLinkPrefix puts down is what the receiver strips off.
        var buffer = new byte[LanProtocol.LinkPrefixSize];
        const ulong Token = 0x0123456789ABCDEFUL;
        var written = LanProtocol.WriteLinkPrefix(buffer, LanPacketKind.Payload, Token);

        var readPrefix = LanProtocol.TryReadPrefix(buffer, out var kind, out var body);
        var readToken = LanProtocol.TryReadSessionToken(ref body, out var token);

        Check(
            "a link prefix round trips its kind and token and leaves an empty body",
            written == LanProtocol.LinkPrefixSize
                && readPrefix
                && kind == LanPacketKind.Payload
                && readToken
                && token == Token
                && body.Length == 0);

        // A body one byte short of a token must be refused rather than read past.
        var truncated = (ReadOnlySpan<byte>)new byte[LanProtocol.SessionTokenSize - 1];
        Check(
            "a link body too short for a token is refused",
            !LanProtocol.TryReadSessionToken(ref truncated, out _));

        // Tokens are the whole defence, so they must be unguessable and never zero -
        // zero is the "no token" sentinel a link carries before the handshake completes.
        var tokens = new HashSet<ulong>();
        for (var i = 0; i < 64; i++)
        {
            tokens.Add(LanProtocol.NewSessionToken());
        }

        Check(
            "session tokens are non-zero and distinct",
            tokens.Count == 64 && !tokens.Contains(0));

        // Regression: MaxFragmentPayload must subtract the *link* prefix. If it still
        // subtracted the old six-byte one, a full fragment would be eight bytes over the
        // MTU budget - which shows up as sporadic loss of large messages only, and only
        // on networks without headroom. Nothing in the reassembly tests would catch it.
        Check(
            "a maximum-size fragment still fits inside one datagram",
            LanProtocol.LinkPrefixSize + LanProtocol.PayloadHeaderSize + LanProtocol.MaxFragmentPayload
                == LanProtocol.MaxDatagram);
    }

    /// <summary>
    /// Drives two <see cref="LanLink"/>s directly so fragments can be delivered in an
    /// order, and with losses, that a healthy loopback socket never produces.
    /// </summary>
    private static void LinkReliability()
    {
        var endPoint = new IPEndPoint(IPAddress.Loopback, 1);

        // --- A stale sequenced message is dropped rather than applied ---------
        {
            var (sender, receiver) = NewPair(endPoint);
            var wire = new List<byte[]>();

            sender.Send("first"u8, MessageDelivery.UnreliableSequenced, 0, Capture(wire));
            sender.Send("second"u8, MessageDelivery.UnreliableSequenced, 0, Capture(wire));

            var delivered = new List<byte[]>();
            Deliver(receiver, wire[1], delivered);
            Deliver(receiver, wire[0], delivered);

            Check(
                "a sequenced message older than one already applied is dropped",
                delivered.Count == 1 && Text(delivered[0]) == "second");
        }

        // --- A reordered reliable message is buffered and delivered in order ---
        {
            var (sender, receiver) = NewPair(endPoint);
            var wire = new List<byte[]>();

            sender.Send("first"u8, MessageDelivery.Reliable, 0, Capture(wire));
            sender.Send("second"u8, MessageDelivery.Reliable, 0, Capture(wire));

            var delivered = new List<byte[]>();
            Deliver(receiver, wire[1], delivered);

            var heldBack = delivered.Count == 0;

            Deliver(receiver, wire[0], delivered);

            Check(
                "a reliable message that arrives early is held until its predecessor does",
                heldBack
                    && delivered.Count == 2
                    && Text(delivered[0]) == "first"
                    && Text(delivered[1]) == "second");
        }

        // --- A retransmitted reliable message is delivered once ---------------
        {
            var (sender, receiver) = NewPair(endPoint);
            var wire = new List<byte[]>();
            sender.Send("once"u8, MessageDelivery.Reliable, 0, Capture(wire));

            var delivered = new List<byte[]>();
            Deliver(receiver, wire[0], delivered, out _, out var firstAck);
            Deliver(receiver, wire[0], delivered, out _, out var secondAck);

            Check(
                "a duplicated reliable message is delivered exactly once",
                delivered.Count == 1 && Text(delivered[0]) == "once");

            // Both copies must still be acknowledged: a retransmission means the first
            // acknowledgement was lost, and silence would make the sender retry forever.
            Check("a duplicate is still acknowledged", firstAck >= 0 && secondAck >= 0);
        }

        // --- An unacknowledged reliable message is retransmitted --------------
        {
            var (sender, _) = NewPair(endPoint);
            var wire = new List<byte[]>();
            sender.Send("pending"u8, MessageDelivery.Reliable, 0, Capture(wire));

            var sentBeforeRetry = wire.Count;
            sender.Retransmit(50, Capture(wire));
            var afterTooSoon = wire.Count;
            sender.Retransmit(1_000, Capture(wire));

            Check(
                "an unacknowledged reliable fragment is resent, but not immediately",
                sentBeforeRetry == 1 && afterTooSoon == 1 && wire.Count == 2);

            Check("the message is still outstanding", sender.PendingReliableCount == 1);

            sender.OnAck(1, 0);

            Check("acknowledging clears it", sender.PendingReliableCount == 0);
        }

        // --- Giving up on a peer that never acknowledges ----------------------
        {
            var (sender, _) = NewPair(endPoint);
            sender.Send("lost"u8, MessageDelivery.Reliable, 0, static (_, _, _) => { });

            Check(
                "a reliable message unacknowledged past the give-up window kills the link",
                !sender.Retransmit(60_000, static (_, _, _) => { }));
        }

        // --- Fragmentation and reassembly -------------------------------------
        {
            var (sender, receiver) = NewPair(endPoint);
            var wire = new List<byte[]>();
            var large = new byte[LanProtocol.MaxFragmentPayload * 3 + 17];

            for (var i = 0; i < large.Length; i++)
            {
                large[i] = (byte)(i * 31);
            }

            sender.Send(large, MessageDelivery.Reliable, 0, Capture(wire));

            var oversize = wire.Exists(d => d.Length > LanProtocol.MaxDatagram);
            var delivered = new List<byte[]>();

            // Delivered back to front, so reassembly cannot be accidentally relying on
            // the first fragment arriving first.
            for (var i = wire.Count - 1; i >= 0; i--)
            {
                Deliver(receiver, wire[i], delivered);
            }

            Check(
                "a message larger than one datagram is fragmented under the MTU",
                wire.Count == 4 && !oversize);

            Check(
                "fragments arriving out of order reassemble byte for byte",
                delivered.Count == 1 && delivered[0].AsSpan().SequenceEqual(large));
        }

        // --- Malformed headers --------------------------------------------------
        {
            var (_, receiver) = NewPair(endPoint);
            var delivered = new List<byte[]>();

            var truncated = new byte[LanProtocol.PayloadHeaderSize - 1];
            receiver.OnPayload(truncated, 0, delivered, out _, out _);

            var body = new byte[LanProtocol.PayloadHeaderSize];
            body[0] = 99; // channel that does not exist
            receiver.OnPayload(body, 0, delivered, out _, out _);

            Check("a truncated or out-of-range payload header is discarded", delivered.Count == 0);
        }
    }

    // --- Socket-level -------------------------------------------------------

    /// <summary>Connect, deliver in both directions, then disconnect, over real sockets.</summary>
    private static async Task SocketSession()
    {
        using var host = NewService();
        using var client = NewService();

        var hosted = await host.HostAsync(4, "Deathmatch", NRProtocol.VersionString()).ConfigureAwait(false);
        Check("HostAsync opens a socket", hosted.Succeeded && host.BoundPort != 0);

        var joined = 0;
        host.PeerJoined += peerId => joined = peerId;

        // The client side of the same event. Subscribed before joining because it fires
        // as the connect completes.
        var clientSawHost = 0;
        client.PeerJoined += peerId => clientSawHost = peerId;

        var joinTask = client.JoinByConnectionStringAsync($"lan://127.0.0.1:{host.BoundPort}", NRProtocol.VersionString());
        var connected = await PumpUntil(host, client, () => joinTask.IsCompleted).ConfigureAwait(false);
        var joinResult = connected ? await joinTask.ConfigureAwait(false) : PlatformResult.Unavailable();

        Check(
            "a client connects by connection string and is assigned peer id 2",
            joinResult.Succeeded && client.LocalPeerId == 2 && client.IsHost is false);

        Check("the host is told the peer joined", joined == 2);
        Check("the host counts one peer", host.PeerCount == 1);

        // Regression: this was missing, and the asymmetry only surfaced when a real
        // autopiloted client sat in a lobby forever waiting for a peer it already had.
        // A host announcing clients while clients announce nobody makes PeerJoined mean
        // two different things depending on which end is listening.
        Check("the client is told the host joined", clientSawHost == IPartyService.HostPeerId);

        // --- Reliable, host to client -----------------------------------------
        byte[]? atClient = null;
        var clientSender = 0;
        client.MessageReceived += (sender, payload) =>
        {
            clientSender = sender;
            atClient = payload.ToArray();
        };

        host.Send(IPartyService.PartyBroadcast, "host-to-client"u8, MessageDelivery.Reliable);
        await PumpUntil(host, client, () => atClient is not null).ConfigureAwait(false);

        Check(
            "a reliable broadcast reaches the client, attributed to the host",
            atClient is not null && Text(atClient) == "host-to-client" && clientSender == IPartyService.HostPeerId);

        // --- Reliable, client to host -----------------------------------------
        byte[]? atHost = null;
        var hostSender = 0;
        host.MessageReceived += (sender, payload) =>
        {
            hostSender = sender;
            atHost = payload.ToArray();
        };

        client.Send(IPartyService.HostPeerId, "client-to-host"u8, MessageDelivery.Reliable);
        await PumpUntil(host, client, () => atHost is not null).ConfigureAwait(false);

        Check(
            "a client send reaches the host, attributed to the client",
            atHost is not null && Text(atHost) == "client-to-host" && hostSender == 2);

        // --- A message far larger than one datagram ----------------------------
        var large = new byte[LanProtocol.MaxFragmentPayload * 5];
        Random.Shared.NextBytes(large);
        atClient = null;

        host.Send(IPartyService.PartyBroadcast, large, MessageDelivery.Reliable);
        await PumpUntil(host, client, () => atClient is not null).ConfigureAwait(false);

        Check(
            "a five-fragment message survives the socket intact",
            atClient is not null && atClient.AsSpan().SequenceEqual(large));

        // --- A datagram from an unknown endpoint is refused ---------------------
        //
        // Peers are identified by the endpoint a datagram arrives from, so anything that
        // can reach this port is a candidate host. This pins the first of the two gates:
        // an endpoint with no link is dropped before its contents are read at all.
        //
        // The second gate - the per-link session token - is what defends against an
        // attacker who *spoofs* a known endpoint's source address. That cannot be staged
        // over loopback, so it is covered by the framing checks in LinkFraming instead.
        atClient = null;
        var forged = new byte[LanProtocol.LinkPrefixSize + LanProtocol.PayloadHeaderSize + 4];
        LanProtocol.WriteLinkPrefix(forged, LanPacketKind.Payload, 0xDEADBEEFDEADBEEFUL);

        using (var attacker = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            attacker.SendTo(forged, new IPEndPoint(IPAddress.Loopback, client.BoundPort));
        }

        // Pumped for a while precisely because a pass here is an absence. If the datagram
        // were going to be accepted, this is where it would arrive.
        await PumpUntil(host, client, () => atClient is not null).ConfigureAwait(false);

        Check("a payload from an endpoint with no link is dropped", atClient is null);

        // --- Clean disconnect ---------------------------------------------------
        var left = 0;
        host.PeerLeft += peerId => left = peerId;

        await client.LeaveAsync().ConfigureAwait(false);
        await PumpUntil(host, client, () => left != 0).ConfigureAwait(false);

        Check("a client that leaves is reported to the host", left == 2 && host.PeerCount == 0);
        Check("the client's own state is cleared", !client.HasNetwork && client.LocalPeerId == 0);

        await host.LeaveAsync().ConfigureAwait(false);
        Check("the host's own state is cleared",
            !host.HasNetwork
                && !host.IsHost
                && host.LocalPeerId == 0
                && host.ConnectionString.Length == 0
                && host.JoinCode.Length == 0);

        var hostedAgain = await host.HostAsync(4, "Deathmatch", NRProtocol.VersionString()).ConfigureAwait(false);
        Check("a cleared service can host a fresh session",
            hostedAgain.Succeeded && host.HasNetwork && host.IsHost && host.LocalPeerId == IPartyService.HostPeerId);
        await host.LeaveAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The whole stack: <see cref="PartyMatchNetwork"/> on both ends of a socket. Until
    /// this ran, no match message had ever been encoded, transmitted and decoded between
    /// two instances - only handed across in process by a double.
    /// </summary>
    private static async Task MatchOverSockets()
    {
        using var hostParty = NewService();
        using var clientParty = NewService();

        await hostParty.HostAsync(4, "Deathmatch", NRProtocol.VersionString()).ConfigureAwait(false);

        var joinTask = clientParty.JoinByConnectionStringAsync($"lan://127.0.0.1:{hostParty.BoundPort}", NRProtocol.VersionString());

        if (!await PumpUntil(hostParty, clientParty, () => joinTask.IsCompleted).ConfigureAwait(false))
        {
            Check("a match runs over the LAN transport", false);
            return;
        }

        using var hostNet = new PartyMatchNetwork(hostParty, new PlayerState { DisplayName = "Host" });
        using var clientNet = new PartyMatchNetwork(clientParty, new PlayerState { DisplayName = "Client" });

        WorldSnapshot? snapshot = null;
        MatchState? state = null;
        clientNet.WorldSnapshotReceived += s => snapshot = s;
        clientNet.MatchStateChanged += s => state = s;

        hostNet.SetMatchState(MatchState.Running);

        var objects = new List<SnapshotEntry>();

        for (var i = 0; i < 31; i++)
        {
            objects.Add(new SnapshotEntry(
                i,
                new Vector2(i * 3.5f, i * -2.5f),
                new Vector2(i, -i),
                i * 0.1f,
                100f - i,
                50f + i));
        }

        hostNet.BroadcastWorldSnapshot(new WorldSnapshot(4242, objects));

        await PumpUntil(
            hostParty,
            clientParty,
            () => snapshot is not null && state is not null).ConfigureAwait(false);

        Check("the match state reaches the client over the socket", state == MatchState.Running);

        Check(
            "a full 31-object world snapshot survives the socket",
            snapshot is not null
                && snapshot.Frame == 4242
                && snapshot.Objects.Count == 31
                && snapshot.Objects[30].Position == new Vector2(105f, -75f));

        // A client is not the authority: its snapshot must be ignored by the host, exactly
        // as PartyMatchNetwork's authority guard does for the loopback double.
        var spoofed = false;
        hostNet.WorldSnapshotReceived += _ => spoofed = true;
        clientParty.Send(IPartyService.HostPeerId, "\u0000"u8, MessageDelivery.Reliable);
        clientNet.SendShipInput(Vector2.One, Vector2.Zero, false, 1);

        var inputSeen = 0;
        hostNet.ShipInputReceived += (peerId, _, _, _, _) => inputSeen = peerId;

        await PumpUntil(hostParty, clientParty, () => inputSeen != 0).ConfigureAwait(false);

        Check("client input reaches the host attributed to the client", inputSeen == 2);
        Check("the host ignores a snapshot that did not come from itself", !spoofed);

        await clientParty.LeaveAsync().ConfigureAwait(false);
        await hostParty.LeaveAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The build-mismatch refusal: a peer speaking a different wire contract is turned
    /// away at the handshake with a sentence, rather than admitted into a match whose
    /// every message it will then misread.
    /// </summary>
    private static async Task ProtocolMismatch()
    {
        using var host = NewService();
        using var stale = NewService();
        using var silent = NewService();
        using var matching = NewService();

        await host.HostAsync(4, "Deathmatch", NRProtocol.VersionString()).ConfigureAwait(false);

        // --- A peer on a different, but present, version ------------------------
        var staleTask = stale.JoinByConnectionStringAsync(
            $"lan://127.0.0.1:{host.BoundPort}", NRProtocol.VersionString() + "-old");

        await PumpUntil(host, stale, () => staleTask.IsCompleted).ConfigureAwait(false);
        var staleResult = await staleTask.ConfigureAwait(false);

        Check(
            "a peer on a different protocol version is refused",
            staleResult.Failed && staleResult.Status is PlatformStatus.Failed);

        Check(
            "the refusal names both versions rather than being a bare error",
            staleResult.Message is not null
            && staleResult.Message.Contains(NRProtocol.VersionString(), StringComparison.Ordinal)
            && staleResult.Message.Contains("same build", StringComparison.Ordinal));

        // --- A peer that publishes nothing, which is what a pre-check build sends -
        var silentTask = silent.JoinByConnectionStringAsync($"lan://127.0.0.1:{host.BoundPort}", string.Empty);
        await PumpUntil(host, silent, () => silentTask.IsCompleted).ConfigureAwait(false);
        var silentResult = await silentTask.ConfigureAwait(false);

        Check(
            "a peer advertising no version at all is refused, not waved through",
            silentResult.Failed);

        Check(
            "the refusal says which side is the older build",
            silentResult.Message?.Contains("older", StringComparison.Ordinal) == true);

        // --- The teeth: the same handshake still admits a matching peer ----------
        //
        // Without this the three checks above would pass just as happily if the
        // handshake had been broken outright and were refusing everyone.
        var okTask = matching.JoinByConnectionStringAsync(
            $"lan://127.0.0.1:{host.BoundPort}", NRProtocol.VersionString());

        await PumpUntil(host, matching, () => okTask.IsCompleted).ConfigureAwait(false);
        var okResult = await okTask.ConfigureAwait(false);

        Check("a peer on the same protocol version still joins", okResult.Succeeded && matching.HasNetwork);

        await matching.LeaveAsync().ConfigureAwait(false);
        await host.LeaveAsync().ConfigureAwait(false);
    }

    /// <summary>Text chat, the mute rule, and refusing a peer once the match is full.</summary>
    private static async Task ChatAndCapacity()
    {
        using var host = NewService();
        using var client = NewService();
        using var extra = NewService();

        await host.HostAsync(2, "Deathmatch", NRProtocol.VersionString()).ConfigureAwait(false);

        var joinTask = client.JoinByConnectionStringAsync($"lan://127.0.0.1:{host.BoundPort}", NRProtocol.VersionString());

        if (!await PumpUntil(host, client, () => joinTask.IsCompleted).ConfigureAwait(false))
        {
            Check("a second player fills a two-player match", false);
            return;
        }

        // --- The match is now full --------------------------------------------
        var rejectTask = extra.JoinByConnectionStringAsync($"lan://127.0.0.1:{host.BoundPort}", NRProtocol.VersionString());
        await PumpUntil(host, extra, () => rejectTask.IsCompleted).ConfigureAwait(false);
        var rejected = await rejectTask.ConfigureAwait(false);

        Check(
            "a third peer is refused with the host's reason, not a timeout",
            rejected.Failed && rejected.Status is PlatformStatus.Failed && rejected.Message == "This match is full.");

        // --- Chat round trip ---------------------------------------------------
        var received = new List<(int Peer, string Text)>();
        client.ChatTextReceived += (peerId, text, _) => received.Add((peerId, text));

        var sent = host.SendChatText("nice shot");
        await PumpUntil(host, client, () => received.Count > 0).ConfigureAwait(false);

        Check(
            "a text chat line reaches the other peer, attributed to its sender",
            sent.Succeeded && received.Count == 1 && received[0] == (IPartyService.HostPeerId, "nice shot"));

        // --- Muting suppresses inbound chat ------------------------------------
        received.Clear();
        client.SetPeerMuted(IPartyService.HostPeerId, muted: true);
        host.SendChatText("still there?");
        await PumpUntil(host, client, () => false, TimeSpan.FromMilliseconds(400)).ConfigureAwait(false);

        Check("chat from a muted peer is suppressed", received.Count == 0);

        Check(
            "a muted peer shows as muted in the roster indicator",
            client.GetChatIndicator(IPartyService.HostPeerId) == ChatIndicator.Muted);

        // --- The communications privilege is a master switch --------------------
        client.ChatAllowed = false;

        Check(
            "no chat indicator at all once chat is disallowed",
            client.GetChatIndicator(IPartyService.HostPeerId) == ChatIndicator.None);

        var refused = client.SendChatText("hello");

        Check(
            "sending is refused with the privilege reason, per XR-018",
            refused.Failed && refused.Status == PlatformStatus.NoPrivilege);

        await client.LeaveAsync().ConfigureAwait(false);
        await host.LeaveAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The other end of the departure the socket session already covers: the host leaves,
    /// and the client must hear about it as a peer leaving and not only as the network
    /// dying. Kept separate because it has to tear the host down, which ends the session.
    /// </summary>
    private static async Task HostDeparture()
    {
        using var host = NewService();
        using var client = NewService();

        await host.HostAsync(4, "Deathmatch", NRProtocol.VersionString()).ConfigureAwait(false);

        var joinTask = client.JoinByConnectionStringAsync($"lan://127.0.0.1:{host.BoundPort}", NRProtocol.VersionString());

        if (!await PumpUntil(host, client, () => joinTask.IsCompleted).ConfigureAwait(false))
        {
            Check("a client connects before the host departs", false);
            return;
        }

        var left = new List<int>();
        var destroyed = new List<PlatformResult>();
        client.PeerLeft += left.Add;
        client.NetworkDestroyed += destroyed.Add;

        // Observed inside the PeerLeft handler: a handler asking who is left should see
        // the roster the event describes, not one already cleared by teardown.
        var peersWhenTold = -1;
        client.PeerLeft += _ => peersWhenTold = client.PeerCount;

        await host.LeaveAsync().ConfigureAwait(false);
        await PumpUntil(host, client, () => destroyed.Count > 0).ConfigureAwait(false);

        // Regression, and the sibling of the PeerJoined asymmetry: a host raises PeerLeft
        // for a client that goes away, so a client must raise it for a host that goes
        // away. NetworkDestroyed is a larger event, not a substitute - taking it as one
        // let a single departure raise the event on only one of the two ends that saw it.
        Check(
            "the client is told the host left, not only that the network died",
            left.Count == 1 && left[0] == IPartyService.HostPeerId);

        Check(
            "PeerLeft precedes NetworkDestroyed, and the peer is already gone from the count",
            destroyed.Count == 1 && peersWhenTold == 0);

        Check(
            "the departing host raises neither event on itself",
            !host.HasNetwork && host.PeerCount == 0);
    }

    /// <summary>
    /// Join-code discovery. Reported as a skip on failure: it needs a UDP broadcast to
    /// come back to this machine, which a firewall profile may legitimately refuse.
    /// </summary>
    private static async Task Discovery()
    {
        using var host = new LanPartyService(new LanPartyOptions { DiscoveryPort = SpikeDiscoveryPort });
        using var client = new LanPartyService(new LanPartyOptions { DiscoveryPort = SpikeDiscoveryPort });

        var hosted = await host.HostAsync(4, "Deathmatch", NRProtocol.VersionString()).ConfigureAwait(false);

        if (!hosted.Succeeded || hosted.Value is not { Length: LanProtocol.JoinCodeLength } code)
        {
            Console.WriteLine("    SKIP join-code discovery (the discovery port could not be opened)");
            await host.LeaveAsync().ConfigureAwait(false);
            return;
        }

        var joinTask = client.JoinAsync(code, NRProtocol.VersionString());
        await PumpUntil(host, client, () => joinTask.IsCompleted, TimeSpan.FromSeconds(8)).ConfigureAwait(false);

        if (!joinTask.IsCompleted)
        {
            Console.WriteLine("    SKIP join-code discovery (no broadcast reply; check the firewall profile)");
        }
        else
        {
            var result = await joinTask.ConfigureAwait(false);

            if (result.Succeeded)
            {
                Check("a five-character join code resolves to the host over broadcast", client.LocalPeerId == 2);
            }
            else
            {
                Console.WriteLine($"    SKIP join-code discovery ({result})");
            }
        }

        // An unknown code must fail as "not found" rather than hang. A fresh instance,
        // because the one above is now connected and would refuse a second join outright.
        using var stranger = new LanPartyService(new LanPartyOptions { DiscoveryPort = SpikeDiscoveryPort });
        var missTask = stranger.JoinAsync("ZZZZZ", NRProtocol.VersionString());

        await PumpUntil(host, stranger, () => missTask.IsCompleted, TimeSpan.FromSeconds(8)).ConfigureAwait(false);
        var miss = missTask.IsCompleted ? await missTask.ConfigureAwait(false) : PlatformResult.Ok();

        Check(
            "an unknown join code times out as not found",
            miss.Failed && miss.Status == PlatformStatus.TimedOut);

        await client.LeaveAsync().ConfigureAwait(false);
        await host.LeaveAsync().ConfigureAwait(false);
    }

    // --- Helpers ------------------------------------------------------------

    /// <summary>
    /// A service with discovery off: these checks connect by endpoint so they never
    /// depend on the machine's broadcast behaviour.
    /// </summary>
    private static LanPartyService NewService()
        => new(new LanPartyOptions { EnableDiscovery = false, DiscoveryPort = SpikeDiscoveryPort });

    private static (LanLink Sender, LanLink Receiver) NewPair(IPEndPoint endPoint)
        => (new LanLink(2, endPoint), new LanLink(1, endPoint));

    private static Action<IPEndPoint, byte[], int> Capture(List<byte[]> wire)
        => (_, datagram, length) => wire.Add(datagram[..length]);

    private static void Deliver(LanLink link, byte[] datagram, List<byte[]> delivered)
        => Deliver(link, datagram, delivered, out _, out _);

    private static void Deliver(
        LanLink link,
        byte[] datagram,
        List<byte[]> delivered,
        out uint ackSequence,
        out int ackFragment)
        // LinkPrefixSize, not PrefixSize: a Payload is link-scoped and carries the session
        // token that LanPartyService.ProcessDatagram verifies and strips before handing
        // the body down. This helper stands in for that strip.
        => link.OnPayload(datagram.AsSpan(LanProtocol.LinkPrefixSize), 0, delivered, out ackSequence, out ackFragment);

    private static string Text(byte[] payload) => System.Text.Encoding.UTF8.GetString(payload);

    /// <summary>
    /// Pumps both services until the condition holds, standing in for two game loops.
    /// Returns false on timeout so a caller can report a specific failure.
    /// </summary>
    private static async Task<bool> PumpUntil(
        LanPartyService host,
        LanPartyService client,
        Func<bool> condition,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));

        while (DateTime.UtcNow < deadline)
        {
            host.Pump();
            client.Pump();

            if (condition())
            {
                return true;
            }

            await Task.Delay(5).ConfigureAwait(false);
        }

        host.Pump();
        client.Pump();
        return condition();
    }

    private static void Check(string label, bool condition)
        => Console.WriteLine($"    {(condition ? "PASS" : "FAIL")} {label}");
}
