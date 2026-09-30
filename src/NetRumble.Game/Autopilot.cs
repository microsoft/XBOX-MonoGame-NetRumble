using System.Numerics;
using Microsoft.Xna.Framework.Input;
using NetRumble.Core;
using NetRumble.Core.Net;
using NetRumble.Core.Objects;
using NetRumble.Game.UI;
using NetRumble.Game.UI.Screens;

namespace NetRumble.Game;

/// <summary>What an autopiloted instance is supposed to do.</summary>
internal enum AutopilotRole
{
    Host,
    Join,

    /// <summary>
    /// A single-process practice match against the AI. Exercises the bots, asteroid
    /// splitting and the lit bodies without needing a second instance or a socket.
    /// </summary>
    Practice,
}

/// <summary>
/// Drives an unattended instance of the game through a whole match.
/// </summary>
/// <remarks>
/// <para>
/// Every phase of this port has been verified headlessly: 300-odd spike checks over
/// services, codecs, reliability and replication, none of which ever ran the game. That
/// leaves the largest untested surface in the project - whether the pieces compose into
/// something playable - resting on the fact that each piece passes on its own. This is
/// what closes that gap: two processes, a real UDP transport between them, the real
/// screens, the real match loop, and no human.
/// </para>
/// <para>
/// <b>It drives the game, it does not replace it.</b> Screen transitions go through the
/// same <see cref="Screen.HandleAction"/> and <c>Screens.Push</c> calls the menus use,
/// and ship control is injected as a synthetic <see cref="KeyboardState"/> that flows
/// through <c>UiInput</c> and <c>LocalInput</c> exactly as a real keyboard would. A
/// harness that called <c>IPartyService</c> and <c>World</c> directly would prove the
/// netcode again and the game not at all - which is precisely the thing already proved.
/// </para>
/// <para>
/// <b>Debug developer switch.</b> It is opt-in via <c>--autopilot=</c>, absent by
/// default, and it force-quits the process when the run is done. Nothing here should
/// ever be reachable in a shipping build.
/// </para>
/// </remarks>
internal sealed class Autopilot
{
    private const string RolePrefix = "--autopilot=";
    private const string FilePrefix = "--autopilot-file=";
    private const string SecondsPrefix = "--autopilot-seconds=";

    /// <summary>
    /// How long to wait for the other instance before giving up.
    /// </summary>
    /// <remarks>
    /// An unattended run that hangs is worse than one that fails: it holds a machine and
    /// reports nothing. Every wait here is bounded and every timeout is logged as a
    /// failure with the stage it timed out in.
    /// </remarks>
    private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(45);

    /// <summary>How often the remote ship is sampled while a match is running.</summary>
    /// <remarks>
    /// Slow enough that a ship under thrust moves visibly between samples, so the
    /// movement test does not turn into a test of floating-point noise, and fast enough
    /// that a twelve-second match still yields dozens of them.
    /// </remarks>
    private const float SampleInterval = 0.25f;

    /// <summary>Displacement between samples that counts as the remote ship moving.</summary>
    /// <remarks>
    /// A replicated ship that is merely being extrapolated in place still jitters by
    /// fractions of a unit. This is above that and far below a thrusting ship's travel.
    /// </remarks>
    private const float MovedEpsilon = 0.5f;

    /// <summary>
    /// Displacement above which a sample is a teleport, not travel, and is not counted.
    /// </summary>
    /// <remarks>
    /// A spawn or a respawn relocates a ship instantly. Counting those would let a match
    /// whose netcode was dead still pass on the strength of two respawns, which is
    /// exactly the hole this check exists to close.
    /// </remarks>
    private const float TeleportThreshold = 400f;

    /// <summary>
    /// How many samples must show the remote ship travelling for replication to count as
    /// proved.
    /// </summary>
    /// <remarks>
    /// A count rather than a distance, on purpose: distance can be banked by a single
    /// large jump, whereas a count can only be reached by the remote ship moving over
    /// and over across the match, which is what a working stream of updates looks like.
    /// </remarks>
    private const int RequiredRemoteMoves = 8;

    private readonly AutopilotRole _role;
    private readonly string _rendezvousPath;
    private readonly float _playSeconds;
    private readonly Random _random = new(20260820);

    private Stage _stage = Stage.Booting;
    private float _elapsed;
    private float _stageEntered;
    private float _playStarted;
    private int _peers;
    private bool _readySent;
    private bool _failed;

    private int _remotePeerId;
    private int _localPeerId;
    private float _nextSampleAt;
    private Vector2? _lastRemotePosition;
    private Vector2? _lastLocalPosition;
    private int _remoteSamples;
    private int _remoteMoves;
    private int _remoteTeleports;
    private float _remoteTravel;
    private int _localMoves;
    private float _localTravel;
    private bool _remoteShipSeen;
    private bool _worldWasBuilt;
    private int _mostShipsSeen;

    /// <summary>
    /// How long to sit on the results before quitting, so an unattended run can be
    /// screenshotted showing them.
    /// </summary>
    private const float ResultsHoldSeconds = 4.0f;

    private MatchResult? _matchResult;
    private float _resultsSeenAt;
    private bool _completionHooked;

    private Autopilot(AutopilotRole role, string rendezvousPath, float playSeconds)
    {
        _role = role;
        _rendezvousPath = rendezvousPath;
        _playSeconds = playSeconds;
    }

    private enum Stage
    {
        Booting,
        Menu,
        Lobby,
        WaitingForPeer,
        Ready,
        Playing,
        Done,
    }

    /// <summary>True when the run finished having failed a stage.</summary>
    public bool Failed => _failed;

    /// <summary>
    /// Builds an autopilot from the command line, or null when not requested.
    /// </summary>
    /// <remarks>
    /// <c>--autopilot=host</c> or <c>--autopilot=join</c>. The two instances find each
    /// other through a rendezvous file rather than a hard-coded port: the host writes the
    /// connection string its transport actually bound, and the client waits for it. That
    /// avoids the classic flake where a fixed port is occupied and the client connects to
    /// something else entirely.
    /// </remarks>
    public static Autopilot? FromCommandLine(IReadOnlyList<string> args)
    {
        var role = default(AutopilotRole?);
        string? file = null;
        var seconds = 15f;

        foreach (var arg in args)
        {
            if (arg.StartsWith(RolePrefix, StringComparison.OrdinalIgnoreCase))
            {
                var value = arg[RolePrefix.Length..];
                if (Enum.TryParse<AutopilotRole>(value, ignoreCase: true, out var parsed))
                {
                    role = parsed;
                }
            }
            else if (arg.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase))
            {
                file = arg[FilePrefix.Length..];
            }
            else if (arg.StartsWith(SecondsPrefix, StringComparison.OrdinalIgnoreCase)
                && float.TryParse(
                    arg[SecondsPrefix.Length..],
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsedSeconds))
            {
                seconds = Math.Clamp(parsedSeconds, 1f, 600f);
            }
        }

        return role is null
            ? null
            : new Autopilot(
                role.Value,
                file ?? Path.Combine(Path.GetTempPath(), "netrumble-autopilot.txt"),
                seconds);
    }

    /// <summary>
    /// Replaces the real keyboard while a match is running.
    /// </summary>
    /// <remarks>
    /// Only during <see cref="Stage.Playing"/>. Synthesising keys in the menus would
    /// fight the deterministic screen transitions below, and the point of injecting at
    /// this level is to exercise the real input path - <c>UiInput</c>, then
    /// <c>LocalInput</c>'s A/D/W/S movement and arrow-key fire - rather than to poke
    /// <c>Ship</c> directly.
    /// </remarks>
    public KeyboardState Filter(KeyboardState real)
    {
        // Once the results are up the run stops driving the ship, so its synthetic keys
        // cannot dismiss the very dialog it is waiting to be seen.
        if (_stage != Stage.Playing || _matchResult is not null)
        {
            return real;
        }

        var keys = new List<Keys>(4);

        // Thrust most of the time and turn in slow sweeps, so the ships actually travel
        // and collide rather than jittering on the spot. Firing constantly is what makes
        // scores move, which is the thing worth observing in a short run.
        keys.Add(Keys.W);
        keys.Add(_random.Next(2) == 0 ? Keys.A : Keys.D);

        var aim = _random.Next(4);
        keys.Add(aim switch
        {
            0 => Keys.Left,
            1 => Keys.Right,
            2 => Keys.Up,
            _ => Keys.Down,
        });

        return new KeyboardState(keys.ToArray());
    }

    /// <summary>Advances the plan. Called once per frame, after the screens update.</summary>
    public void Update(UiContext context)
    {
        _elapsed += context.Delta;

        switch (_stage)
        {
            case Stage.Booting:
                Booting(context);
                break;
            case Stage.Menu:
                Menu(context);
                break;
            case Stage.Lobby:
                Lobby(context);
                break;
            case Stage.WaitingForPeer:
                WaitingForPeer(context);
                break;
            case Stage.Ready:
                Ready(context);
                break;
            case Stage.Playing:
                Playing(context);
                break;
        }
    }

    // --- Stages ------------------------------------------------------------

    /// <summary>
    /// Skips the acquire-user screen.
    /// </summary>
    /// <remarks>
    /// Going straight to the main menu is the same thing that screen's "Continue Offline"
    /// button does. There is no account to sign in to on a dev box, and waiting for a
    /// sign-in that cannot succeed would burn the whole run in the first screen.
    /// </remarks>
    private void Booting(UiContext context)
    {
        // A short settle first: the acquire-user screen kicks off a silent sign-in on
        // load, and replacing it mid-flight would tear down a task still running.
        if (_elapsed < 2f)
        {
            return;
        }

        Log($"role={_role} transport={context.Platform.Name}");

        if (context.Screens.Current is not MainMenuScreen)
        {
            context.Screens.ReplaceAll(new MainMenuScreen());
        }

        Enter(Stage.Menu);
    }

    private void Menu(UiContext context)
    {
        if (_elapsed - _stageEntered < 0.5f)
        {
            return;
        }

        var party = context.Platform.Party;
        party.PeerJoined += OnPeerJoined;
        party.PeerLeft += OnPeerLeft;

        if (_role == AutopilotRole.Practice)
        {
            // Practice stands its session up synchronously and has nobody to wait for,
            // so it skips straight past the transport and peer stages.
            context.Screens.Push(new LobbyScreen(LobbyIntent.Practice));
            Enter(Stage.Ready);
            return;
        }

        if (_role == AutopilotRole.Host)
        {
            // Clear any string left by a previous run before hosting, so a client that
            // starts early cannot connect to a stale address and then sit waiting.
            TryDelete(_rendezvousPath);
            context.Screens.Push(new LobbyScreen(LobbyIntent.Host));
            Enter(Stage.Lobby);
            return;
        }

        var connectionString = TryReadRendezvous();
        if (connectionString is null)
        {
            if (_elapsed > PeerTimeout.TotalSeconds)
            {
                Fail("no host connection string appeared at " + _rendezvousPath);
            }

            return;
        }

        Log($"joining {connectionString}");

        // The invite constructor, reused exactly. A LAN connection string is joinable in
        // the same way an invite's is, so the client needs no join-code UI at all.
        context.Screens.Push(new LobbyScreen(LobbyIntent.Join, connectionString));
        Enter(Stage.Lobby);
    }

    /// <summary>Waits for the transport to come up, then publishes the address if hosting.</summary>
    private void Lobby(UiContext context)
    {
        var party = context.Platform.Party;

        if (!party.HasNetwork)
        {
            if (_elapsed - _stageEntered > PeerTimeout.TotalSeconds)
            {
                Fail($"{_role} never established a network");
            }

            return;
        }

        if (_role == AutopilotRole.Host)
        {
            Log($"hosting: joinCode={party.JoinCode} connection={party.ConnectionString}");

            // Written only once the socket is genuinely bound, so the address in the file
            // is one the client can actually reach.
            File.WriteAllText(_rendezvousPath, party.ConnectionString);
        }
        else
        {
            Log($"joined: peerId={party.LocalPeerId}");
        }

        _localPeerId = party.LocalPeerId;
        Enter(Stage.WaitingForPeer);
    }

    /// <summary>
    /// Holds until both instances are present.
    /// </summary>
    /// <remarks>
    /// Readying early is the one thing that would quietly ruin the run: the match starts
    /// when everyone present is ready, so a host that readies alone starts a one-player
    /// match and the client arrives to find it already going.
    /// </remarks>
    private void WaitingForPeer(UiContext context)
    {
        if (_peers > 0)
        {
            Log($"peer connected (peers={_peers})");
            Enter(Stage.Ready);
            return;
        }

        if (_elapsed - _stageEntered > PeerTimeout.TotalSeconds)
        {
            Fail($"{_role} never saw the other instance connect");
        }
    }

    private void Ready(UiContext context)
    {
        // A beat after the peer appears, so both rosters have the other player in them
        // before either readies. Practice has no peer, but still needs the lobby to have
        // finished building its roster before the ready action lands.
        if (_elapsed - _stageEntered < 2f)
        {
            return;
        }

        if (!_readySent)
        {
            // The same action the space bar raises, delivered to the same handler.
            if (context.Screens.Current?.HandleAction(context, MenuAction.ToggleReady) == true)
            {
                _readySent = true;
                Log("ready sent");
            }

            return;
        }

        if (context.Screens.Current is GameplayScreen)
        {
            Log("match started");
            _playStarted = _elapsed;
            Enter(Stage.Playing);
            return;
        }

        if (_elapsed - _stageEntered > PeerTimeout.TotalSeconds)
        {
            Fail("the match never started after both players readied");
        }
    }

    private void Playing(UiContext context)
    {
        if (context.Screens.Current is GameplayScreen gameplay)
        {
            HookCompletion(gameplay);
            SampleBotField(gameplay);
            SampleRemoteShip(gameplay);

            if (_elapsed - _playStarted < _playSeconds)
            {
                return;
            }

            Conclude(context, gameplay.Match);
            return;
        }

        // The gameplay screen is no longer on top. Either the match ended on its own
        // terms - in which case the results dialog is what replaced it - or this instance
        // left for some other reason.
        if (_matchResult is not null)
        {
            if (_resultsSeenAt <= 0.0f)
            {
                _resultsSeenAt = _elapsed;
                ReportResults(_matchResult);
            }

            // Hold so the results are actually on screen long enough to be captured.
            if (_elapsed - _resultsSeenAt < ResultsHoldSeconds)
            {
                return;
            }

            Conclude(context, match: null);
            return;
        }

        // Whichever instance started first also finishes first, and its departure
        // ends the match here. That is this run reaching its end, not failing - but
        // only if the evidence is already in, so a match that collapsed early still
        // fails rather than being excused by the peer having gone.
        if (HasReplicationEvidence)
        {
            Log("the peer finished first; concluding on the evidence already gathered");
            Conclude(context, match: null);
        }
        else
        {
            Fail("left the match unexpectedly");
        }
    }

    /// <summary>
    /// Listens for the match ending, once the session exists.
    /// </summary>
    /// <remarks>
    /// Without this the run could not tell a match that finished properly from one it
    /// fell out of: both look identical from the outside, because the results are a
    /// dialog and a dialog replaces the gameplay screen as the current one. That
    /// ambiguity was being resolved the wrong way, reporting a completed match as the
    /// peer having quit first.
    /// </remarks>
    private void HookCompletion(GameplayScreen gameplay)
    {
        if (_completionHooked || gameplay.Match is not { } match)
        {
            return;
        }

        _completionHooked = true;
        match.Director.MatchCompleted += result => _matchResult = result;
    }

    /// <summary>
    /// Checks the standings the results screen is about to show. A match can end
    /// correctly and still produce a scoreboard that is wrong.
    /// </summary>
    private void ReportResults(MatchResult result)
    {
        var standings = string.Join(
            ", ",
            result.Standings.Select(s => $"{s.Placement}. {s.DisplayName} {s.Score}"));

        Log($"match completed: reason={result.Reason} elapsed={result.Elapsed:F0}s " +
            $"standings=[{standings}]");

        if (result.Standings.Count == 0)
        {
            Fail("the match completed with no standings to show");
            return;
        }

        var placements = result.Standings.Select(s => s.Placement).ToList();
        if (!placements.SequenceEqual(Enumerable.Range(1, placements.Count)))
        {
            Fail($"the standings are not placed 1..n (got [{string.Join(", ", placements)}])");
            return;
        }

        var scores = result.Standings.Select(s => s.Score).ToList();
        if (!scores.SequenceEqual(scores.OrderByDescending(s => s)))
        {
            Fail($"the standings are not ranked by score (got [{string.Join(", ", scores)}])");
        }
    }

    /// <summary>Reports what the run saw and decides whether it proved anything.</summary>
    private void Conclude(UiContext context, Gameplay.MatchSession? match)
    {
        var party = context.Platform.Party;

        // No score here on purpose: IPartyService carries the transport, not the roster,
        // and printing a placeholder would be worse than printing nothing.
        Log($"played {_elapsed - _playStarted:F0}s: peers={_peers} hasNetwork={party.HasNetwork} " +
            $"localPeerId={party.LocalPeerId}");

        Log($"replication: remotePeer={_remotePeerId} samples={_remoteSamples} " +
            $"moves={_remoteMoves} teleports={_remoteTeleports} travel={_remoteTravel:F0} " +
            $"| local moves={_localMoves} travel={_localTravel:F0} " +
            $"| matchState={match?.Director.MatchState} " +
            $"built={match?.World.IsBuilt} ships={match?.World.Ships.Count}");

        if (_role == AutopilotRole.Practice)
        {
            if (match is { CameraStartedOnLocalShip: false })
            {
                Fail("the camera never centered on the local ship");
                return;
            }

            // Practice has no transport and no peer, so every replication assertion below
            // is meaningless. What it proves instead is that the AI drove its ships: the
            // bot roster, the bot controllers and the match loop all had to work for the
            // world to hold more ships than the one human.
            //
            // This reads what was recorded while the match was on screen rather than
            // sampling the world here, because a practice match that reaches its score
            // limit early ends on its own terms: the results dialog replaces the gameplay
            // screen and `match` arrives null. Sampling at this point turned a match that
            // ran correctly to completion into a failure, and did so only under load,
            // when the bots had time to pull ahead before the run's clock expired.
            if (!_worldWasBuilt || _mostShipsSeen < 2)
            {
                Fail($"the practice match never stood up a bot field (built={_worldWasBuilt} ships={_mostShipsSeen})");
                return;
            }

            Log($"PASS played a practice match against {_mostShipsSeen - 1} bots end to end");
            Finish(context);
            return;
        }

        if (!party.HasNetwork && !HasReplicationEvidence)
        {
            Fail("the network dropped during the match");
            return;
        }

        // The check this run existed without for its first outing. Everything above is
        // satisfied by a match that connects, starts and never crashes - which a build
        // with entirely dead replication also does, because each instance happily
        // simulates its own ship and reports PASS. What separates the two is whether the
        // *other* player's ship moved on this screen, so that is what is asserted.
        if (!_remoteShipSeen)
        {
            Fail($"peer {_remotePeerId} never had a ship in this instance's world");
            return;
        }

        if (_remoteMoves < RequiredRemoteMoves)
        {
            Fail(
                $"the remote ship moved in only {_remoteMoves} of {_remoteSamples} samples " +
                $"(needs {RequiredRemoteMoves}) - replication did not progress");
            return;
        }

        Log("PASS reached and played a networked match end to end");
        Finish(context);
    }

    /// <summary>True once the peer's ship has been seen moving often enough to count.</summary>
    private bool HasReplicationEvidence => _remoteShipSeen && _remoteMoves >= RequiredRemoteMoves;

    /// <summary>
    /// Records whether the peer's ship is moving in this instance's world.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reads the world rather than the wire deliberately. Counting packets would prove
    /// bytes arrived; heartbeats arrive too. What a player would call working netcode is
    /// that the other ship moves, and the world is the only place that is visible.
    /// </para>
    /// <para>
    /// It means something slightly different at each end, and is worth having at both. On
    /// a client the remote ship moves because snapshots from the host are being applied.
    /// On the host it moves because the client's inputs arrived and the authoritative
    /// simulation acted on them - so the host's copy of the check covers the upstream
    /// direction that snapshots alone would not.
    /// </para>
    /// </remarks>
    private void SampleBotField(GameplayScreen gameplay)
    {
        var world = gameplay.Match?.World;

        if (world is null)
        {
            return;
        }

        _worldWasBuilt |= world.IsBuilt;
        _mostShipsSeen = Math.Max(_mostShipsSeen, world.Ships.Count);
    }

    private void SampleRemoteShip(GameplayScreen gameplay)
    {
        if (_elapsed < _nextSampleAt || _remotePeerId == 0)
        {
            return;
        }

        _nextSampleAt = _elapsed + SampleInterval;

        var world = gameplay.Match?.World;

        if (world is null)
        {
            return;
        }

        // Measured alongside the remote ship so a failure says which kind it is. A run
        // where neither ship moves is a simulation that never started; one where only the
        // remote ship is still is a simulation running on data that is not arriving.
        SampleShip(world.GetShipFor(_localPeerId), ref _lastLocalPosition, ref _localMoves, ref _localTravel);

        var ship = world.GetShipFor(_remotePeerId);

        if (ship is null)
        {
            // Between death and respawn there is no ship to measure. Drop the anchor so
            // the reappearance is not read as one enormous step.
            _lastRemotePosition = null;
            return;
        }

        _remoteShipSeen = true;
        var position = ship.Position;

        if (_lastRemotePosition is not { } previous)
        {
            _lastRemotePosition = position;
            return;
        }

        _lastRemotePosition = position;
        _remoteSamples++;
        var step = Vector2.Distance(previous, position);

        if (step > TeleportThreshold)
        {
            _remoteTeleports++;
            return;
        }

        if (step > MovedEpsilon)
        {
            _remoteMoves++;
            _remoteTravel += step;
        }
    }

    /// <summary>Accumulates one ship's travel between samples, ignoring teleports.</summary>
    private static void SampleShip(Ship? ship, ref Vector2? last, ref int moves, ref float travel)
    {
        if (ship is null)
        {
            last = null;
            return;
        }

        var position = ship.Position;

        if (last is not { } previous)
        {
            last = position;
            return;
        }

        last = position;
        var step = Vector2.Distance(previous, position);

        if (step > MovedEpsilon && step <= TeleportThreshold)
        {
            moves++;
            travel += step;
        }
    }

    // --- Plumbing ----------------------------------------------------------

    private void OnPeerJoined(int peerId)
    {
        _peers++;

        // Remembered so the match can be checked against the peer that is actually there
        // rather than a guessed id. Symmetric on both ends: the host learns the client's
        // id here, and the client learns the host's, because IPartyService.PeerJoined
        // fires on both.
        _remotePeerId = peerId;
        Log($"peer joined: {peerId}");
    }

    private void OnPeerLeft(int peerId)
    {
        _peers--;
        Log($"peer left: {peerId}");
    }

    private string? TryReadRendezvous()
    {
        try
        {
            if (!File.Exists(_rendezvousPath))
            {
                return null;
            }

            var text = File.ReadAllText(_rendezvousPath).Trim();
            return text.Length == 0 ? null : text;
        }
        catch (IOException)
        {
            // The host is mid-write. Try again next frame rather than treating a torn
            // read as a missing host.
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best effort. A stale file only matters to the client, which validates the
            // address by failing to connect to it.
        }
    }

    private void Enter(Stage stage)
    {
        _stage = stage;
        _stageEntered = _elapsed;
    }

    private void Fail(string reason)
    {
        _failed = true;
        Log($"FAIL {reason}");
        _stage = Stage.Done;

        // Quit rather than idle. An unattended run that hangs after a failure holds the
        // machine and still has to be killed by hand.
        Environment.Exit(1);
    }

    private void Finish(UiContext context)
    {
        _stage = Stage.Done;
        context.Game.Exit();
    }

    private void Log(string message)
        => Console.WriteLine(
            $"[autopilot {_role} {_elapsed,6:F1}s] {message}");
}
