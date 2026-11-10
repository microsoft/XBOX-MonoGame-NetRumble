using System.Collections.Concurrent;

namespace NetRumble.Platform.GameCore.Services;

/// <summary>
/// Queues selected GDK.Net task completions and callbacks for
/// <see cref="GameCoreRuntime.Pump"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> GDK.Net can complete operations on a thread-pool thread.
/// GameCore uses this queue when a completion or callback must be processed during the
/// game's frame pump.
/// </para>
/// <para>
/// <see cref="Marshal{T}(Task{T})"/> completes its mirror task during a drain, but its
/// continuations run asynchronously and follow the caller's synchronization context or
/// scheduler. The non-generic wrapper can itself finish on a thread-pool thread. Do not
/// assume every provider task or callback goes through this dispatcher.
/// </para>
/// <para>
/// Queued work waits until a future pump, which may be later if pumping is paused.
/// </para>
/// </remarks>
internal sealed class PumpDispatcher
{
    private readonly ConcurrentQueue<Action> _pending = new();

    /// <summary>
    /// Queues work for the next <see cref="Drain"/>. Safe to call from any thread; this
    /// is the only method that is.
    /// </summary>
    internal void Post(Action work) => _pending.Enqueue(work);

    /// <summary>
    /// Returns a task that mirrors <paramref name="source"/> but always completes on the
    /// pump thread.
    /// </summary>
    /// <remarks>
    /// Cancellation is taken from <paramref name="source"/> so the mirror preserves the
    /// source result and dispatch ordering.
    /// </remarks>
    internal Task<T> Marshal<T>(Task<T> source)
    {
        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        source.ContinueWith(
            (finished, state) =>
            {
                var dispatcher = (PumpDispatcher)state!;
                dispatcher.Post(() => Complete(completion, finished));
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return completion.Task;
    }

    /// <summary>
    /// Transfers completion through the dispatcher; this wrapper may finish on a
    /// thread-pool thread after its internal await.
    /// </summary>
    internal async Task Marshal(Task source)
    {
        // Adapting to the generic form keeps one implementation of the completion
        // transfer rather than two that can drift.
        await Marshal(AsUnit(source)).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs everything queued since the last drain. Called only from
    /// <see cref="GameCoreRuntime.Pump"/>.
    /// </summary>
    /// <remarks>
    /// The count is snapshotted first so that work queued <em>by</em> this drain runs
    /// next frame instead of extending this one, which would let a callback that posts
    /// to itself hang the game.
    /// </remarks>
    internal void Drain()
    {
        var budget = _pending.Count;

        while (budget-- > 0 && _pending.TryDequeue(out var work))
        {
            work();
        }
    }

    /// <summary>Discards queued work. Used on shutdown.</summary>
    internal void Clear()
    {
        while (_pending.TryDequeue(out _))
        {
        }
    }

    private static void Complete<T>(TaskCompletionSource<T> completion, Task<T> finished)
    {
        if (finished.IsCanceled)
        {
            completion.TrySetCanceled();
        }
        else if (finished.Exception is { } exception)
        {
            completion.TrySetException(exception.InnerExceptions);
        }
        else
        {
            completion.TrySetResult(finished.Result);
        }
    }

    private static async Task<bool> AsUnit(Task source)
    {
        await source.ConfigureAwait(false);
        return true;
    }
}
