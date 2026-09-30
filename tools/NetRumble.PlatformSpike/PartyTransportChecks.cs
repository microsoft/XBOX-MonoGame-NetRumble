using System.Numerics;
using NetRumble.Core;
using NetRumble.Core.Net;
using NetRumble.Core.Net.Wire;
using NetRumble.Core.Objects;
using NetRumble.Platform;

/// <summary>
/// Checks for <see cref="PartyMatchNetwork"/> and the wire format, driven over
/// <see cref="LoopbackPartyService"/> so every assertion covers real encode/decode.
/// </summary>
/// <remarks>
/// The wire format is new to the port - Godot's <c>MultiplayerAPI</c> serialised its
/// <c>Dictionary</c> payloads itself - so none of it is covered by parity reading against
/// the GDScript. It has to be tested directly, particularly the parts that only matter
/// against a hostile or mismatched peer.
/// </remarks>
internal static class PartyTransportChecks
{
    public static void Run()
    {
        Console.WriteLine("[9] Party transport");

        RoundTrips();
        AuthorityGuard();
        InputAttribution();
        MalformedPackets();
        RosterSync();
        RosterSyncAfterTransportJoin();
        LobbySubmissions();
        InterfaceLevelEvents();
        MatchStartGate();
        HostedMatchEndsWhenClientsLeave();
        ClientEndsMatchWhenHostLeaves();
        ClientInputReachesHost();
        EndToEndMatch();

        Console.WriteLine();
    }

    /// <summary>Every payload must survive encode then decode unchanged.</summary>
    private static void RoundTrips()
    {
        var created = new MatchCreatedPayload(
            2400,
            2400,
            [new AsteroidSpawn(7, AsteroidSize.Medium, 2, new Vector2(1, 2), new Vector2(3, 4), 0.5f)],
            [new ShipSpawn(3, 2, 4, 5, new Vector2(6, 7), 1.25f)],
            [new PowerUpSpawn(9, PowerUpType.Rocket)]);

        var decodedCreated = RoundTrip(
            MatchMessageCodec.EncodeMatchCreated(created),
            (ref MessageReader r, out MatchCreatedPayload v) => MatchMessageCodec.TryDecodeMatchCreated(ref r, out v));

        Check(
            "MatchCreated round-trips",
            decodedCreated is not null
                && decodedCreated.Width == 2400
                && decodedCreated.Asteroids[0].Size == AsteroidSize.Medium
                && decodedCreated.Asteroids[0].Variation == 2
                && decodedCreated.Ships[0].ColorId == 4
                && decodedCreated.Ships[0].StyleId == 5
                && Approx(decodedCreated.Ships[0].Rotation, 1.25f)
                && decodedCreated.PowerUps[0].PowerUpType == PowerUpType.Rocket);

        var snapshot = new WorldSnapshot(
            12345,
            [
                new SnapshotEntry(1, new Vector2(10, 20), new Vector2(30, 40), 0.75f, 25.0f, 100.0f),
                new SnapshotEntry(2, new Vector2(-5, -6), new Vector2(0, 0), -2.5f, 40.0f, -1.0f),
            ]);

        var decodedSnapshot = RoundTrip(
            MatchMessageCodec.EncodeWorldSnapshot(snapshot),
            (ref MessageReader r, out WorldSnapshot v) => MatchMessageCodec.TryDecodeWorldSnapshot(ref r, out v));

        Check(
            "WorldSnapshot round-trips",
            decodedSnapshot is not null
                && decodedSnapshot.Frame == 12345
                && decodedSnapshot.Objects.Count == 2
                && decodedSnapshot.Objects[0].Position == new Vector2(10, 20)
                && Approx(decodedSnapshot.Objects[1].Shield, -1.0f));

        var detonated = new ProjectileDetonatedPayload(
            5,
            new Vector2(100, 200),
            [
                new DetonationHit
                {
                    Id = 3, Health = 12.5f, Shield = -1.0f,
                    Position = new Vector2(1, 2), Velocity = new Vector2(3, 4), IsActive = false,
                },
            ]);

        var decodedDetonated = RoundTrip(
            MatchMessageCodec.EncodeProjectileDetonated(detonated),
            (ref MessageReader r, out ProjectileDetonatedPayload v)
                => MatchMessageCodec.TryDecodeProjectileDetonated(ref r, out v));

        Check(
            "ProjectileDetonated round-trips, including the shield sentinel",
            decodedDetonated is not null
                && decodedDetonated.Hits.Count == 1
                && Approx(decodedDetonated.Hits[0].Shield, -1.0f)
                && !decodedDetonated.Hits[0].IsActive);

        var result = new MatchResult(
            MatchEndReason.ScoreLimit,
            "Deathmatch",
            123.5f,
            [new MatchStanding(2, "Pilot \u00c5ke", 7, 1)]);

        var decodedResult = RoundTrip(
            MatchMessageCodec.EncodeMatchCompleted(result),
            (ref MessageReader r, out MatchResult v) => MatchMessageCodec.TryDecodeMatchCompleted(ref r, out v));

        // Non-ASCII matters: display names come from platform profiles, so the length
        // prefix has to be a byte count rather than a character count.
        Check(
            "MatchCompleted round-trips a non-ASCII display name",
            decodedResult is not null
                && decodedResult.Reason == MatchEndReason.ScoreLimit
                && decodedResult.Standings[0].DisplayName == "Pilot \u00c5ke");

        var sizeProbe = MatchMessageCodec.EncodeWorldSnapshot(new WorldSnapshot(
            1,
            [.. Enumerable.Range(0, 31).Select(i =>
                new SnapshotEntry(i, Vector2.One, Vector2.One, 0.0f, 1.0f, 1.0f))]));

        var size = sizeProbe.Written.Length;
        sizeProbe.Dispose();

        // 16 ships + 15 asteroids is the worst case. Party's documented reliable message
        // limit is well above this, but a snapshot that outgrew a typical ~1200-byte
        // datagram would fragment every frame, so the number is worth watching.
        Check($"worst-case snapshot is {size} bytes (under 1200)", size < 1200);
    }

    /// <summary>
    /// The single most important property of the transport: a peer that is not the host
    /// must not be able to issue authority messages.
    /// </summary>
    private static void AuthorityGuard()
    {
        var (_, clientParty) = LoopbackPartyService.CreatePair();
        using var client = new PartyMatchNetwork(clientParty, NewPlayer(LoopbackPartyService.ClientPeerId, "Client"));

        var snapshots = 0;
        var completions = 0;
        client.WorldSnapshotReceived += _ => snapshots++;
        client.MatchCompletedReceived += _ => completions++;

        using var snapshotPacket = MatchMessageCodec.EncodeWorldSnapshot(
            new WorldSnapshot(1, [new SnapshotEntry(1, Vector2.Zero, Vector2.Zero, 0, 1, 1)]));

        var snapshotBytes = snapshotPacket.Written.ToArray();

        // Peer 3 is another client, not the host.
        clientParty.Deliver(3, snapshotBytes);
        Check($"snapshot from a non-host peer is ignored (got {snapshots})", snapshots == 0);

        clientParty.Deliver(IPartyService.HostPeerId, snapshotBytes);
        Check($"snapshot from the host is accepted (got {snapshots})", snapshots == 1);

        using var completePacket = MatchMessageCodec.EncodeMatchCompleted(
            new MatchResult(MatchEndReason.TimeLimit, "Deathmatch", 1.0f, []));

        clientParty.Deliver(99, completePacket.Written.ToArray());
        Check($"match-completed from a non-host peer is ignored (got {completions})", completions == 0);

        // A client must also refuse to act on host-only traffic it somehow receives while
        // it believes it is the host, and vice versa: ship input is host-side only.
        var inputs = 0;
        client.ShipInputReceived += (_, _, _, _, _) => inputs++;

        using var inputPacket = MatchMessageCodec.EncodeShipInput(Vector2.One, Vector2.Zero, false, 1);
        clientParty.Deliver(IPartyService.HostPeerId, inputPacket.Written.ToArray());

        Check($"a client does not process ship input (got {inputs})", inputs == 0);
    }

    /// <summary>
    /// Ship input must be attributed to the sender the transport reports, never to
    /// anything in the payload - otherwise a modified client could drive another player's
    /// ship.
    /// </summary>
    private static void InputAttribution()
    {
        var (hostParty, clientParty) = LoopbackPartyService.CreatePair();
        using var host = new PartyMatchNetwork(hostParty, NewPlayer(IPartyService.HostPeerId, "Host"));
        using var client = new PartyMatchNetwork(clientParty, NewPlayer(LoopbackPartyService.ClientPeerId, "Client"));

        var received = new List<(int Peer, Vector2 Move, bool Mine, int Sequence)>();
        host.ShipInputReceived += (peer, move, _, mine, seq) => received.Add((peer, move, mine, seq));

        client.SendShipInput(new Vector2(0.5f, -0.25f), Vector2.UnitX, true, 77);

        Check($"host received one input (got {received.Count})", received.Count == 1);

        Check(
            $"input attributed to the sending peer (got {received[0].Peer})",
            received[0].Peer == LoopbackPartyService.ClientPeerId);

        Check(
            "input values survive the wire",
            received[0].Move == new Vector2(0.5f, -0.25f) && received[0].Mine && received[0].Sequence == 77);

        // The host must not send its own input over the wire; it applies it directly.
        var before = hostParty.Sent.Count;
        host.SendShipInput(Vector2.One, Vector2.One, true, 1);
        Check($"host does not send its own input (sent {hostParty.Sent.Count - before})",
            hostParty.Sent.Count == before);
    }

    /// <summary>
    /// Truncated, over-declared and undefined-enum packets must be dropped without
    /// throwing. These arrive from out-of-date builds and from deliberately hostile peers,
    /// and an exception here lands on the simulation thread.
    /// </summary>
    private static void MalformedPackets()
    {
        var (_, clientParty) = LoopbackPartyService.CreatePair();
        using var client = new PartyMatchNetwork(clientParty, NewPlayer(LoopbackPartyService.ClientPeerId, "Client"));

        var delivered = 0;
        client.WorldSnapshotReceived += _ => delivered++;
        client.ProjectileSpawnedReceived += _ => delivered++;
        client.MatchStateChanged += _ => delivered++;

        using var full = MatchMessageCodec.EncodeWorldSnapshot(
            new WorldSnapshot(1, [new SnapshotEntry(1, Vector2.Zero, Vector2.Zero, 0, 1, 1)]));

        var bytes = full.Written.ToArray();

        var threw = false;

        try
        {
            // Every proper prefix of a valid packet.
            for (var length = 0; length < bytes.Length; length++)
            {
                clientParty.Deliver(IPartyService.HostPeerId, bytes.AsSpan(0, length));
            }

            // Valid packet with trailing rubbish.
            clientParty.Deliver(IPartyService.HostPeerId, [.. bytes, 0xFF, 0xFF]);

            // A count field claiming far more entries than the payload can hold.
            clientParty.Deliver(
                IPartyService.HostPeerId,
                [(byte)MessageType.WorldSnapshot, 1, 0, 0, 0, 0xFF, 0xFF]);

            // An undefined enum value where a projectile type belongs.
            clientParty.Deliver(
                IPartyService.HostPeerId,
                [(byte)MessageType.ProjectileSpawned, 1, 0, 0, 0, 200, 0, 0, 0, 0,
                 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

            // A NaN velocity, which would otherwise poison every position it touches.
            var nan = new MessageWriter(MessageType.ProjectileSpawned, 64);
            nan.WriteInt(1);
            nan.WriteByte((byte)ProjectileType.Laser);
            nan.WriteInt(2);
            nan.WriteVector2(Vector2.Zero);
            nan.WriteVector2(new Vector2(float.NaN, 0.0f));
            nan.WriteFloat(0.0f);
            clientParty.Deliver(IPartyService.HostPeerId, nan.Written);
            nan.Dispose();

            // An unknown message type from a newer build.
            clientParty.Deliver(IPartyService.HostPeerId, [200, 1, 2, 3]);

            // A completely empty packet.
            clientParty.Deliver(IPartyService.HostPeerId, []);
        }
        catch (Exception ex)
        {
            threw = true;
            Console.WriteLine($"      threw: {ex.GetType().Name}: {ex.Message}");
        }

        Check("malformed packets never throw", !threw);
        Check($"malformed packets raise no events (got {delivered})", delivered == 0);

        // And the connection must still work afterwards - a bad packet must not wedge it.
        clientParty.Deliver(IPartyService.HostPeerId, bytes);
        Check($"a valid packet still works after malformed ones (got {delivered})", delivered == 1);
    }

    /// <summary>
    /// A joining client must end up in the host's roster, and both peers must agree on it.
    /// </summary>
    private static void RosterSync()
    {
        var (hostParty, clientParty) = LoopbackPartyService.CreatePair();

        var hostPlayer = NewPlayer(IPartyService.HostPeerId, "Host");
        using var host = new PartyMatchNetwork(hostParty, hostPlayer);

        var clientPlayer = NewPlayer(LoopbackPartyService.ClientPeerId, "Client");
        clientPlayer.ShipColorId = 3;
        using var client = new PartyMatchNetwork(clientParty, clientPlayer);

        // The host asks, the client answers, the host publishes the whole roster back.
        hostParty.RaisePeerJoined(LoopbackPartyService.ClientPeerId);

        Check($"host roster has both players (got {host.Players.Count})", host.Players.Count == 2);

        Check(
            "host learned the client's name and appearance",
            host.Players[LoopbackPartyService.ClientPeerId].DisplayName == "Client"
                && host.Players[LoopbackPartyService.ClientPeerId].ShipColorId == 3);

        Check($"client roster has both players (got {client.Players.Count})", client.Players.Count == 2);

        Check(
            "client learned the host's name",
            client.Players[IPartyService.HostPeerId].DisplayName == "Host");

        // The host's echo of the client's own row must not replace the local object that
        // LocalShip and the UI are already holding.
        Check(
            "client kept its own PlayerState instance",
            ReferenceEquals(client.LocalPlayer, clientPlayer));

        using (var stale = MatchMessageCodec.EncodeRosterEntry(
            NewPlayer(99, "Stale player")))
        {
            clientParty.Deliver(IPartyService.HostPeerId, stale.Written);
        }

        Check("the test client contains a stale row before reconciliation",
            client.Players.ContainsKey(99));

        client.SendIdentity();

        Check("an authoritative snapshot removes stale client-only rows",
            host.Players.Keys.Order().SequenceEqual(client.Players.Keys.Order()));
        Check("roster state agrees after reconciliation",
            host.SortedPlayers().Zip(client.SortedPlayers()).All(pair =>
                pair.First.PeerId == pair.Second.PeerId
                && pair.First.DisplayName == pair.Second.DisplayName
                && pair.First.ShipColorId == pair.Second.ShipColorId
                && pair.First.ShipStyleId == pair.Second.ShipStyleId
                && pair.First.IsReady == pair.Second.IsReady));

        // A third peer joins, so a departure can be observed by someone other than the
        // player leaving - a peer is never told about its own exit.
        using (var thirdIdentity = new MessageWriter(MessageType.SubmitIdentity, 64))
        {
            thirdIdentity.WriteString("Third");
            thirdIdentity.WriteString(string.Empty);
            thirdIdentity.WriteInt(5);
            thirdIdentity.WriteInt(6);
            hostParty.Deliver(3, thirdIdentity.Written);
        }

        Check($"host roster has three players (got {host.Players.Count})", host.Players.Count == 3);
        Check($"client learned about the third player (got {client.Players.Count})", client.Players.Count == 3);

        var left = new List<int>();
        client.PlayerLeft += left.Add;
        hostParty.RaisePeerLeft(3);

        Check($"host dropped the departed player (got {host.Players.Count})", host.Players.Count == 2);
        Check($"client was told about the departure (got [{string.Join(",", left)}])", left is [3]);
        Check($"client dropped the departed player (got {client.Players.Count})", client.Players.Count == 2);
    }

    private static void RosterSyncAfterTransportJoin()
    {
        var (hostParty, clientParty) = LoopbackPartyService.CreatePair();
        using var host = new PartyMatchNetwork(hostParty, NewPlayer(IPartyService.HostPeerId, "Host"));

        // GameCore raises PeerJoined during the transport join, before LobbyScreen
        // constructs the client's PartyMatchNetwork. These messages have no listener.
        hostParty.RaisePeerJoined(LoopbackPartyService.ClientPeerId);
        clientParty.RaisePeerJoined(IPartyService.HostPeerId);
        Check("transport join alone has not registered the client", host.Players.Count == 1);

        var local = NewPlayer(LoopbackPartyService.ClientPeerId, "Invited player");
        local.ShipColorId = 3;
        using var client = new PartyMatchNetwork(clientParty, local);

        client.SendIdentity();

        Check("post-join identity populates both rosters",
            host.Players.Count == 2 && client.Players.Count == 2);
        Check("post-join identity preserves name and appearance",
            host.Players.TryGetValue(local.PeerId, out var registered)
                && registered.DisplayName == local.DisplayName && registered.ShipColorId == 3);
        Check("post-join identity preserves the local player instance",
            ReferenceEquals(client.LocalPlayer, local));

        client.PublishLocalReady(true);
        client.SendIdentity();
        hostParty.RaisePeerJoined(local.PeerId);
        Check("duplicate identity handshakes preserve roster and ready state",
            host.Players.Count == 2 && client.Players.Count == 2
                && host.Players[local.PeerId].IsReady && local.IsReady);
    }

    /// <summary>
    /// A client's ready toggle and appearance change must reach the host, not just stay
    /// local. This is D-gap 1: <see cref="PartyMatchNetwork"/> handled
    /// <see cref="MessageType.SubmitReady"/>/<see cref="MessageType.SubmitAppearance"/>
    /// host-side with nothing that ever sent them from a client.
    /// </summary>
    private static void LobbySubmissions()
    {
        var (hostParty, clientParty) = LoopbackPartyService.CreatePair();

        var hostPlayer = NewPlayer(IPartyService.HostPeerId, "Host");
        using var host = new PartyMatchNetwork(hostParty, hostPlayer);

        var clientPlayer = NewPlayer(LoopbackPartyService.ClientPeerId, "Client");
        using var client = new PartyMatchNetwork(clientParty, clientPlayer);

        hostParty.RaisePeerJoined(LoopbackPartyService.ClientPeerId);

        client.PublishLocalReady(true);

        Check(
            "client's ready toggle reached the host",
            host.Players[LoopbackPartyService.ClientPeerId].IsReady);

        Check(
            "client's own copy is ready too",
            client.LocalPlayer!.IsReady);

        client.PublishLocalAppearance(colorId: 4, styleId: 2);

        Check(
            "client's appearance change reached the host",
            host.Players[LoopbackPartyService.ClientPeerId] is { ShipColorId: 4, ShipStyleId: 2 });

        // The host publishing its own change is the other half of the same call: it has
        // no host to submit to, so it must broadcast its roster row directly instead.
        host.PublishLocalReady(true);

        Check("host's own ready toggle reaches the client", client.Players[IPartyService.HostPeerId].IsReady);
    }

    /// <summary>
    /// D-gap 3: <see cref="IMatchNetwork.RosterChanged"/> and
    /// <see cref="IMatchNetwork.ConnectionLost"/> must be reachable through the interface
    /// alone, with no downcast to <see cref="PartyMatchNetwork"/> needed.
    /// </summary>
    private static void InterfaceLevelEvents()
    {
        var (hostParty, clientParty) = LoopbackPartyService.CreatePair();
        IMatchNetwork host = new PartyMatchNetwork(hostParty, NewPlayer(IPartyService.HostPeerId, "Host"));

        var rosterChanges = 0;
        host.RosterChanged += () => rosterChanges++;

        hostParty.RaisePeerJoined(LoopbackPartyService.ClientPeerId);

        using (var identity = new MessageWriter(MessageType.SubmitIdentity, 64))
        {
            identity.WriteString("Client");
            identity.WriteString(string.Empty);
            identity.WriteInt(0);
            identity.WriteInt(0);
            hostParty.Deliver(LoopbackPartyService.ClientPeerId, identity.Written);
        }

        Check(
            $"IMatchNetwork.RosterChanged fires with no downcast (got {rosterChanges})",
            rosterChanges > 0);

        var lostResults = new List<PlatformResult>();
        host.ConnectionLost += lostResults.Add;

        hostParty.RaiseNetworkDestroyed(PlatformResult.Fail(PlatformStatus.Failed, "lost"));

        Check(
            $"IMatchNetwork.ConnectionLost fires with no downcast (got {lostResults.Count})",
            lostResults is [{ Succeeded: false }]);

        (host as IDisposable)?.Dispose();
    }

    /// <summary>
    /// The whole stack: a host world driven by a director, replicating over the real byte
    /// path into a client world.
    /// </summary>
    private static void EndToEndMatch()
    {
        GameObject.ResetIdCounter();

        var (hostParty, clientParty) = LoopbackPartyService.CreatePair();

        var hostPlayer = NewPlayer(IPartyService.HostPeerId, "Host");
        using var hostNet = new PartyMatchNetwork(hostParty, hostPlayer);

        var clientPlayer = NewPlayer(LoopbackPartyService.ClientPeerId, "Client");
        using var clientNet = new PartyMatchNetwork(clientParty, clientPlayer);

        hostParty.RaisePeerJoined(LoopbackPartyService.ClientPeerId);

        var hostWorld = new World(new Random(31337));
        var director = new MatchDirector(hostNet);
        director.Setup(hostWorld);

        var clientWorld = new World(new Random(1));
        clientWorld.AttachNetwork(clientNet);
        clientWorld.Initialize(false, clientNet.SortedPlayers());

        hostWorld.StartMatch();

        Check(
            $"client built the world over the wire (asteroids {clientWorld.Asteroids.Count}, ships {clientWorld.Ships.Count})",
            clientWorld.Asteroids.Count == hostWorld.Asteroids.Count && clientWorld.Ships.Count == 2);

        Check(
            "client bound its own ship",
            clientWorld.LocalShip?.OwnerPeerId == LoopbackPartyService.ClientPeerId);

        // Drift a remote object, then let snapshots pull it back over the real byte path.
        var remote = clientWorld.Ships[hostWorld.LocalShip!.UniqueId];
        var target = new Vector2(900.0f, 900.0f);
        hostWorld.LocalShip.Teleport(target);
        remote.Teleport(target + new Vector2(150.0f, 0.0f));

        for (var i = 0; i < 30; i++)
        {
            hostWorld.BroadcastSnapshot();
        }

        Check(
            $"snapshots converge over the wire (error {Vector2.Distance(remote.Position, target):F2})",
            Vector2.Distance(remote.Position, target) < 1.0f);

        // A dropped link must not corrupt the client; it just stops updating.
        hostParty.DropSends = true;
        var frozen = remote.Position;
        hostWorld.LocalShip.Teleport(new Vector2(2000.0f, 2000.0f));
        hostWorld.BroadcastSnapshot();

        Check("client is unchanged while the link is down", remote.Position == frozen);

        hostParty.DropSends = false;
        hostWorld.BroadcastSnapshot();

        Check("client resumes once the link is back", remote.Position != frozen);
    }

    /// <summary>
    /// The gate that decides whether a networked match ever begins.
    /// </summary>
    /// <remarks>
    /// <see cref="EndToEndMatch"/> calls <c>World.StartMatch</c> itself, which is why it
    /// proved replication works and still could not notice that nothing ever called it:
    /// the host never marked its own <see cref="PlayerState.InGame"/>, the director waited
    /// on a roster that could not complete, and both peers sat on the gameplay screen in
    /// <see cref="MatchState.PlayersJoining"/> forever. This drives the director instead of
    /// stepping around it.
    /// </remarks>
    private static void MatchStartGate()
    {
        GameObject.ResetIdCounter();

        var (hostParty, clientParty) = LoopbackPartyService.CreatePair();
        using var hostNet = new PartyMatchNetwork(hostParty, NewPlayer(IPartyService.HostPeerId, "Host"));
        using var clientNet = new PartyMatchNetwork(clientParty, NewPlayer(LoopbackPartyService.ClientPeerId, "Client"));

        hostParty.RaisePeerJoined(LoopbackPartyService.ClientPeerId);

        var hostWorld = new World(new Random(31337));
        var director = new MatchDirector(hostNet);
        director.Setup(hostWorld);

        Check(
            "a networked match opens in PlayersJoining",
            director.MatchState.HasMatchState(MatchState.PlayersJoining));

        Check(
            $"both players are on the host's roster (got {hostNet.Players.Count})",
            hostNet.Players.Count == 2);

        // Only the client reports in. The host is a player too, so this must not be enough.
        clientNet.SendLoaded();
        Tick(director, 60);

        Check(
            "the match does not start while a player has not reported in",
            director.MatchState.HasMatchState(MatchState.PlayersJoining));

        // Regression: this call used to return immediately on a host, leaving its own
        // InGame false and the gate permanently shut.
        hostNet.SendLoaded();

        Check(
            "a host reporting in marks its own roster entry",
            hostNet.LocalPlayerState.InGame);

        Tick(director, 60);

        Check(
            $"the match starts once every player has loaded (state {director.MatchState})",
            !director.MatchState.HasMatchState(MatchState.PlayersJoining));
    }

    /// <summary>
    /// A client's input must reach the host's copy of its ship.
    /// </summary>
    /// <remarks>
    /// <see cref="InputAttribution"/> covers the wire once something calls
    /// <c>SendShipInput</c>. Nothing in the game did: the method existed only on the
    /// concrete network, not on <see cref="IMatchNetwork"/>, so the simulation had no way
    /// to reach it and a client's ship stood still on the host and on every other client
    /// while moving perfectly on its own screen. This drives it the way the game does,
    /// through <see cref="World.SetLocalInput"/>.
    /// </remarks>
    private static void ClientInputReachesHost()
    {
        GameObject.ResetIdCounter();

        var (hostParty, clientParty) = LoopbackPartyService.CreatePair();
        using var hostNet = new PartyMatchNetwork(hostParty, NewPlayer(IPartyService.HostPeerId, "Host"));
        using var clientNet = new PartyMatchNetwork(clientParty, NewPlayer(LoopbackPartyService.ClientPeerId, "Client"));

        hostParty.RaisePeerJoined(LoopbackPartyService.ClientPeerId);

        var hostWorld = new World(new Random(31337));
        var director = new MatchDirector(hostNet);
        director.Setup(hostWorld);

        var clientWorld = new World(new Random(1));
        clientWorld.AttachNetwork(clientNet);
        clientWorld.Initialize(false, clientNet.SortedPlayers());

        hostWorld.StartMatch();

        var movement = new Vector2(1.0f, 0.0f);
        var fire = new Vector2(0.0f, -1.0f);
        clientWorld.SetLocalInput(movement, fire, deployMineHeld: false);

        var hostCopy = hostWorld.GetShipFor(LoopbackPartyService.ClientPeerId);

        Check(
            "the client's input reaches the host's copy of its ship",
            hostCopy is not null
                && hostCopy.ShipInput.MovementDirection == movement
                && hostCopy.ShipInput.FireDirection == fire);

        // The client predicts locally as well - the send mirrors input, it does not
        // replace driving the local ship.
        Check(
            "the client still drives its own ship for prediction",
            clientWorld.LocalShip?.ShipInput.MovementDirection == movement);

        // Held keys must send the press edge, or one held mine key lays a mine every frame
        // on the host.
        var mineFlags = new List<bool>();
        hostNet.ShipInputReceived += (_, _, _, mine, _) => mineFlags.Add(mine);

        clientWorld.SetLocalInput(movement, fire, deployMineHeld: true);
        clientWorld.SetLocalInput(movement, fire, deployMineHeld: true);

        Check(
            $"a held mine control sends the press edge once (got [{string.Join(", ", mineFlags)}])",
            mineFlags is [true, false]);

        // Stale packets must not win. The host keeps the newest sequence it has seen.
        clientNet.SendShipInput(new Vector2(-1.0f, 0.0f), Vector2.Zero, false, 1);

        Check(
            "an input that arrives out of order is ignored",
            hostCopy!.ShipInput.MovementDirection == movement);
    }

    private static void HostedMatchEndsWhenClientsLeave()
    {
        GameObject.ResetIdCounter();

        var (hostParty, clientParty) = LoopbackPartyService.CreatePair();
        using var hostNet = new PartyMatchNetwork(hostParty, NewPlayer(IPartyService.HostPeerId, "Host"));
        using var clientNet = new PartyMatchNetwork(clientParty, NewPlayer(LoopbackPartyService.ClientPeerId, "Client"));

        hostParty.RaisePeerJoined(LoopbackPartyService.ClientPeerId);

        var world = new World(new Random(17));
        using var director = new MatchDirector(hostNet);
        director.Setup(world);

        MatchResult? result = null;
        director.MatchCompleted += completed => result = completed;

        hostParty.RaisePeerLeft(LoopbackPartyService.ClientPeerId);

        Check("a hosted match ends when every client has left",
            result?.Reason == MatchEndReason.LastPlayerStanding
                && director.MatchState.HasMatchState(MatchState.MatchComplete));
        Check("the departed client has no ship left in the hosted world",
            world.GetShipFor(LoopbackPartyService.ClientPeerId) is null);
    }

    /// <summary>
    /// The mirror of the check above, from the side that used to be forgotten: a client
    /// whose host quits has to end its own match. Nothing is coming down the wire to tell
    /// it - the peer that would have broadcast the completion is the one that left - so
    /// without this the player left behind kept flying around a world nothing was driving
    /// while the host, of all peers, was the one shown "everyone else left the match".
    /// </summary>
    private static void ClientEndsMatchWhenHostLeaves()
    {
        GameObject.ResetIdCounter();

        var (hostParty, clientParty) = LoopbackPartyService.CreatePair();
        using var hostNet = new PartyMatchNetwork(hostParty, NewPlayer(IPartyService.HostPeerId, "Host"));
        using var clientNet = new PartyMatchNetwork(clientParty, NewPlayer(LoopbackPartyService.ClientPeerId, "Client"));

        clientParty.RaisePeerJoined(IPartyService.HostPeerId);
        hostParty.RaisePeerJoined(LoopbackPartyService.ClientPeerId);

        // The client only learns who the host is from the roster snapshot the identity
        // handshake produces, and only a peer it knows about can be seen to leave.
        clientNet.SendIdentity();
        Check($"the client knows about the host (got {clientNet.Players.Count})",
            clientNet.Players.ContainsKey(IPartyService.HostPeerId) && clientNet.Players.Count == 2);

        using (var thirdIdentity = new MessageWriter(MessageType.SubmitIdentity, 64))
        {
            thirdIdentity.WriteString("Third");
            thirdIdentity.WriteString(string.Empty);
            thirdIdentity.WriteInt(1);
            thirdIdentity.WriteInt(1);
            hostParty.Deliver(3, thirdIdentity.Written);
        }

        var world = new World(new Random(23));
        using var director = new MatchDirector(clientNet);
        director.Setup(world);

        MatchResult? result = null;
        director.MatchCompleted += completed => result = completed;

        var sentBeforeCompletion = clientParty.Sent.Count;

        // A peer that is not the host going is not the client's business: the host is
        // still there to decide what that means for the match.
        hostParty.RaisePeerLeft(3);

        Check("a client ignores another client leaving",
            result is null && !director.MatchState.HasMatchState(MatchState.MatchComplete));

        clientParty.RaisePeerLeft(IPartyService.HostPeerId);

        Check($"a client ends its match when the host leaves (got {result?.Reason.ToString() ?? "none"})",
            result?.Reason == MatchEndReason.HostLeft
                && director.MatchState.HasMatchState(MatchState.MatchComplete));
        Check("a client does not broadcast its own completion",
            clientParty.Sent.Count == sentBeforeCompletion);
    }

    /// <summary>Advances the director by whole frames at a fixed step.</summary>
    private static void Tick(MatchDirector director, int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            director.Tick(1.0f / 60.0f);
        }
    }

    private delegate bool TryDecode<T>(ref MessageReader reader, out T value);

    /// <summary>Encodes, then decodes past the type byte, exactly as the transport does.</summary>
    private static T? RoundTrip<T>(MessageWriter writer, TryDecode<T> decode)
    {
        using (writer)
        {
            var reader = new MessageReader(writer.Written);

            if (!reader.TryReadMessageType(out _))
            {
                return default;
            }

            return decode(ref reader, out var value) && reader.IsFullyConsumed ? value : default;
        }
    }

    private static PlayerState NewPlayer(int peerId, string name) => new()
    {
        PeerId = peerId,
        DisplayName = name,
        ShipColorId = 0,
        ShipStyleId = 0,
    };

    private static bool Approx(float a, float b) => MathF.Abs(a - b) < 0.0001f;

    private static void Check(string label, bool condition)
        => Console.WriteLine($"    {(condition ? "PASS" : "FAIL")} {label}");
}
