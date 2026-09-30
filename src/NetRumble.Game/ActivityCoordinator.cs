using System.Security.Cryptography;
using System.Text;
using NetRumble.Platform;
using NetRumble.Platform.Diagnostics;

namespace NetRumble.Game;

/// <summary>
/// The single owner of every Multiplayer Activity mutation and every Recently Played
/// With report (XR-064, XR-067).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Activity set and delete used to be issued as bare
/// <c>_ = SetActivityAsync(...)</c> / <c>_ = DeleteActivityAsync()</c> from four screens,
/// unserialised and unobserved. Two consequences, both XR-064 defects. First, ordering:
/// a set raised by a roster change and a delete raised by the match starting are two
/// independent tasks, so the set can land <i>after</i> the delete and resurrect an
/// advertisement for a session nobody can join. Second, failure: a discarded task is a
/// failure the title cannot see, cannot retry and cannot tell the player about, which
/// reads to certification as the feature not working.
/// </para>
/// <para>
/// <b>How it fixes both.</b> There is one desired state - advertise
/// <see cref="ActivityState"/>, or advertise nothing - and one worker applying it. Callers
/// only ever declare intent; they never issue a call. A newer intent replaces an older one
/// that has not been applied yet, so the last state transition always wins no matter how
/// many roster changes queued up behind it, and the worker never has two calls in flight.
/// </para>
/// <para>
/// <b>Retry is bounded and does not outlive its intent.</b> A failed apply is retried with
/// the same escalating backoff <c>PlayFabRestClient</c> uses, but the retry loop re-reads
/// the desired state each time: a delete that arrives while a set is being retried simply
/// ends the retry, because the thing it was retrying is no longer what the title wants.
/// </para>
/// <para>
/// <b>Recently Played With is separate and additive.</b> It is not a state to converge on
/// but a set of encounters to accumulate, and the provider already de-duplicates, so a
/// failed report is retried by folding its xuids back into the pending set rather than by
/// replacing anything.
/// </para>
/// <para>
/// Everything here runs on the thread pool. No game state is touched:
/// <see cref="FailureMessage"/> is the only thing screens read, and it is a single
/// reference write.
/// </para>
/// </remarks>
public sealed class ActivityCoordinator
{
    /// <summary>
    /// Backoff between retries, in seconds. Matches the REST client's shape - quick
    /// first retry, then back off - and stops rather than retrying forever, because an
    /// activity that cannot be published after this long is a condition the player
    /// should be told about instead of one the title keeps quietly re-attempting.
    /// </summary>
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(8),
    ];

    private readonly IPlatformProvider _platform;
    private readonly object _gate = new();

    /// <summary>What the title wants advertised, or null for "nothing".</summary>
    private ActivityState? _desired;

    /// <summary>
    /// Bumped on every intent. The worker compares it before and after an await to tell
    /// "my result is still relevant" from "somebody has since asked for something else".
    /// </summary>
    private long _revision;

    private long _appliedRevision = -1;
    private bool _running;

    /// <summary>The in-flight drain, so a teardown can wait for queued work to land.</summary>
    private Task _worker = Task.CompletedTask;
    private readonly HashSet<string> _pendingRecentPlayers = new(StringComparer.OrdinalIgnoreCase);

    public ActivityCoordinator(IPlatformProvider platform) => _platform = platform;

    /// <summary>
    /// What the shell should be advertising.
    /// </summary>
    /// <param name="ConnectionString">The join payload a friend's shell hands back.</param>
    /// <param name="MaxPlayers">The mode's capacity.</param>
    /// <param name="CurrentPlayers">The live roster size (XR-064 requires this to track).</param>
    /// <param name="GroupId">The id every member of one session computes identically.</param>
    public readonly record struct ActivityState(
        string ConnectionString,
        int MaxPlayers,
        int CurrentPlayers,
        string GroupId);

    /// <summary>
    /// Why the session is not advertised, or empty when it is. Read by the lobby's status
    /// line (XR-064/XR-067: a silent failure is indistinguishable from the feature not
    /// existing).
    /// </summary>
    public string FailureMessage { get; private set; } = string.Empty;

    /// <summary>
    /// A group id every member of one session agrees on, whatever route they joined by.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The group id is how the shell decides that several players' activities describe
    /// the same game rather than several games. Left empty, the provider mints a fresh
    /// GUID per player, so a host and a client in one lobby advertise two unrelated
    /// sessions.
    /// </para>
    /// <para>
    /// <b>Derived from the connection string alone</b>, deliberately. It used to prefer
    /// <see cref="IPartyService.JoinCode"/> and fall back to a hash of the connection
    /// string, which is not one id: the host and a code joiner have a join code, while a
    /// player who arrived through an invite never gets one assigned, so a mixed session
    /// advertised two groups for one game. The connection string is the only value every
    /// member of a session holds identically regardless of how they got in, so it is the
    /// only correct input.
    /// </para>
    /// </remarks>
    public static string GroupIdFor(string connectionString)
    {
        if (connectionString.Length == 0)
        {
            return string.Empty;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(connectionString));

        return Convert.ToHexString(hash, 0, 8);
    }

    /// <summary>Declares that this session should be advertised as joinable.</summary>
    public void Advertise(ActivityState state)
    {
        if (state.ConnectionString.Length == 0)
        {
            return;
        }

        SetDesired(state);
    }

    /// <summary>
    /// Declares that nothing should be advertised (XR-064: "clear the activity whenever
    /// the session becomes non-joinable").
    /// </summary>
    /// <remarks>
    /// Called on every teardown path, <b>including connection loss</b>. The old code
    /// skipped the delete there on the reasoning that a provider would expire an activity
    /// behind a dead connection by itself. That is not guaranteed, and the failure mode -
    /// the shell advertising a session nobody can join - is exactly what the requirement
    /// names.
    /// </remarks>
    public void Clear() => SetDesired(null);

    /// <summary>Adds peers to the Recently Played With set (XR-067).</summary>
    public void ReportRecentPlayers(IEnumerable<string> xboxUserIds)
    {
        lock (_gate)
        {
            foreach (var xuid in xboxUserIds)
            {
                if (!string.IsNullOrEmpty(xuid))
                {
                    _pendingRecentPlayers.Add(xuid);
                }
            }

            if (_pendingRecentPlayers.Count == 0)
            {
                return;
            }
        }

        Kick();
    }

    private void SetDesired(ActivityState? state)
    {
        lock (_gate)
        {
            _desired = state;
            _revision++;
        }

        Kick();
    }

    /// <summary>
    /// Starts the worker if it is not already running. Exactly one runs at a time, which
    /// is what serialises every mutation.
    /// </summary>
    private void Kick()
    {
        lock (_gate)
        {
            if (_running)
            {
                return;
            }

            _running = true;
            _worker = Task.Run(DrainAsync);
        }
    }

    private async Task DrainAsync()
    {
        try
        {
            while (true)
            {
                ActivityState? desired;
                long revision;
                string[] recentPlayers;

                lock (_gate)
                {
                    desired = _desired;
                    revision = _revision;
                    recentPlayers = [.. _pendingRecentPlayers];
                    _pendingRecentPlayers.Clear();

                    if (revision == _appliedRevision && recentPlayers.Length == 0)
                    {
                        // Nothing left to converge on. Stopping inside the lock is what
                        // makes it safe: an intent raised after this point sees _running
                        // false and starts a fresh worker.
                        _running = false;
                        return;
                    }
                }

                if (recentPlayers.Length > 0)
                {
                    await ApplyRecentPlayersAsync(recentPlayers).ConfigureAwait(false);
                }

                if (revision != _appliedRevision)
                {
                    await ApplyActivityAsync(desired, revision).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            // The worker is the only thing draining the queue, so it must not die. A
            // provider that throws instead of returning a failed result is a bug in the
            // provider, not a reason to stop advertising for the rest of the session.
            CrashLog.Fatal("activity: coordinator worker", ex);

            lock (_gate)
            {
                _running = false;
            }
        }
    }

    private async Task ApplyActivityAsync(ActivityState? desired, long revision)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = desired is { } state
                ? await _platform.Activity.SetActivityAsync(
                    state.ConnectionString, state.MaxPlayers, state.CurrentPlayers, state.GroupId)
                    .ConfigureAwait(false)
                : await _platform.Activity.DeleteActivityAsync().ConfigureAwait(false);

            // Superseded while this was in flight: whatever this was doing no longer
            // describes the title's state, so neither its success nor its failure means
            // anything. The loop above will pick the new intent up immediately.
            lock (_gate)
            {
                if (_revision != revision)
                {
                    return;
                }
            }

            if (result.Succeeded)
            {
                _appliedRevision = revision;
                FailureMessage = string.Empty;
                return;
            }

            // Unavailable is not a failure to report or retry: it is a provider with no
            // activity support at all - offline, LAN, or a desktop build with no GDK -
            // and retrying it would never do anything but spin.
            if (result.Status == PlatformStatus.Unavailable)
            {
                _appliedRevision = revision;
                return;
            }

            if (attempt >= RetryDelays.Length)
            {
                _appliedRevision = revision;
                FailureMessage = desired is null
                    ? "This session is still being advertised to your friends."
                    : "Your friends can't see this session right now.";

                CrashLog.Mark(
                    $"activity: {(desired is null ? "delete" : "set")} failed after " +
                    $"{attempt} retries: {result.Message ?? result.Status.ToString()}");

                return;
            }

            await Task.Delay(RetryDelays[attempt]).ConfigureAwait(false);
        }
    }

    private async Task ApplyRecentPlayersAsync(string[] xboxUserIds)
    {
        var result = await _platform.Activity.ReportRecentPlayersAsync(xboxUserIds)
            .ConfigureAwait(false);

        if (result.Succeeded || result.Status == PlatformStatus.Unavailable)
        {
            return;
        }

        // Folded back in rather than retried in place: the set is additive, so the next
        // pass reports these together with whoever has joined since. The provider
        // de-duplicates, so re-reporting is free.
        lock (_gate)
        {
            foreach (var xuid in xboxUserIds)
            {
                _pendingRecentPlayers.Add(xuid);
            }
        }

        CrashLog.MarkOnce(
            "rpw-failed",
            $"activity: recent-players report failed, will retry: " +
            $"{result.Message ?? result.Status.ToString()}");

        await Task.Delay(RetryDelays[0]).ConfigureAwait(false);
    }

    /// <summary>
    /// Flushes the provider's own Recently Played With buffer at the end of a session.
    /// </summary>
    /// <remarks>
    /// Awaited by the caller rather than queued, because it happens on a teardown path
    /// that is already asynchronous. The queued work is drained first: a flush that
    /// overtook the delete or the last roster report would push a buffer that does not
    /// yet contain the peers the caller is tearing down around.
    /// </remarks>
    public async Task FlushAsync()
    {
        Task worker;

        lock (_gate)
        {
            worker = _worker;
        }

        // The worker only ever fails by logging, so this cannot observe an exception -
        // but a teardown path is the last place to start propagating one if that changes.
        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CrashLog.Fatal("activity: waiting for coordinator drain", ex);
        }

        var result = await _platform.Activity.FlushRecentPlayersAsync().ConfigureAwait(false);

        if (result.Failed && result.Status != PlatformStatus.Unavailable)
        {
            CrashLog.Mark(
                $"activity: recent-players flush failed: " +
                $"{result.Message ?? result.Status.ToString()}");
        }
    }
}
