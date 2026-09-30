using System.Numerics;
using NetRumble.Core;
using NetRumble.Core.Net;
using NetRumble.Core.Net.Wire;
using NetRumble.Core.Objects;
using NetRumble.Core.Tuning;

namespace NetRumble.PlatformSpike;

/// <summary>
/// Drives a match all the way to <see cref="MatchState.MatchComplete"/>.
/// </summary>
/// <remarks>
/// Nothing had ever ended a match. The autopilot cannot reach it - a Deathmatch runs for
/// ten minutes and the harness runs for tens of seconds - and <c>MatchChecks</c> stopped
/// at scoring and respawn. So the win conditions, the standings the results screen is
/// built from, and the agreement between host and client about who won were all unexecuted
/// code, which in this port has repeatedly meant broken code.
///
/// Standings are the part worth being careful about: the director assigns placement from
/// the order <see cref="IMatchNetwork.PlayersByScore"/> returns, and trusts it completely.
/// An implementation that returns the roster unsorted produces a scoreboard that looks
/// plausible and is wrong, with the winner's name against last place.
/// </remarks>
internal static class MatchEndChecks
{
    private const float Step = 1.0f / 60.0f;

    public static void Run()
    {
        Console.WriteLine("[18] End of match");

        TimeLimitEndsTheMatch();
        ScoreLimitEndsTheMatch();
        StandingsAreRankedAndPlaced();
        ProductionRankingsAreSorted();
        LastPlayerStandingEndsTheMatch();
        HostAndClientAgreeOnTheResult();
        CompletionIsIdempotent();
        TheTimeLimitOverrideApplies();

        Console.WriteLine();
    }

    /// <summary>
    /// The clock running out has to end the match by itself, with no player action at all.
    /// </summary>
    private static void TimeLimitEndsTheMatch()
    {
        var (director, _, _) = LiveMatch();
        var results = new List<MatchResult>();
        director.MatchCompleted += results.Add;

        var limit = TuningLibrary.GameMode.TimeLimit;

        Check($"the mode has a real time limit (got {limit}s)", limit > 0.0f);
        Check("the match is not complete before the limit", !director.MatchState.HasMatchState(MatchState.MatchComplete));

        Advance(director, limit + 1.0f);

        Check("the time limit completes the match", director.MatchState.HasMatchState(MatchState.MatchComplete));
        Check($"exactly one completion was announced (got {results.Count})", results.Count == 1);

        if (results.Count == 0)
        {
            return;
        }

        Check(
            $"the reason is the time limit (got {results[0].Reason})",
            results[0].Reason == MatchEndReason.TimeLimit);

        Check(
            $"the result carries the mode name (got \"{results[0].GameMode}\")",
            results[0].GameMode == TuningLibrary.GameMode.DisplayName);

        // A duration that reads zero, or that kept counting past the end, is the sort of
        // thing a results screen would show verbatim.
        Check(
            $"the duration is the match length (got {results[0].Elapsed:F1}s)",
            results[0].Elapsed >= limit && results[0].Elapsed < limit + 1.0f);

        // The clock stopping matters: the world keeps ticking on the finished screen.
        var frozen = director.ElapsedMatchTime;
        Advance(director, 2.0f);
        Check("the match clock stops once the match is complete", director.ElapsedMatchTime == frozen);
    }

    /// <summary>
    /// Reaching the target score ends the match at the moment the point lands, which is
    /// checked immediately after a death is scored.
    /// </summary>
    private static void ScoreLimitEndsTheMatch()
    {
        var (director, world, hub) = LiveMatch();
        var results = new List<MatchResult>();
        director.MatchCompleted += results.Add;

        var target = TuningLibrary.GameMode.TargetScore;
        Check($"the mode has a target score (got {target})", target > 0);

        // Park the client on the target, then kill the host's ship so a scoring death
        // occurs. The client's score is untouched by the host dying, so what ends the
        // match is the threshold test rather than the death itself.
        hub.Roster[LoopbackHub.ClientPeerId].Score = target;

        var hostShip = world.Ships.Values.First(s => s.OwnerPeerId == NRConst.HostPeerId);
        hostShip.Health = 0.0f;
        director.Tick(Step);

        Check("reaching the target score completes the match", director.MatchState.HasMatchState(MatchState.MatchComplete));
        Check($"exactly one completion was announced (got {results.Count})", results.Count == 1);
        Check(
            $"the reason is the score limit (got {(results.Count > 0 ? results[0].Reason.ToString() : "none")})",
            results.Count > 0 && results[0].Reason == MatchEndReason.ScoreLimit);

        // A completed match must not queue a respawn: the ship would return to a world
        // nobody is playing in any more.
        Advance(director, NRConst.ShipRespawnDelay + 1.0f);
        Check("no respawn is queued after the match ends", !hostShip.IsActive);
    }

    /// <summary>
    /// The standings are the results screen. Placement comes straight from the order
    /// <see cref="IMatchNetwork.PlayersByScore"/> hands back.
    /// </summary>
    private static void StandingsAreRankedAndPlaced()
    {
        var (director, world, hub) = LiveMatch();
        MatchResult? result = null;
        director.MatchCompleted += r => result = r;

        // A field of five with a deliberate tie on 4, so both the ordering and the
        // tie-break have something to get wrong.
        hub.Roster[NRConst.HostPeerId].Score = 4;
        hub.Roster[NRConst.HostPeerId].DisplayName = "Host";
        hub.Roster[LoopbackHub.ClientPeerId].Score = 9;
        hub.Roster[LoopbackHub.ClientPeerId].DisplayName = "Client";
        hub.AddPlayer(3, "Third", score: 1);
        hub.AddPlayer(4, "Fourth", score: 4);
        hub.AddPlayer(5, "Fifth", score: 7);

        Advance(director, TuningLibrary.GameMode.TimeLimit + 1.0f);

        Check("a completed match produced a result", result is not null);
        if (result is null)
        {
            return;
        }

        var standings = result.Standings;

        Check($"every player is in the standings (got {standings.Count} of 5)", standings.Count == 5);
        if (standings.Count != 5)
        {
            return;
        }

        var scores = standings.Select(s => s.Score).ToList();
        Check(
            $"standings run highest score first (got [{string.Join(", ", scores)}])",
            scores.SequenceEqual(scores.OrderByDescending(s => s)));

        var placements = standings.Select(s => s.Placement).ToList();
        Check(
            $"placement is 1..n in order (got [{string.Join(", ", placements)}])",
            placements.SequenceEqual(Enumerable.Range(1, standings.Count)));

        Check(
            $"the winner is the highest scorer (got \"{standings[0].DisplayName}\" on {standings[0].Score})",
            standings[0].DisplayName == "Client" && standings[0].Score == 9);

        Check(
            $"last place is the lowest scorer (got \"{standings[^1].DisplayName}\" on {standings[^1].Score})",
            standings[^1].DisplayName == "Third" && standings[^1].Score == 1);

        // The tie must break on peer id, or two peers can disagree about who came third.
        var tied = standings.Where(s => s.Score == 4).Select(s => s.PeerId).ToList();
        Check(
            $"a tie breaks on peer id (got [{string.Join(", ", tied)}])",
            tied.SequenceEqual(tied.OrderBy(id => id)));

        // Names and ids have to survive together: a scoreboard that pairs the right score
        // with the wrong name is worse than one that fails outright.
        Check(
            "each standing keeps its own name and score",
            standings.All(s => hub.Roster[s.PeerId].DisplayName == s.DisplayName
                && hub.Roster[s.PeerId].Score == s.Score));
    }

    /// <summary>
    /// Checks the real <see cref="IMatchNetwork"/> implementations, not the test double.
    /// </summary>
    /// <remarks>
    /// <see cref="StandingsAreRankedAndPlaced"/> runs against <c>LoopbackNetwork</c>, which
    /// has its own copy of the ranking. That proves the director places correctly given a
    /// ranked list and proves nothing whatsoever about the implementations the game
    /// actually ships. <see cref="OfflineMatchNetwork"/> returned the roster completely
    /// unsorted from a method named <c>PlayersByScore</c>, which was survivable only
    /// because a solo session has one player in it.
    /// </remarks>
    private static void ProductionRankingsAreSorted()
    {
        var (hostParty, _) = LoopbackPartyService.CreatePair();
        using var host = new PartyMatchNetwork(
            hostParty,
            new PlayerState { PeerId = NetRumble.Platform.IPartyService.HostPeerId, DisplayName = "Host" });

        // A peer only enters the roster once it answers the host's identity request, so
        // announcing the join is not enough - the answer has to arrive too. Peer ids are
        // deliberately out of order.
        foreach (var peerId in (int[])[7, 2, 3])
        {
            hostParty.RaisePeerJoined(peerId);

            using var identity = new MessageWriter(MessageType.SubmitIdentity, 64);
            identity.WriteString($"Peer{peerId}");
            identity.WriteString(string.Empty);
            identity.WriteInt(0);
            identity.WriteInt(0);
            hostParty.Deliver(peerId, identity.Written);
        }

        // Without this the ranking checks below pass on a one-entry list and prove nothing.
        Check($"the host roster filled up (got {host.Players.Count} of 4)", host.Players.Count == 4);
        if (host.Players.Count != 4)
        {
            return;
        }

        // Scores assigned in roster-iteration order, so insertion order, peer order and
        // score order are three different orders and only one of them is correct.
        var assigned = 0;
        foreach (var player in host.Players.Values)
        {
            player.Score = assigned++;
        }

        var rankedScores = host.PlayersByScore().Select(p => p.Score).ToList();
        Check(
            $"PartyMatchNetwork ranks by score (got [{string.Join(", ", rankedScores)}])",
            rankedScores.SequenceEqual(rankedScores.OrderByDescending(s => s)));

        var byPeer = host.SortedPlayers().Select(p => p.PeerId).ToList();
        Check(
            $"PartyMatchNetwork sorts the roster by peer id (got [{string.Join(", ", byPeer)}])",
            byPeer.SequenceEqual(byPeer.OrderBy(id => id)));

        // Ties must break on peer id or two peers can disagree about the placement.
        foreach (var player in host.Players.Values)
        {
            player.Score = 3;
        }

        var tied = host.PlayersByScore().Select(p => p.PeerId).ToList();
        Check(
            $"PartyMatchNetwork breaks ties on peer id (got [{string.Join(", ", tied)}])",
            tied.SequenceEqual(tied.OrderBy(id => id)));

        var offline = new OfflineMatchNetwork(new PlayerState { DisplayName = "Solo" });
        Check(
            "OfflineMatchNetwork returns its single player ranked",
            offline.PlayersByScore().Count == 1
                && offline.PlayersByScore()[0].PeerId == NRConst.HostPeerId
                && offline.SortedPlayers().Count == 1);
    }

    /// <summary>
    /// A networked match that empties out ends rather than leaving one player alone.
    /// </summary>
    private static void LastPlayerStandingEndsTheMatch()
    {
        var (director, world, hub) = LiveMatch();
        var results = new List<MatchResult>();
        director.MatchCompleted += results.Add;

        Advance(director, 1.0f);
        Check("two players keep the match alive", !director.MatchState.HasMatchState(MatchState.MatchComplete));

        var departedShip = world.GetShipFor(LoopbackHub.ClientPeerId)!;
        var hostShip = world.GetShipFor(NRConst.HostPeerId)!;
        departedShip.UpdateRemoteInput(Vector2.One, Vector2.One, deployMine: true, sequence: 1);
        departedShip.AddCollisionException(hostShip);

        hub.RemovePlayer(LoopbackHub.ClientPeerId);
        Advance(director, 0.5f);

        Check("losing the last opponent completes the match", director.MatchState.HasMatchState(MatchState.MatchComplete));
        Check(
            $"the reason is last player standing (got {(results.Count > 0 ? results[0].Reason.ToString() : "none")})",
            results.Count > 0 && results[0].Reason == MatchEndReason.LastPlayerStanding);
        Check("the departed player's ship is removed from the world",
            world.GetShipFor(LoopbackHub.ClientPeerId) is null && !departedShip.IsActive);
        Check("the departed player's input state is cleared",
            departedShip.ShipInput.MovementDirection == Vector2.Zero
                && departedShip.ShipInput.FireDirection == Vector2.Zero
                && !departedShip.ShipInput.DeployMinePressed);
        Check("the departed player's interaction state is cleared",
            !departedShip.IsExemptFrom(hostShip) && !hostShip.IsExemptFrom(departedShip));

        // An offline session is a legitimate one-player game and must be exempt.
        var solo = SoloMatch();
        var soloEnded = false;
        solo.MatchCompleted += _ => soloEnded = true;
        Advance(solo, 2.0f);
        Check("a solo offline match is exempt from the rule", !soloEnded);
    }

    /// <summary>
    /// Both ends have to show the same results screen, which means the client must both
    /// receive the result and move into <see cref="MatchState.MatchComplete"/> itself.
    /// </summary>
    private static void HostAndClientAgreeOnTheResult()
    {
        var (director, _, hub) = LiveMatch();

        var clientDirector = new MatchDirector(hub.Client);
        var clientWorld = new World(new Random(99));
        clientDirector.Setup(clientWorld);

        MatchResult? hostResult = null;
        MatchResult? clientResult = null;
        director.MatchCompleted += r => hostResult = r;
        clientDirector.MatchCompleted += r => clientResult = r;

        hub.Roster[NRConst.HostPeerId].Score = 2;
        hub.Roster[LoopbackHub.ClientPeerId].Score = 6;

        Advance(director, TuningLibrary.GameMode.TimeLimit + 1.0f);

        Check("the host announced a result", hostResult is not null);
        Check("the client received a result", clientResult is not null);

        if (hostResult is null || clientResult is null)
        {
            return;
        }

        Check(
            $"both ends agree on the reason (host {hostResult.Reason}, client {clientResult.Reason})",
            hostResult.Reason == clientResult.Reason);

        Check(
            "both ends agree on the standings",
            hostResult.Standings.Count == clientResult.Standings.Count
                && hostResult.Standings.Zip(clientResult.Standings).All(pair =>
                    pair.First.PeerId == pair.Second.PeerId
                    && pair.First.Placement == pair.Second.Placement
                    && pair.First.Score == pair.Second.Score
                    && pair.First.DisplayName == pair.Second.DisplayName));

        // Without this the client sits in a live world while the host shows results.
        Check(
            "the client also enters MatchComplete",
            clientDirector.MatchState.HasMatchState(MatchState.MatchComplete));
    }

    /// <summary>
    /// Two win conditions can be satisfied on the same frame. The results screen must not
    /// be pushed twice, and the standings must not be rebuilt from a mutated roster.
    /// </summary>
    private static void CompletionIsIdempotent()
    {
        var (director, world, hub) = LiveMatch();
        var results = new List<MatchResult>();
        director.MatchCompleted += results.Add;

        hub.Roster[LoopbackHub.ClientPeerId].Score =
            TuningLibrary.GameMode.TargetScore;

        var hostShip = world.Ships.Values.First(s => s.OwnerPeerId == NRConst.HostPeerId);
        hostShip.Health = 0.0f;
        director.Tick(Step);

        Check($"the first completion announced once (got {results.Count})", results.Count == 1);

        // Now drop the opponent too, which is a second, independent win condition.
        hub.RemovePlayer(LoopbackHub.ClientPeerId);
        Advance(director, 2.0f);

        Check($"a second win condition announces nothing more (got {results.Count})", results.Count == 1);
    }

    /// <summary>
    /// The debug switch that makes a match short enough to finish unattended.
    /// </summary>
    /// <remarks>
    /// It mutates the shared tuning, so this runs last and puts the original limits back.
    /// A check that left a twenty-second time limit behind would quietly rewrite every
    /// later match in the process.
    /// </remarks>
    private static void TheTimeLimitOverrideApplies()
    {
        var original = TuningLibrary.GameMode.TimeLimit;

        try
        {
            TuningLibrary.OverrideMatchTimeLimit(20.0f);

            Check(
                "the override shortens the match",
                TuningLibrary.GameMode.TimeLimit == 20.0f);

            // A shortened match must still end for the right reason rather than tripping
            // some other condition on the way.
            var (director, _, _) = LiveMatch();
            MatchResult? result = null;
            director.MatchCompleted += r => result = r;

            Advance(director, 21.0f);

            Check("a shortened match completes on the clock", result?.Reason == MatchEndReason.TimeLimit);
            Check(
                $"the shortened duration is reported (got {result?.Elapsed:F0}s)",
                result is not null && result.Elapsed >= 20.0f && result.Elapsed < 21.0f);

            TuningLibrary.OverrideMatchTimeLimit(0.0f);
            Check(
                "a zero override is ignored rather than ending matches instantly",
                TuningLibrary.GameMode.TimeLimit == 20.0f);

            TuningLibrary.OverrideMatchTimeLimit(-5.0f);
            Check(
                "a negative override is ignored",
                TuningLibrary.GameMode.TimeLimit == 20.0f);
        }
        finally
        {
            TuningLibrary.GameMode.TimeLimit = original;
        }

        Check(
            "the original time limit was restored",
            TuningLibrary.GameMode.TimeLimit == original);
    }

    /// <summary>
    /// A two-peer match already past the countdown and running, which is where every win
    /// condition lives.
    /// </summary>
    private static (MatchDirector Director, World World, LoopbackHub Hub) LiveMatch()
    {
        GameObject.ResetIdCounter();

        var hub = new LoopbackHub();
        foreach (var player in hub.Roster.Values)
        {
            player.InGame = true;
        }

        var director = new MatchDirector(hub.Host);
        var world = new World(new Random(4321));
        director.Setup(world);

        director.Tick(Step);
        Advance(director, NRConst.SimulationDelayStarting);

        return (director, world, hub);
    }

    private static MatchDirector SoloMatch()
    {
        GameObject.ResetIdCounter();

        var network = new OfflineMatchNetwork(new PlayerState { DisplayName = "Solo" });
        var director = new MatchDirector(network);
        director.Setup(new World(new Random(1234)));

        director.Tick(Step);
        Advance(director, NRConst.SimulationDelayStarting);

        return director;
    }

    private static void Advance(MatchDirector director, float seconds)
    {
        var steps = (int)MathF.Ceiling(seconds / Step);
        for (var i = 0; i < steps; i++)
        {
            director.Tick(Step);
        }
    }

    private static void Check(string label, bool condition)
        => Console.WriteLine($"    {(condition ? "PASS" : "FAIL")} {label}");
}
