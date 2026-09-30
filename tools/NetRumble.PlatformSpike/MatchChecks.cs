using NetRumble.Core;
using NetRumble.Core.Net;

/// <summary>
/// End-to-end checks for <see cref="MatchDirector"/> driving a real <see cref="World"/>
/// over an <see cref="OfflineMatchNetwork"/>.
/// </summary>
/// <remarks>
/// The director is a state machine whose phases are separated by timers, so most of its
/// bugs are ordering bugs that only appear after several hundred simulated frames. These
/// run a whole match at a fixed step rather than testing methods in isolation.
/// </remarks>
internal static class MatchChecks
{
    private const float Step = 1.0f / 60.0f;

    public static void Run()
    {
        Console.WriteLine("[7] Match director");

        PhaseProgression();
        SimulationGate();
        DeathScoringAndRespawn();

        Console.WriteLine();
    }

    /// <summary>
    /// A solo offline match must walk PlayersJoining -> Starting -> Running on its own,
    /// and emit one countdown tick per whole second on the way.
    /// </summary>
    private static void PhaseProgression()
    {
        var (director, _, _) = NewMatch();
        var countdowns = new List<int>();
        director.CountdownChanged += countdowns.Add;

        Check("starts in PlayersJoining", director.MatchState == MatchState.PlayersJoining);

        // The lone player is already in-game, so the first step should find everyone
        // loaded and lay the world out.
        director.Tick(Step);
        Check("advances to Starting once everyone is loaded", director.MatchState == MatchState.Starting);

        Advance(director, NRConst.SimulationDelayStarting);
        Check("countdown expiry goes live", director.MatchState == MatchState.Running);

        // 5 down to 1: zero is never broadcast because the state leaves Starting on the
        // frame the timer expires.
        Check(
            $"countdown counted down once per second (got [{string.Join(", ", countdowns)}])",
            countdowns is [5, 4, 3, 2, 1]);

        Check("match clock restarted on going live", director.ElapsedMatchTime < 0.5f);
    }

    /// <summary>
    /// Ships must hold still through the start countdown and move once the match is live.
    /// This is the gate that stops players drifting off their spawn points while the
    /// countdown runs.
    /// </summary>
    private static void SimulationGate()
    {
        var (director, world, _) = NewMatch();
        director.Tick(Step);

        var ship = world.Ships.Values.First();
        ship.Velocity = new System.Numerics.Vector2(200.0f, 0.0f);
        var frozenStart = ship.Position;

        Advance(director, 0.5f);
        Check("ship is frozen during the start countdown", ship.Position == frozenStart);

        Advance(director, NRConst.SimulationDelayStarting);
        Check("match is live", director.MatchState == MatchState.Running);

        var liveStart = ship.Position;
        ship.Velocity = new System.Numerics.Vector2(200.0f, 0.0f);
        Advance(director, 0.5f);

        Check("ship moves once the match is live", ship.Position != liveStart);
    }

    /// <summary>
    /// Dying to something that is not another player's projectile costs a point, and the
    /// score can never go negative. The ship must then come back after the respawn delay.
    /// </summary>
    private static void DeathScoringAndRespawn()
    {
        var (director, world, network) = NewMatch();

        director.Tick(Step);
        Advance(director, NRConst.SimulationDelayStarting);

        var ship = world.Ships.Values.First();
        var player = network.Players[ship.OwnerPeerId];
        player.Score = 3;

        // No LastDamagedById, so this is a self-inflicted death: minus one.
        ship.Health = 0.0f;
        director.Tick(Step);

        Check($"self-inflicted death costs a point (got {player.Score})", player.Score == 2);
        Check("destroyed ship is deactivated", !ship.IsActive);

        Advance(director, NRConst.ShipRespawnDelay);
        Check("ship respawns after the delay", ship.IsActive);
        Check("respawned ship is at full health", ship.Health > 0.0f);

        // Floor test: three more deaths from a score of two must stop at zero.
        for (var i = 0; i < 3; i++)
        {
            ship.Health = 0.0f;
            director.Tick(Step);
            Advance(director, NRConst.ShipRespawnDelay);
        }

        Check($"score floors at zero (got {player.Score})", player.Score == 0);
    }

    private static (MatchDirector Director, World World, IMatchNetwork Network) NewMatch()
    {
        GameObject_ResetIds();

        var player = new PlayerState
        {
            DisplayName = "Solo",
            ShipColorId = 0,
            ShipStyleId = 0,
        };

        var network = new OfflineMatchNetwork(player);
        var director = new MatchDirector(network);
        var world = new World(new Random(1234));
        director.Setup(world);

        return (director, world, network);
    }

    /// <summary>Runs the director for a wall-clock duration at the fixed step.</summary>
    private static void Advance(MatchDirector director, float seconds)
    {
        var steps = (int)MathF.Ceiling(seconds / Step);
        for (var i = 0; i < steps; i++)
        {
            director.Tick(Step);
        }
    }

    private static void GameObject_ResetIds() =>
        NetRumble.Core.Objects.GameObject.ResetIdCounter();

    private static void Check(string label, bool condition)
        => Console.WriteLine($"    {(condition ? "PASS" : "FAIL")} {label}");
}
