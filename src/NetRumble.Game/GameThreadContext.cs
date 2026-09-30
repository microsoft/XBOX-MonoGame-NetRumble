using System.Collections.Concurrent;
using NetRumble.Platform.Diagnostics;

namespace NetRumble.Game;

/// <summary>
/// A <see cref="SynchronizationContext"/> that resumes <c>await</c> continuations on the
/// game thread, pumped once a frame.
/// </summary>
/// <remarks>
/// <para>
/// MonoGame installs no synchronisation context, so <c>await</c> in a screen resumes on
/// whichever thread pool thread completed the operation. Every line after an <c>await</c>
/// in this game's UI code is therefore running on the wrong thread: pushing screens,
/// mutating the roster, sending on the party transport, touching the graphics device.
/// Godot had no such hazard - its <c>await</c> resumed inside the scene tree's own
/// processing - so the port inherited the pattern without inheriting the guarantee.
/// </para>
/// <para>
/// On desktop this mostly gets away with it, which is why it survived this long. It was
/// caught by a LAN client crashing in <c>LanLink.Retransmit</c> with "collection was
/// modified": the lobby's join continuation was sending on a pool thread while the game
/// thread serviced the same link. The same shape on console is worse than an exception,
/// because the Xbox graphics and Party layers are reached through native calls that
/// assume the thread that owns them - and a native access violation takes the title
/// straight back to the dashboard with nothing logged.
/// </para>
/// <para>
/// Installing a context fixes every <c>await</c> at once rather than auditing
/// twenty-five of them and trusting the next one to remember. Platform-layer code that
/// deliberately stays off the game thread is unaffected: it already uses
/// <c>ConfigureAwait(false)</c>, which opts out of exactly this.
/// </para>
/// </remarks>
internal sealed class GameThreadContext : SynchronizationContext
{
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly int _threadId = Environment.CurrentManagedThreadId;

    /// <summary>
    /// Installs a context for the calling thread, which must be the thread that runs the
    /// game loop.
    /// </summary>
    public static GameThreadContext Install()
    {
        var context = new GameThreadContext();
        SetSynchronizationContext(context);

        return context;
    }

    /// <summary>
    /// Restores the default (thread pool) behaviour.
    /// </summary>
    /// <remarks>
    /// Called before shutdown blocks on <c>DisposeAsync</c>. Waiting on a task from the
    /// game thread while continuations are queued <i>to</i> the game thread is the classic
    /// deadlock, and after the loop has stopped nothing is left to drain the queue.
    /// </remarks>
    public static void Uninstall() => SetSynchronizationContext(null);

    public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

    /// <summary>
    /// Runs the callback on the game thread and waits for it.
    /// </summary>
    /// <remarks>
    /// Inline when already on the game thread - blocking there would wait for a queue only
    /// this thread can drain.
    /// </remarks>
    public override void Send(SendOrPostCallback d, object? state)
    {
        if (Environment.CurrentManagedThreadId == _threadId)
        {
            d(state);
            return;
        }

        using var done = new ManualResetEventSlim(false);
        Exception? failure = null;

        Post(
            _ =>
            {
                try
                {
                    d(state);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    done.Set();
                }
            },
            null);

        done.Wait();

        if (failure is not null)
        {
            throw failure;
        }
    }

    /// <summary>
    /// Runs the continuations that arrived since the last frame.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bounded to the depth found on entry: a continuation is free to queue more work, and
    /// draining until empty would let two callbacks that post to each other hold the frame
    /// forever.
    /// </para>
    /// <para>
    /// A continuation that throws is logged and the rest still run. These are the tails of
    /// <c>async void</c> UI handlers - a failed sign-in, a refused join - and before this
    /// context existed such an exception surfaced on a pool thread, where it took the
    /// process down. Losing one interaction is the better outcome, and the log keeps it
    /// from being lost silently.
    /// </para>
    /// </remarks>
    public void Pump()
    {
        var budget = _queue.Count;

        while (budget-- > 0 && _queue.TryDequeue(out var work))
        {
            try
            {
                work.Callback(work.State);
            }
            catch (Exception ex)
            {
                CrashLog.Fatal("GameThreadContext.Pump", ex);
            }
        }
    }
}
