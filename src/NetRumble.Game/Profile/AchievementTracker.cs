using NetRumble.Core;
using NetRumble.Platform;
using NetRumble.Platform.Diagnostics;

namespace NetRumble.Game.Profile;

/// <summary>
/// Maps gameplay to this title's ten achievements and reports them through
/// <see cref="IAchievementService"/>.
/// </summary>
/// <remarks>
/// <para>
/// The ids are the title's real ones, from the Godot repo's
/// <c>docs/achievements2017.xml</c>. They resolve against the service configuration
/// published for this title's TitleId - see the comment in
/// <c>packaging/MicrosoftGame.config</c> and the SCID in
/// <c>packaging/xboxservices.config</c>. Achievement ids 1-10 must exist in that service
/// configuration, or every unlock fails where nothing reports it.
/// </para>
/// <para>
/// <b>Where the conditions are detected.</b> <see cref="Gameplay.MatchAchievementWatcher"/>
/// owns that; this class owns what to do about it. The split matters because the watcher's
/// problem is "what can a client observe that a host can", and this one's is "what has
/// this player earned across every session" - which is <see cref="AchievementStats"/> and
/// the save system.
/// </para>
/// <para>
/// <b>Best-effort throughout.</b> Nothing here surfaces an error: a missed achievement
/// must never interrupt a match. A report that does not succeed releases its dedupe slot,
/// so a player who starts offline and signs in later still gets credited for what they
/// have already done.
/// </para>
/// </remarks>
public sealed class AchievementTracker
{
    /// <summary>Shakedown Cruise - finish your first match.</summary>
    public const string FirstMatchFinished = "1";

    /// <summary>Space Debris - get destroyed for the first time.</summary>
    public const string FirstDeath = "2";

    /// <summary>First Blood - score your first kill.</summary>
    public const string FirstKill = "3";

    /// <summary>Rumble Champion - win a match.</summary>
    public const string MatchWon = "4";

    /// <summary>Untouchable - win a match without being destroyed.</summary>
    public const string UntouchableWin = "5";

    /// <summary>Arms Dealer - fire every weapon in the game at least once.</summary>
    public const string ArmsDealer = "6";

    /// <summary>Fully Buffed - collect every buff power-up at least once.</summary>
    public const string FullyBuffed = "7";

    /// <summary>Rock Breaker - destroy 250 asteroids.</summary>
    public const string RockBreaker = "8";

    /// <summary>Full House - complete a match in every game mode against human opponents.</summary>
    public const string FullHouse = "9";

    /// <summary>Centurion - destroy 100 enemy ships.</summary>
    public const string Centurion = "10";

    /// <summary>The target for <see cref="Centurion"/>.</summary>
    private const int CenturionTarget = 100;

    /// <summary>The target for <see cref="RockBreaker"/>.</summary>
    private const int RockBreakerTarget = 250;

    private readonly IPlatformProvider _platform;
    private readonly IGameSaveService _saves;
    private readonly object _gate = new();
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _lastPercent = new(StringComparer.Ordinal);

    private AchievementStats _stats = new();
    private bool _ownerless;

    public AchievementTracker(IPlatformProvider platform, IGameSaveService saves)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _saves = saves ?? throw new ArgumentNullException(nameof(saves));
    }

    /// <summary>All-time progress. The report methods below maintain it.</summary>
    public AchievementStats Stats => _stats;

    /// <summary>
    /// Loads the local stats. Called during boot, before sign-in, because the local tier
    /// is the one that always works.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        _stats = await AchievementStats.LoadLocalAsync(_saves, cancellationToken);
        _ownerless = false;
    }

    /// <summary>
    /// Merges this account's own stats in and re-reports progress. Called once sign-in
    /// completes - which is both when a cloud tier first becomes usable and when the
    /// achievement service is first able to accept anything at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Starts from empty, not from what boot loaded (XR-052).</b> The boot-time
    /// <see cref="LoadAsync"/> ran before anyone was signed in, so whatever it read
    /// belongs to the unsigned session and not to the account that has just arrived. This
    /// used to merge the signed-in user's bucket on top of those retained counters, which
    /// meant a stranger's kills and asteroids were reported to the service as this
    /// player's and then flushed into this player's own bucket - the leak test 052-05
    /// Correct User Association looks for. Discarding <c>_stats</c> first is what closes
    /// it; scoping the save path alone does not.
    /// </para>
    /// <para>
    /// The dedupe state goes with it. Those sets record what has already been reported
    /// for a player who is not this one.
    /// </para>
    /// </remarks>
    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _reported.Clear();
            _lastPercent.Clear();
        }

        _stats = new AchievementStats();
        _ownerless = false;

        // Now that the save tier can resolve this account's own bucket, this read is the
        // account's real local history and the only thing _stats is seeded from.
        _stats.MergeFrom(await AchievementStats.LoadLocalAsync(_saves, cancellationToken));

        await _stats.SyncCloudAsync(_saves, cancellationToken);

        // Progress earned while signed out only ever reached the disk. Re-reporting
        // pushes it to the service now that there is somebody to credit it to.
        ReportProgressAchievements();
        ReportPendingBinaryAchievements();
    }

    /// <summary>
    /// Re-attempts every report the service has not confirmed (XR-055).
    /// </summary>
    /// <remarks>
    /// The third drain point alongside sign-in and the gameplay events themselves, for the
    /// two occasions where nothing in the game has changed but the service's willingness to
    /// listen has: coming back from a suspend, and connectivity being restored. Cheap and
    /// safe to call at any time - both report paths drop anything already confirmed, and
    /// <see cref="IAchievementService"/> is idempotent besides.
    /// </remarks>
    public void RetryPending()
    {
        if (_ownerless)
        {
            return;
        }

        ReportProgressAchievements();
        ReportPendingBinaryAchievements();
    }

    /// <summary>Writes stats to both tiers if anything has changed.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_stats.IsDirty && !_ownerless)
        {
            await _stats.SaveAsync(_saves, cancellationToken);
        }
    }

    /// <summary>
    /// Writes the local tier and waits for it, for callers with no later frame to be
    /// continued on - a suspend, or the signed-in user being removed.
    /// </summary>
    public void CommitLocal()
    {
        if (_stats.IsDirty && !_ownerless)
        {
            _stats.SaveLocalNow(_saves);
        }
    }

    /// <summary>
    /// Drops all progress held in memory, for use when the account it belongs to has gone
    /// away (XR-052). Call <see cref="CommitLocal"/> first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The counters are the sharp half. Leaving them in place would carry the departed
    /// player's kills and asteroids into the next account and unlock against it - not a
    /// privacy leak but a false unlock, which is worse, because the service records it
    /// permanently. The dedupe sets go with them: they say what has already been reported
    /// for a player who is no longer here.
    /// </para>
    /// <para>
    /// <b>This also stops the tracker persisting anything</b>, until a
    /// <see cref="LoadAsync"/> or <see cref="SyncAsync"/> gives it an owner again. Clearing
    /// without that would trade one bug for a worse one: the save key is not per-user, so
    /// a title that keeps running after the removal - the platform is entitled to let it,
    /// and practice needs no identity at all - would accumulate fresh progress against
    /// nobody and write it straight over the file the commit above just produced. Zero
    /// counters overwriting a departed player's real ones is data loss that looks like a
    /// successful save.
    /// </para>
    /// </remarks>
    public void Clear()
    {
        lock (_gate)
        {
            _reported.Clear();
            _lastPercent.Clear();
        }

        _stats = new AchievementStats();
        _ownerless = true;
    }

    // --- Gameplay reports ---------------------------------------------------

    /// <summary>The local player fired a weapon. Drives "Arms Dealer".</summary>
    public void ReportWeaponFired(WeaponType weapon)
    {
        if (_stats.AddWeaponFired(weapon))
        {
            ReportArmsDealer();
        }
    }

    /// <summary>The local player gained a buff. Drives "Fully Buffed".</summary>
    public void ReportBuffCollected(BuffType buff)
    {
        if (_stats.AddBuffCollected(buff))
        {
            ReportFullyBuffed();
        }
    }

    /// <summary>The local player destroyed a ship. Drives "First Blood" and "Centurion".</summary>
    public void ReportKill()
    {
        _stats.AddKill();
        Unlock(FirstKill);
        ReportProgress(Centurion, _stats.Kills, CenturionTarget);
    }

    /// <summary>The local player's ship was destroyed. Drives "Space Debris".</summary>
    public void ReportDeath() => Unlock(FirstDeath);

    /// <summary>
    /// The local player broke an asteroid apart with their own shot. Drives
    /// "Rock Breaker".
    /// </summary>
    /// <remarks>
    /// Counts splits, not rocks removed: a huge rock broken all the way down is worth
    /// every fragment it took to clear, which is what makes 250 a reachable number.
    /// </remarks>
    public void ReportAsteroidDestroyed()
    {
        _stats.AddAsteroidDestroyed();
        ReportProgress(RockBreaker, _stats.AsteroidsDestroyed, RockBreakerTarget);
    }

    /// <summary>
    /// A match the local player was in ran to completion. Drives "Shakedown Cruise",
    /// "Rumble Champion", "Untouchable" and "Full House".
    /// </summary>
    /// <param name="won">True when the local player finished first.</param>
    /// <param name="survived">True when the local player was never destroyed.</param>
    /// <param name="againstHumans">
    /// True when this was a networked match with at least one other player. The three
    /// competitive achievements all require it: "Full House" says so outright, and
    /// winning a solo practice match - where the local player is the only standing, and
    /// so always first - is not something to award a hundred gamerscore for.
    /// </param>
    public void ReportMatchFinished(bool won, bool survived, bool againstHumans)
    {
        Unlock(FirstMatchFinished);

        if (!againstHumans)
        {
            return;
        }

        if (won)
        {
            Unlock(MatchWon);

            if (survived)
            {
                Unlock(UntouchableWin);
            }
        }

        if (_stats.AddModeCompleted())
        {
            ReportFullHouse();
        }
    }

    // --- Reporting ----------------------------------------------------------

    /// <summary>
    /// Re-sends every progressive achievement's current percentage. Cheap:
    /// <see cref="ReportProgress"/> drops a value it has already sent, and
    /// <see cref="IAchievementService"/> is idempotent besides.
    /// </summary>
    private void ReportProgressAchievements()
    {
        ReportArmsDealer();
        ReportFullyBuffed();
        ReportFullHouse();
        ReportProgress(Centurion, _stats.Kills, CenturionTarget);
        ReportProgress(RockBreaker, _stats.AsteroidsDestroyed, RockBreakerTarget);
    }

    private void ReportArmsDealer() => ReportProgress(
        ArmsDealer,
        System.Numerics.BitOperations.PopCount((uint)_stats.WeaponsFired),
        AchievementStats.WeaponCount);

    private void ReportFullyBuffed() => ReportProgress(
        FullyBuffed,
        System.Numerics.BitOperations.PopCount((uint)_stats.BuffsCollected),
        AchievementStats.BuffCount);

    private void ReportFullHouse() => ReportProgress(
        FullHouse,
        AchievementStats.CountBits(_stats.ModesCompleted, AchievementStats.AllModesMask),
        AchievementStats.CountBits(AchievementStats.AllModesMask, AchievementStats.AllModesMask));

    /// <summary>
    /// Reports a fraction of the way to a target, skipping a percentage already sent this
    /// session. <see cref="IAchievementService.SetProgressAsync"/> unlocks at 100 on
    /// providers that only do binary unlocks, so no separate unlock call is needed.
    /// </summary>
    private void ReportProgress(string achievementId, int current, int target)
    {
        if (target <= 0)
        {
            return;
        }

        var percent = (int)Math.Clamp(current * 100L / target, 0L, 100L);

        if (percent <= 0)
        {
            return;
        }

        // XR-055: the record of what the service has actually accepted lives in the stats
        // and survives a restart, so a report lost to a brief outage or a suspend is still
        // owed afterwards. _lastPercent only stops the same percentage being sent twice
        // concurrently; it is not what decides whether the work is done.
        if (_stats.GetConfirmedPercent(achievementId) >= percent)
        {
            return;
        }

        lock (_gate)
        {
            if (_lastPercent.TryGetValue(achievementId, out var sending) && sending >= percent)
            {
                return;
            }

            _lastPercent[achievementId] = percent;
        }

        _ = SendAsync(achievementId, percent);
    }

    /// <summary>Unlocks a binary achievement unless it has already been reported this session.</summary>
    private void Unlock(string achievementId)
    {
        // Persisted (XR-055): a bit set here survives the process exiting before the
        // service call below succeeds, so ReportPendingBinaryAchievements can retry it
        // next time there is somebody to report to, even if the gameplay event that
        // first earned it never happens again this run.
        if (BinaryBit(achievementId) is { } bit)
        {
            _stats.AddBinaryUnlocked(bit);
        }

        lock (_gate)
        {
            if (!_reported.Add(achievementId))
            {
                return;
            }
        }

        _ = SendAsync(achievementId, 100);
    }

    /// <summary>Maps a binary achievement id to its persisted bit, or null for a progressive one.</summary>
    private static int? BinaryBit(string achievementId) => achievementId switch
    {
        FirstMatchFinished => AchievementStats.FirstMatchFinishedBit,
        FirstDeath => AchievementStats.FirstDeathBit,
        FirstKill => AchievementStats.FirstKillBit,
        MatchWon => AchievementStats.MatchWonBit,
        UntouchableWin => AchievementStats.UntouchableWinBit,
        _ => null,
    };

    /// <summary>
    /// Re-sends every one-shot achievement <see cref="AchievementStats.BinaryUnlocked"/>
    /// already has a bit for, skipping ones this session has already reported (XR-055).
    /// Complements <see cref="ReportProgressAchievements"/>: a binary achievement's
    /// triggering gameplay event fires at most once, so if the report made when it fired
    /// did not survive to a successful send, nothing will ever call <see cref="Unlock"/>
    /// for it again - this is the only other place that can retry it.
    /// </summary>
    private void ReportPendingBinaryAchievements()
    {
        TryResend(FirstMatchFinished, AchievementStats.FirstMatchFinishedBit);
        TryResend(FirstDeath, AchievementStats.FirstDeathBit);
        TryResend(FirstKill, AchievementStats.FirstKillBit);
        TryResend(MatchWon, AchievementStats.MatchWonBit);
        TryResend(UntouchableWin, AchievementStats.UntouchableWinBit);

        void TryResend(string achievementId, int bit)
        {
            if ((_stats.BinaryUnlocked & bit) == 0)
            {
                return;
            }

            lock (_gate)
            {
                if (!_reported.Add(achievementId))
                {
                    return;
                }
            }

            _ = SendAsync(achievementId, 100);
        }
    }

    private async Task SendAsync(string achievementId, int percent)
    {
        var succeeded = false;
        string detail;

        try
        {
            var result = await _platform.Achievements.SetProgressAsync(achievementId, percent);
            succeeded = result.Succeeded;
            detail = result.Message ?? result.Status.ToString();
        }
        catch (Exception ex)
        {
            // The interface promises best-effort. A provider that throws rather than
            // returning a failed result must still not take a match down with it - but it
            // is no longer swallowed silently: a throwing provider is a defect somebody
            // has to be able to see, and this catch used to record nothing at all.
            detail = ex.GetType().Name + ": " + ex.Message;
        }

        if (succeeded)
        {
            // XR-055: only now, once the service has confirmed it, is the percentage
            // written down as sent. Advancing it optimistically is what made a failed
            // final report unrecoverable - it looked delivered and was never retried.
            //
            // Marked dirty rather than written through: losing a confirmation to a crash
            // costs one idempotent re-report, while the suspend and match-end paths that
            // already persist the counters carry this with them. Writing from here would
            // put concurrent file IO on a save tier that expects one writer.
            _stats.SetConfirmedPercent(achievementId, percent);

            return;
        }

        CrashLog.MarkOnce(
            $"achievement-send-failed-{achievementId}",
            $"achievements: {achievementId} at {percent}% not accepted: {detail}");

        // Nobody was signed in, or the service refused. Release the dedupe so the next
        // opportunity retries rather than the process giving up for good. The stats are
        // already on disk, so no progress is lost either way.
        lock (_gate)
        {
            _reported.Remove(achievementId);
            _lastPercent.Remove(achievementId);
        }
    }
}
