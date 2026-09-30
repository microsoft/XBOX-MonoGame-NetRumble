using NetRumble.Core;
using NetRumble.Core.Net;
using NetRumble.Core.Tuning;
using NetRumble.Game.Profile;

namespace NetRumble.Game.Gameplay;

/// <summary>
/// Watches one match for the conditions behind this title's achievements and hands them
/// to <see cref="AchievementTracker"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything here is observable from a client.</b> That constraint drives the whole
/// design, because most of the obvious signals are authority-only:
/// <see cref="World.ShipDestroyed"/> is raised inside a block a client returns from, and
/// <see cref="MatchDirector.ScoreChanged"/> only fires where scoring is decided. A player
/// who joins someone else's match would earn nothing at all if those were the hooks. So
/// deaths and kills are read from replicated state that every peer already has - the
/// local ship's liveness and the local player's score - and the two signals that are not
/// replicated at all (which weapon was fired, which power-up was picked up) are raised
/// down both paths by <see cref="World.LocalWeaponFired"/> and
/// <see cref="World.LocalPowerUpCollected"/>.
/// </para>
/// <para>
/// <b>Why score stands in for a kill.</b> <c>MatchDirector.UpdateScore</c> awards exactly
/// +1 to the killer and never more, and takes a point off for a death or a suicide. A
/// positive delta on the local player is therefore one kill by the local player, and is
/// the only kill signal that reaches a non-host peer.
/// </para>
/// </remarks>
public sealed class MatchAchievementWatcher : IDisposable
{
    private readonly AchievementTracker _tracker;
    private readonly IMatchNetwork _network;
    private readonly MatchDirector _director;
    private readonly World _world;

    private int _lastScore;
    private bool _wasAlive;
    private bool _wasDestroyed;

    public MatchAchievementWatcher(AchievementTracker tracker, MatchSession match, IMatchNetwork network)
    {
        ArgumentNullException.ThrowIfNull(match);

        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _network = network ?? throw new ArgumentNullException(nameof(network));
        _director = match.Director;
        _world = match.World;

        _lastScore = network.LocalPlayer?.Score ?? 0;

        _world.LocalWeaponFired += OnLocalWeaponFired;
        _world.LocalPowerUpCollected += OnLocalPowerUpCollected;
        _world.LocalAsteroidDestroyed += OnLocalAsteroidDestroyed;
    }

    /// <summary>True when the local player has not been destroyed this match.</summary>
    public bool Survived => !_wasDestroyed;

    /// <summary>
    /// Samples the replicated state this frame. Called from the gameplay screen's update,
    /// after the session has ticked.
    /// </summary>
    public void Update()
    {
        // Deaths and scoring are both gated on Running in the director, so sampling
        // outside it would count the pre-match reset as a death and the post-match
        // roster as a kill.
        if (!_director.IsRunning)
        {
            _wasAlive = IsLocalShipAlive();
            _lastScore = _network.LocalPlayer?.Score ?? _lastScore;
            return;
        }

        TrackDeath();
        TrackKills();
    }

    /// <summary>Reports the end-of-match achievements. Safe to call once per match.</summary>
    public void OnMatchCompleted(MatchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var standing = result.StandingFor(_network.LocalPeerId);

        if (standing is null)
        {
            // The local player is not in the standings, so this was not their match to
            // have finished - nothing here applies to them.
            return;
        }

        // "Against human opponents" in the achievement list's own words. An offline
        // practice match has one standing and no transport; either alone disqualifies it.
        var againstHumans = !_network.IsOffline && result.Standings.Count > 1;

        _tracker.ReportMatchFinished(
            won: standing.Value.Placement == 1,
            survived: Survived,
            againstHumans: againstHumans);
    }

    public void Dispose()
    {
        _world.LocalWeaponFired -= OnLocalWeaponFired;
        _world.LocalPowerUpCollected -= OnLocalPowerUpCollected;
        _world.LocalAsteroidDestroyed -= OnLocalAsteroidDestroyed;
    }

    private void OnLocalWeaponFired(WeaponType weapon) => _tracker.ReportWeaponFired(weapon);

    /// <summary>
    /// Only a buff pickup advances "Fully Buffed". Weapon and restore drops are the bulk
    /// of the table, and counting them would make the achievement track the drop roll
    /// rather than the buffs it is named for.
    /// </summary>
    private void OnLocalPowerUpCollected(PowerUpType powerUp)
    {
        var definition = PickupLibrary.Get(powerUp);

        if (definition.Kind == PickupKind.Buff)
        {
            _tracker.ReportBuffCollected(definition.BuffGranted);
        }
    }

    private void OnLocalAsteroidDestroyed() => _tracker.ReportAsteroidDestroyed();

    /// <summary>
    /// A ship is destroyed by <c>Ship.Die()</c>, which every peer runs - the host from its
    /// own detection, a client from the host's message - so the alive-to-dead edge is
    /// visible everywhere. Respawn flips it back, hence an edge rather than a level.
    /// </summary>
    private void TrackDeath()
    {
        var alive = IsLocalShipAlive();

        if (_wasAlive && !alive)
        {
            _wasDestroyed = true;
            _tracker.ReportDeath();
        }

        _wasAlive = alive;
    }

    private void TrackKills()
    {
        var score = _network.LocalPlayer?.Score;

        if (score is null)
        {
            return;
        }

        // A death costs a point and clamps at zero, so a fall is not a kill and the
        // baseline simply follows it down.
        for (var i = _lastScore; i < score.Value; i++)
        {
            _tracker.ReportKill();
        }

        _lastScore = score.Value;
    }

    private bool IsLocalShipAlive() => _world.LocalShip is { IsActive: true, Health: > 0.0f };
}
