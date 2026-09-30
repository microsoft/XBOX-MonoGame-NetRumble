using System.Numerics;
using NetRumble.Core.Net;
using NetRumble.Core.Objects;
using NetRumble.Core.Tuning;
using NetRumble.Platform.Diagnostics;

namespace NetRumble.Core;

/// <summary>
/// Match orchestration, ported from <c>scripts/gameplay/match_director.gd</c> (itself a
/// port of <c>Game/Gameplay/GameState.h</c> + <c>GameStateServer.cpp</c>).
/// </summary>
/// <remarks>
/// <para>
/// Owns the <see cref="MatchState"/> machine, the ship respawn queue, scoring rules and
/// win conditions. <see cref="World"/> underneath stays a pure simulation and is driven
/// from here - the same split the C++ uses between <c>GameState*</c> and <c>World</c>.
/// </para>
/// <para>
/// The host runs the full state machine. Clients only mirror state pushed down by the
/// host through <see cref="IMatchNetwork"/>, exactly as <c>GameStateClient</c> does in
/// the original.
/// </para>
/// <para>
/// <b>Timers.</b> The GDScript used child <c>Timer</c> nodes for the loading grace
/// period, the start countdown and every pending respawn. Those are float countdowns
/// here. The ordering is load-bearing: Godot calls a parent's
/// <c>_physics_process</c> before its children process, so a timer that expires this
/// frame fires <em>after</em> the phase handler has already read its remaining time.
/// <see cref="Tick"/> preserves that by running the phase handler first and expiring
/// timers afterwards.
/// </para>
/// </remarks>
public sealed class MatchDirector : IDisposable
{
    private readonly IMatchNetwork _network;

    /// <summary>
    /// One countdown per destroyed ship, mirroring the C++ <c>ShipRespawn</c> queue.
    /// Keyed by ship id.
    /// </summary>
    private readonly Dictionary<int, float> _shipRespawns = [];

    private readonly List<Ship> _destroyedBuffer = [];
    private readonly List<int> _expiredRespawns = [];

    private bool _isAuthority;
    private bool _externallyPaused;
    private float _worldUpdateTimer;
    private float _clockBroadcastTimer;
    private int _lastCountdownBroadcast = -1;

    private PhaseTimer _loadingTimer;
    private PhaseTimer _startingTimer;

    /// <summary>Creates a director bound to a network session.</summary>
    /// <param name="network">Transport, or <see cref="OfflineMatchNetwork"/> for solo play.</param>
    public MatchDirector(IMatchNetwork network)
    {
        _network = network ?? throw new ArgumentNullException(nameof(network));

        _network.MatchStateChanged += OnRemoteMatchStateChanged;
        _network.CountdownChanged += OnRemoteCountdownChanged;
        _network.MatchClockReceived += OnRemoteMatchClock;
        _network.MatchCompletedReceived += OnRemoteMatchCompleted;
        _network.ShipInputReceived += OnShipInputReceived;
        _network.PlayerLeft += OnPlayerLeft;
    }

    /// <summary>Raised after <see cref="MatchState"/> changes, on every peer.</summary>
    public event Action<MatchState>? MatchStateChanged;

    /// <summary>Raised as a state is entered, after the simulation gate has been applied.</summary>
    public event Action<MatchState>? MatchStateEntered;

    /// <summary>Raised as a state is left, before <see cref="MatchState"/> is reassigned.</summary>
    public event Action<MatchState>? MatchStateExited;

    /// <summary>Raised when the whole-second countdown changes during the start phase.</summary>
    public event Action<int>? CountdownChanged;

    /// <summary>Raised when a player's score changes.</summary>
    public event Action<int, int>? ScoreChanged;

    /// <summary>
    /// Raised on every peer when the match ends. The stats write lives on
    /// this event rather than inside the director, because <c>NetRumble.Core</c> has no
    /// reference to the platform services. Both host and client raise it, which matches
    /// the GDScript calling <c>_report_result</c> down both paths.
    /// </summary>
    public event Action<MatchResult>? MatchCompleted;

    /// <summary>Raised when a player never finished loading and the match was abandoned.</summary>
    public event Action? MatchCanceled;

    /// <summary>The simulation this director drives.</summary>
    public World? World { get; private set; }

    /// <summary>Current phase of the match.</summary>
    public MatchState MatchState { get; private set; } = MatchState.Loading;

    /// <summary>Seconds the match has been running.</summary>
    public float ElapsedMatchTime { get; private set; }

    /// <summary>Seconds left on the pre-match countdown.</summary>
    public float StartingTimeRemaining { get; private set; }

    /// <summary>True once the match is live.</summary>
    public bool IsRunning => MatchState.HasMatchState(MatchState.Running);

    /// <summary>True while ships may be flown, which includes the pre-match phases.</summary>
    public bool IsPlayable => MatchState.HasMatchState(MatchState.Playable);

    /// <summary>Seconds left before the match's time limit is reached.</summary>
    public float TimeRemaining =>
        MathF.Max(TuningLibrary.GameMode.TimeLimit - ElapsedMatchTime, 0.0f);

    /// <summary>True while the platform has frozen the match from outside (XR-001).</summary>
    public bool IsExternallyPaused => _externallyPaused;

    /// <summary>
    /// Freezes or thaws the match from outside the state machine, for the platform
    /// constrain path (XR-001). The match keeps whatever phase it was in - this is a
    /// pause, not a state transition - so nothing here touches <see cref="MatchState"/>
    /// and no match state is broadcast to the other peers.
    /// </summary>
    public void SetExternallyPaused(bool paused)
    {
        if (_externallyPaused == paused)
        {
            return;
        }

        _externallyPaused = paused;
        ApplySimulationGate();
    }

    /// <summary>
    /// XR-001 requires a constrained title to pause, but a networked match is
    /// <i>shared</i>: a constrained host that stops simulating halts the match for every
    /// other player, and a constrained client stops relaying its input and falls behind.
    /// Online sessions therefore keep simulating - the lifecycle path still mutes audio
    /// for the constrain - and only offline play actually freezes.
    /// </summary>
    private bool ConstrainFreezesSimulation() => _network.IsOffline;

    /// <summary>Binds a world and starts the state machine.</summary>
    /// <param name="world">The simulation to drive.</param>
    public void Setup(World world)
    {
        ArgumentNullException.ThrowIfNull(world);

        World = world;
        _isAuthority = _network.IsHost;

        World.AttachNetwork(_network);
        World.Initialize(_isAuthority, _network.SortedPlayers());

        ResetTimers();

        // The world is announced to clients exactly once, from World.StartMatch, so every
        // peer builds its entities from a single authoritative layout.
        CrashLog.Mark(
            $"match: Setup peer={_network.LocalPeerId} host={_isAuthority} "
            + $"roster={_network.Players.Count}");
        SetMatchState(_isAuthority ? MatchState.PlayersJoining : MatchState.Loading);
    }

    /// <summary>
    /// Advances the match by one fixed step. Call this instead of ticking
    /// <see cref="World"/> directly - the director decides when the simulation runs.
    /// </summary>
    /// <param name="delta">Fixed timestep, in seconds.</param>
    public void Tick(float delta)
    {
        if (World is null)
        {
            return;
        }

        // Constrained *offline*: the world is already frozen by the simulation gate, but
        // the clocks, countdown and respawn logic live here and would otherwise keep
        // advancing behind the Guide and jump on return. An online match deliberately
        // keeps running - see ConstrainFreezesSimulation.
        if (_externallyPaused && ConstrainFreezesSimulation())
        {
            return;
        }

        if (!_isAuthority)
        {
            // Clients only tick the world so interpolation and local prediction advance;
            // all authoritative outcomes arrive via snapshots.
            World.Tick(delta);

            // The match clock is host-owned, but it has to keep moving locally between the
            // host's updates or the HUD timer sits frozen on the client.
            if (MatchState.HasMatchState(MatchState.Running))
            {
                ElapsedMatchTime += delta;
            }

            return;
        }

        if (!MatchState.HasMatchState(MatchState.MatchComplete))
        {
            TickShipDestroyedLogic();
        }

        if (MatchState.HasMatchState(MatchState.PlayersJoining))
        {
            HandlePlayersLoading(delta);
        }
        else if (MatchState.HasMatchState(MatchState.Starting))
        {
            HandleStarting();
        }
        else if (MatchState.HasMatchState(MatchState.Running))
        {
            HandleRunning(delta);
        }

        // Timers expire after the phase handlers, matching Godot's parent-before-children
        // physics processing order.
        TickPhaseTimers(delta);
        TickShipRespawns(delta);
    }

    // --- Match state ticks --------------------------------------------------

    private float _loadWaitLogTimer;

    private void HandlePlayersLoading(float delta)
    {
        // Players can keep flying while others finish loading.
        World!.Tick(delta);

        var everyoneLoaded = true;

        foreach (var player in _network.Players.Values)
        {
            if (!player.InGame)
            {
                everyoneLoaded = false;
                break;
            }
        }

        if (!everyoneLoaded)
        {
            // Breadcrumb for the 4-player "waiting for players" hang: this barrier
            // blocks the whole match on a single missing InGame flag, so logging which
            // peer id(s) are still missing - at most a few times a second, not per tick
            // - is what tells the next repro apart from a guess.
            _loadWaitLogTimer += delta;

            if (_loadWaitLogTimer >= 2.0f)
            {
                _loadWaitLogTimer = 0f;
                var missing = _network.Players.Values.Where(p => !p.InGame).Select(p => p.PeerId);
                CrashLog.Mark(
                    $"match: HandlePlayersLoading still waiting, missing InGame from peer(s) "
                    + $"[{string.Join(',', missing)}] of {_network.Players.Count}");
            }

            return;
        }

        CrashLog.Mark($"match: HandlePlayersLoading complete, {_network.Players.Count} players in game");

        // Single round setup: the world picks every spawn point once here, hands the
        // finished layout to the clients, and nothing is repositioned again until the
        // match is over. The countdown then runs on that final layout.
        World.StartMatch();
        SetMatchState(MatchState.Starting);
    }

    private void HandleStarting()
    {
        StartingTimeRemaining = _startingTimer.Remaining;
        BroadcastCountdown(StartingTimeRemaining);
    }

    private void HandleRunning(float delta)
    {
        ElapsedMatchTime += delta;
        World!.Tick(delta);

        _worldUpdateTimer += delta;
        if (_worldUpdateTimer >= 1.0f / NRConst.WorldSnapshotHz)
        {
            _worldUpdateTimer = 0.0f;
            World.BroadcastSnapshot();
        }

        _clockBroadcastTimer += delta;
        if (_clockBroadcastTimer >= 1.0f / NRConst.MatchClockHz)
        {
            _clockBroadcastTimer = 0.0f;
            _network.BroadcastMatchClock(ElapsedMatchTime);
        }

        CheckForMatchTimeLimitMet();
    }

    // --- Ship destruction, scoring and respawn ------------------------------

    private void TickShipDestroyedLogic()
    {
        _destroyedBuffer.Clear();

        foreach (var ship in World!.Ships.Values)
        {
            if (ship.IsActive && ship.Health <= 0.0f)
            {
                _destroyedBuffer.Add(ship);
            }
        }

        if (_destroyedBuffer.Count == 0)
        {
            return;
        }

        // Deterministic ordering so host and clients agree on scoring order.
        _destroyedBuffer.Sort(static (a, b) => a.UniqueId.CompareTo(b.UniqueId));

        var scoreDeaths = MatchState.HasMatchState(MatchState.Running);

        foreach (var ship in _destroyedBuffer)
        {
            ship.Die();
            _network.BroadcastShipDestroyed(new ShipDestroyedPayload(ship.UniqueId, ship.OwnerPeerId));

            if (scoreDeaths)
            {
                UpdateScore(ship);
            }
        }

        if (scoreDeaths)
        {
            CheckForMatchScoreMet();
        }

        foreach (var ship in _destroyedBuffer)
        {
            if (!MatchState.HasMatchState(MatchState.MatchComplete))
            {
                QueueShipRespawn(ship);
            }
        }
    }

    /// <summary>
    /// Ported from <c>GameStateServer::UpdateScore</c>. Killing yourself, or dying to
    /// anything that is not another player's projectile, costs a point instead of
    /// awarding one.
    /// </summary>
    private void UpdateScore(Ship ship)
    {
        Ship? killer = null;
        var damager = World!.GetGameObject(ship.LastDamagedById);

        if (damager is not null && damager.ObjectType == GameObjectType.Projectile)
        {
            killer = World.GetShipById(((Projectile)damager).OwnerId);
        }

        if (killer is not null && !ReferenceEquals(killer, ship))
        {
            ApplyScoreDelta(killer.OwnerPeerId, 1);
        }
        else
        {
            ApplyScoreDelta(ship.OwnerPeerId, -1);
        }
    }

    private void ApplyScoreDelta(int peerId, int delta)
    {
        if (!_network.Players.TryGetValue(peerId, out var state))
        {
            return;
        }

        // A negative score is never displayed in the original; deaths cannot push you
        // below zero.
        if (delta < 0 && state.Score <= 0)
        {
            return;
        }

        state.Score = Math.Max(state.Score + delta, 0);
        _network.BroadcastScoreUpdated(peerId, state.Score, delta);
        ScoreChanged?.Invoke(peerId, state.Score);
    }

    private void QueueShipRespawn(Ship ship) =>
        _shipRespawns[ship.UniqueId] = NRConst.ShipRespawnDelay;

    private void TickShipRespawns(float delta)
    {
        if (_shipRespawns.Count == 0)
        {
            return;
        }

        // Respawn countdowns are frozen between match phases, mirroring the GDScript
        // pausing each respawn Timer whenever the simulation gate closes. Without this a
        // player destroyed just before the match ends would respawn during the results
        // screen.
        if (!StateRunsSimulation())
        {
            return;
        }

        _expiredRespawns.Clear();

        foreach (var shipId in _shipRespawns.Keys)
        {
            var remaining = _shipRespawns[shipId] - delta;
            _shipRespawns[shipId] = remaining;

            if (remaining <= 0.0f)
            {
                _expiredRespawns.Add(shipId);
            }
        }

        foreach (var shipId in _expiredRespawns)
        {
            _shipRespawns.Remove(shipId);
            RespawnShip(shipId);
        }
    }

    private void RespawnShip(int shipId)
    {
        if (World is null || MatchState.HasMatchState(MatchState.MatchComplete))
        {
            return;
        }

        var ship = World.GetShipById(shipId);
        if (ship is null)
        {
            return;
        }

        var spawnPoint = World.FindSpawnPoint(ship.Radius);
        ship.Teleport(spawnPoint);
        ship.IsActive = true;
        ship.Start();

        _network.BroadcastShipSpawned(new ShipSpawnedPayload(ship.UniqueId, ship.OwnerPeerId, spawnPoint));
    }

    private void CancelShipRespawn(int shipId) => _shipRespawns.Remove(shipId);

    // --- Win conditions -----------------------------------------------------

    private void CheckForMatchTimeLimitMet()
    {
        if (ElapsedMatchTime >= TuningLibrary.GameMode.TimeLimit)
        {
            CompleteMatch(MatchEndReason.TimeLimit);
        }
    }

    private void CheckForMatchScoreMet()
    {
        var target = TuningLibrary.GameMode.TargetScore;

        foreach (var player in _network.Players.Values)
        {
            if (player.Score >= target)
            {
                CompleteMatch(MatchEndReason.ScoreLimit);
                return;
            }
        }
    }

    /// <summary>
    /// A match with a single player left has nobody to play against, so it ends rather
    /// than leaving the last player flying around an empty world. Offline play is exempt:
    /// it is a legitimate one-player session and never sees a peer leave anyway.
    /// </summary>
    private void CheckForLastPlayerStanding()
    {
        if (!_isAuthority || _network.IsOffline)
        {
            return;
        }

        if (MatchState.HasMatchState(MatchState.MatchComplete) || _network.Players.Count > 1)
        {
            return;
        }

        CompleteMatch(MatchEndReason.LastPlayerStanding);
    }

    /// <summary>
    /// A client only has a match for as long as the host is there to run it: every state
    /// transition, respawn and score comes down from the authority, so a client whose
    /// host has gone would otherwise keep flying around a world that nothing is driving.
    /// The host's own departure is the one peer loss a client has to act on itself -
    /// there is nobody left to broadcast the completion it would normally be told about.
    /// </summary>
    /// <remarks>
    /// A transport that also tears the connection down (the LAN one does) reaches the
    /// same end through the screen's connection-lost path; one that keeps the network up
    /// after the host leaves (PlayFab Party does) arrives here and nowhere else.
    /// </remarks>
    private void CheckForAuthorityLeft(int peerId)
    {
        if (_network.IsOffline || peerId != NRConst.HostPeerId)
        {
            return;
        }

        if (MatchState.HasMatchState(MatchState.MatchComplete))
        {
            return;
        }

        CompleteMatch(MatchEndReason.HostLeft);
    }

    private void CompleteMatch(MatchEndReason reason)
    {
        if (MatchState.HasMatchState(MatchState.MatchComplete))
        {
            return;
        }

        SetMatchState(MatchState.MatchComplete);

        var ranked = _network.PlayersByScore();
        var standings = new List<MatchStanding>(ranked.Count);

        for (var i = 0; i < ranked.Count; i++)
        {
            standings.Add(new MatchStanding(
                ranked[i].PeerId,
                ranked[i].DisplayName,
                ranked[i].Score,
                i + 1));
        }

        var result = new MatchResult(
            reason,
            TuningLibrary.GameMode.DisplayName,
            ElapsedMatchTime,
            standings);

        _network.BroadcastMatchCompleted(result);

        // BroadcastMatchCompleted only reaches clients, so the host announces its own
        // completion here. Without this the host sits in the finished world while every
        // client returns to the lobby.
        MatchCompleted?.Invoke(result);
    }

    // --- Input relay --------------------------------------------------------

    private void OnShipInputReceived(
        int peerId,
        Vector2 movement,
        Vector2 fire,
        bool deployMine,
        int sequence)
    {
        if (!_isAuthority || World is null)
        {
            return;
        }

        World.GetShipFor(peerId)?.UpdateRemoteInput(movement, fire, deployMine, sequence);
    }

    private void OnPlayerLeft(int peerId)
    {
        if (!_isAuthority)
        {
            CheckForAuthorityLeft(peerId);
            return;
        }

        if (World is null)
        {
            return;
        }

        var ship = World.GetShipFor(peerId);
        if (ship is not null)
        {
            // Purge any pending respawn so a player who leaves mid-respawn is not
            // resurrected.
            CancelShipRespawn(ship.UniqueId);
        }

        World.RemoveShipFor(peerId);
        CheckForLastPlayerStanding();
    }

    public void Dispose()
    {
        _network.MatchStateChanged -= OnRemoteMatchStateChanged;
        _network.CountdownChanged -= OnRemoteCountdownChanged;
        _network.MatchClockReceived -= OnRemoteMatchClock;
        _network.MatchCompletedReceived -= OnRemoteMatchCompleted;
        _network.ShipInputReceived -= OnShipInputReceived;
        _network.PlayerLeft -= OnPlayerLeft;
    }

    // --- State plumbing -----------------------------------------------------

    private void SetMatchState(MatchState state)
    {
        if (MatchState == state)
        {
            return;
        }

        CrashLog.Mark($"match: SetMatchState (host) peer={_network.LocalPeerId} {MatchState} -> {state}");

        var previous = MatchState;
        ExitMatchState(previous);
        MatchStateExited?.Invoke(previous);

        MatchState = state;
        ApplySimulationGate();

        if (_isAuthority)
        {
            _network.SetMatchState(state);
        }

        MatchStateChanged?.Invoke(state);
        EnterMatchState(state);
        MatchStateEntered?.Invoke(state);
    }

    private void EnterMatchState(MatchState state)
    {
        if (state.HasMatchState(MatchState.Running))
        {
            // Both peers restart the clock here; the host then keeps the client's copy
            // honest through BroadcastMatchClock.
            ElapsedMatchTime = 0.0f;
            _clockBroadcastTimer = 0.0f;
        }

        if (!_isAuthority)
        {
            return;
        }

        if (state.HasMatchState(MatchState.PlayersJoining))
        {
            _loadingTimer.Start(NRConst.SimulationDelayPlayersLoading);
        }
        else if (state.HasMatchState(MatchState.Starting))
        {
            StartingTimeRemaining = NRConst.SimulationDelayStarting;
            _startingTimer.Start(NRConst.SimulationDelayStarting);
            BroadcastCountdown(StartingTimeRemaining);
        }
    }

    private void ExitMatchState(MatchState state)
    {
        if (state.HasMatchState(MatchState.PlayersJoining))
        {
            _loadingTimer.Stop();
        }
        else if (state.HasMatchState(MatchState.Starting))
        {
            _startingTimer.Stop();
            StartingTimeRemaining = 0.0f;
        }
    }

    private void ResetTimers()
    {
        ElapsedMatchTime = 0.0f;
        StartingTimeRemaining = 0.0f;
        _worldUpdateTimer = 0.0f;
        _clockBroadcastTimer = 0.0f;
        _lastCountdownBroadcast = -1;

        _loadingTimer.Stop();
        _startingTimer.Stop();
        _shipRespawns.Clear();
    }

    private void TickPhaseTimers(float delta)
    {
        if (_loadingTimer.Tick(delta))
        {
            OnLoadingTimeout();
        }

        if (_startingTimer.Tick(delta))
        {
            OnStartingTimeout();
        }
    }

    private void OnLoadingTimeout()
    {
        if (!_isAuthority || !MatchState.HasMatchState(MatchState.PlayersJoining))
        {
            return;
        }

        // A player never finished loading. Move to a terminal state so this branch cannot
        // re-fire and spam the cancellation every frame.
        SetMatchState(MatchState.MatchComplete);
        MatchCanceled?.Invoke();
    }

    private void OnStartingTimeout()
    {
        if (!_isAuthority || !MatchState.HasMatchState(MatchState.Starting))
        {
            return;
        }

        // The ships are already sitting on their final spawn points; going live only has
        // to thaw the simulation, which ApplySimulationGate does.
        SetMatchState(MatchState.Running);
    }

    private bool StateRunsSimulation() =>
        MatchState.HasMatchState(MatchState.PlayersJoining)
        || MatchState.HasMatchState(MatchState.Running);

    /// <summary>
    /// The bodies are driven by the collision solver, so between match phases they have to
    /// be frozen explicitly rather than simply not being ticked.
    /// </summary>
    private void ApplySimulationGate()
    {
        if (World is null)
        {
            return;
        }

        // The constrain freeze outranks the match phase, but only offline: freezing one
        // peer of a live session desyncs it from the rest, and freezing the host stops the
        // match for everybody.
        if (_externallyPaused && ConstrainFreezesSimulation())
        {
            World.SetSimulationRunning(false);
            return;
        }

        if (!_isAuthority)
        {
            // Clients keep simulating so prediction and interpolation stay warm, but they
            // have to hold still through the start countdown as well - otherwise the local
            // ship drifts away from the host and snaps back on the first snapshot.
            World.SetSimulationRunning(!MatchState.HasMatchState(MatchState.Starting));
            return;
        }

        World.SetSimulationRunning(StateRunsSimulation());
    }

    private void OnRemoteCountdownChanged(int secondsRemaining)
    {
        if (!_isAuthority)
        {
            CountdownChanged?.Invoke(secondsRemaining);
        }
    }

    private void OnRemoteMatchClock(float elapsed)
    {
        if (!_isAuthority)
        {
            ElapsedMatchTime = elapsed;
        }
    }

    private void OnRemoteMatchStateChanged(MatchState state)
    {
        if (_isAuthority)
        {
            return;
        }

        CrashLog.Mark($"match: OnRemoteMatchStateChanged peer={_network.LocalPeerId} {MatchState} -> {state}");

        var previous = MatchState;
        ExitMatchState(previous);
        MatchStateExited?.Invoke(previous);

        MatchState = state;
        ApplySimulationGate();

        MatchStateChanged?.Invoke(state);
        EnterMatchState(state);
        MatchStateEntered?.Invoke(state);
    }

    private void OnRemoteMatchCompleted(MatchResult result)
    {
        if (_isAuthority)
        {
            return;
        }

        var previous = MatchState;
        ExitMatchState(previous);
        MatchStateExited?.Invoke(previous);

        MatchState = MatchState.MatchComplete;
        ApplySimulationGate();

        MatchCompleted?.Invoke(result);
        MatchStateEntered?.Invoke(MatchState);
    }

    private void BroadcastCountdown(float secondsRemaining)
    {
        var whole = (int)MathF.Ceiling(MathF.Max(secondsRemaining, 0.0f));
        if (whole == _lastCountdownBroadcast)
        {
            return;
        }

        _lastCountdownBroadcast = whole;
        _network.BroadcastCountdown(whole);
        CountdownChanged?.Invoke(whole);
    }

    /// <summary>
    /// A one-shot countdown replacing a Godot <c>Timer</c> node in
    /// <c>TIMER_PROCESS_PHYSICS</c> mode.
    /// </summary>
    /// <remarks>
    /// A struct so the director owns the storage outright and there is nothing to
    /// dispose - the GDScript had to <c>queue_free</c> every timer it made.
    /// </remarks>
    private struct PhaseTimer
    {
        private bool _running;

        /// <summary>Seconds left before this timer expires.</summary>
        public float Remaining { get; private set; }

        public void Start(float duration)
        {
            Remaining = duration;
            _running = true;
        }

        public void Stop()
        {
            Remaining = 0.0f;
            _running = false;
        }

        /// <summary>Advances the countdown, returning true on the frame it expires.</summary>
        public bool Tick(float delta)
        {
            if (!_running)
            {
                return false;
            }

            Remaining -= delta;
            if (Remaining > 0.0f)
            {
                return false;
            }

            // One-shot: stop before reporting, so a handler that restarts the timer is not
            // immediately overwritten.
            Stop();
            return true;
        }
    }
}
