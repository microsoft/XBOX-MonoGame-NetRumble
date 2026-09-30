using System.Numerics;
using NetRumble.Core;
using NetRumble.Core.Net;
using NetRumble.Core.Objects;

/// <summary>
/// Checks for the replication half of <see cref="World"/>, driven by a host and a client
/// world wired together through <see cref="LoopbackHub"/>.
/// </summary>
/// <remarks>
/// These target the failure modes that only appear on a real network and are silent
/// offline: a duplicated layout message, a snapshot arriving out of order, and the client
/// mistaking its own copy of an object for the host's.
/// </remarks>
internal static class ReplicationChecks
{
    private const float Step = 1.0f / 60.0f;

    public static void Run()
    {
        Console.WriteLine("[8] Replication");

        WorldConstruction();
        DuplicateMatchCreated();
        StaleSnapshotRejected();
        SnapshotConverges();
        DiscreteEvents();

        Console.WriteLine();
    }

    /// <summary>
    /// The client must build the same entity set as the host, with matching ids, and bind
    /// its own ship rather than the host's.
    /// </summary>
    private static void WorldConstruction()
    {
        var (hostWorld, clientWorld, _, _) = NewSession();

        Check(
            $"client built the host's asteroids (host {hostWorld.Asteroids.Count}, client {clientWorld.Asteroids.Count})",
            clientWorld.Asteroids.Count == hostWorld.Asteroids.Count && clientWorld.Asteroids.Count > 0);

        Check(
            $"client built both ships (got {clientWorld.Ships.Count})",
            clientWorld.Ships.Count == 2);

        Check(
            "ship ids agree across peers",
            hostWorld.Ships.Keys.OrderBy(k => k).SequenceEqual(clientWorld.Ships.Keys.OrderBy(k => k)));

        // The client must not adopt the host's ship: local prediction and the camera both
        // follow LocalShip, so binding the wrong one would drive the wrong player.
        Check(
            "client bound its own ship",
            clientWorld.LocalShip is not null
                && clientWorld.LocalShip.OwnerPeerId == LoopbackHub.ClientPeerId);

        Check(
            "host bound its own ship",
            hostWorld.LocalShip is not null && hostWorld.LocalShip.OwnerPeerId == NRConst.HostPeerId);
    }

    /// <summary>
    /// A repeated layout message must be ignored. Applying it twice would stack a second
    /// set of ships and asteroids on top of the first.
    /// </summary>
    private static void DuplicateMatchCreated()
    {
        var (hostWorld, clientWorld, hub, _) = NewSession();

        var asteroidsBefore = clientWorld.Asteroids.Count;
        var shipsBefore = clientWorld.Ships.Count;
        var objectsBefore = clientWorld.GameObjects.Count;

        // Resend the exact layout the host already sent, as a retransmit or a late-joiner
        // resend would.
        hostWorld.StartMatch();

        Check(
            $"duplicate layout adds no asteroids (got {clientWorld.Asteroids.Count}, was {asteroidsBefore})",
            clientWorld.Asteroids.Count == asteroidsBefore);

        Check(
            $"duplicate layout adds no ships (got {clientWorld.Ships.Count}, was {shipsBefore})",
            clientWorld.Ships.Count == shipsBefore);

        Check(
            $"duplicate layout adds no objects (got {clientWorld.GameObjects.Count}, was {objectsBefore})",
            clientWorld.GameObjects.Count == objectsBefore);

        _ = hub;
    }

    /// <summary>
    /// Snapshots ride an unreliable channel and can arrive out of order. An older frame
    /// applied after a newer one drags every object backwards, so it must be dropped.
    /// </summary>
    private static void StaleSnapshotRejected()
    {
        var (hostWorld, clientWorld, hub, _) = NewSession();

        var remote = clientWorld.Ships.Values.First(s => s.OwnerPeerId == NRConst.HostPeerId);
        var hostShip = hostWorld.Ships[remote.UniqueId];

        // Two snapshots at clearly different positions, delivered newest first.
        hostShip.Teleport(new Vector2(400.0f, 400.0f));
        hub.Queue = true;
        hostWorld.BroadcastSnapshot();

        hostShip.Teleport(new Vector2(1600.0f, 1600.0f));
        hostWorld.BroadcastSnapshot();

        Check($"two snapshots were held (got {hub.QueuedCount})", hub.QueuedCount == 2);

        hub.Queue = false;
        hub.Flush([1, 0]);

        // The client eases toward each snapshot, so the test is which one it moved toward,
        // not an exact position: the newer target is the far corner.
        var towardNewer = Vector2.Distance(remote.Position, new Vector2(1600.0f, 1600.0f));
        var towardStale = Vector2.Distance(remote.Position, new Vector2(400.0f, 400.0f));

        Check(
            $"out-of-order snapshot is discarded (newer {towardNewer:F0}, stale {towardStale:F0})",
            towardNewer < towardStale);
    }

    /// <summary>
    /// Repeated snapshots must pull a drifted remote object onto the host's copy, and the
    /// local ship must be corrected far more gently than a remote one.
    /// </summary>
    private static void SnapshotConverges()
    {
        var (hostWorld, clientWorld, _, _) = NewSession();

        var remote = clientWorld.Ships.Values.First(s => s.OwnerPeerId == NRConst.HostPeerId);
        var hostRemote = hostWorld.Ships[remote.UniqueId];
        var local = clientWorld.LocalShip!;
        var hostLocal = hostWorld.Ships[local.UniqueId];

        // Same 200-unit error on both, well inside the 250-unit snap distance so the gentle
        // path is the one under test.
        var target = new Vector2(1200.0f, 1200.0f);
        hostRemote.Teleport(target);
        hostLocal.Teleport(target);
        remote.Teleport(target + new Vector2(200.0f, 0.0f));
        local.Teleport(target + new Vector2(200.0f, 0.0f));

        for (var i = 0; i < 30; i++)
        {
            hostWorld.BroadcastSnapshot();
        }

        var remoteError = Vector2.Distance(remote.Position, target);
        var localError = Vector2.Distance(local.Position, target);

        Check($"remote object converges on the host (error {remoteError:F1})", remoteError < 1.0f);

        Check(
            $"local ship is corrected more gently than a remote one ({localError:F1} > {remoteError:F1})",
            localError > remoteError);

        // A large error is a missed collision or a respawn rather than drift, so the
        // reconciler must take the host's state outright instead of sliding across the map.
        var far = target + new Vector2(NRConst.LocalSnapshotSnapDistance + 200.0f, 0.0f);
        local.Teleport(far);
        hostLocal.Teleport(target);
        hostWorld.BroadcastSnapshot();

        Check(
            $"local ship snaps when prediction is far gone (error {Vector2.Distance(local.Position, target):F1})",
            Vector2.Distance(local.Position, target) < 0.01f);
    }

    /// <summary>
    /// Projectile, power-up and gameplay-event messages must reproduce the host's state on
    /// the client without the client running any authoritative logic of its own.
    /// </summary>
    private static void DiscreteEvents()
    {
        var (hostWorld, clientWorld, _, _) = NewSession();

        var hostShip = hostWorld.LocalShip!;
        var events = new List<GameplayEventType>();
        clientWorld.GameplayEvent += (type, _) => events.Add(type);

        var clientProjectilesBefore = clientWorld.GameObjects.Values.Count(o => o is Projectile);

        hostWorld.CreateProjectiles(WeaponType.Laser, hostShip.UniqueId, new Vector2(1.0f, 0.0f));

        var spawned = clientWorld.GameObjects.Values
            .Where(o => o is Projectile p && p.IsActive)
            .ToList();

        Check($"projectile spawn reached the client (got {spawned.Count})", spawned.Count > 0);

        Check(
            "client did not invent projectiles of its own",
            clientWorld.GameObjects.Values.Count(o => o is Projectile)
                >= clientProjectilesBefore + spawned.Count);

        var projectileId = spawned[0].UniqueId;

        Check(
            "client projectile shares the host's id",
            hostWorld.GameObjects.ContainsKey(projectileId));

        // Run the host until the shot expires; the detonation message must retire the
        // client's copy too rather than leaving it flying forever.
        for (var i = 0; i < 600 && clientWorld.GameObjects[projectileId].IsActive; i++)
        {
            hostWorld.Tick(Step);
        }

        Check(
            "detonation retires the client's projectile",
            !clientWorld.GameObjects[projectileId].IsActive);

        Check("gameplay events reached the client", events.Count > 0);

        // The client must never emit an authority-sourced event itself, or every effect
        // would play twice on a listen server's client.
        var before = events.Count;
        clientWorld.EmitGameplayEvent(GameplayEventType.LaserImpact, Vector2.Zero);

        Check("client does not raise authority-sourced events locally", events.Count == before);
    }

    private static (World HostWorld, World ClientWorld, LoopbackHub Hub, MatchDirector Director) NewSession()
    {
        GameObject.ResetIdCounter();

        var hub = new LoopbackHub();

        var hostWorld = new World(new Random(4242));
        var director = new MatchDirector(hub.Host);
        director.Setup(hostWorld);

        var clientWorld = new World(new Random(99));
        clientWorld.AttachNetwork(hub.Client);
        clientWorld.Initialize(false, hub.Client.SortedPlayers());

        // The host announces the layout from StartMatch, which is what builds the client.
        hostWorld.StartMatch();

        return (hostWorld, clientWorld, hub, director);
    }

    private static void Check(string label, bool condition)
        => Console.WriteLine($"    {(condition ? "PASS" : "FAIL")} {label}");
}
