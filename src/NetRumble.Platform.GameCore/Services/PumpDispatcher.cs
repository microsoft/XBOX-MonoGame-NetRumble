using System.Collections.Concurrent;

namespace NetRumble.Platform.GameCore.Services;

/// <summary>
/// Moves GDK.Net completions and events onto the thread that calls
/// <see cref="GameCoreRuntime.Pump"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <c>docs/platform-abstraction.md</c> promises that every task
/// this provider hands out, and every event it raises, surfaces on the pump thread. That
/// is what lets game code touch shared state from a completion handler without a lock,
/// and it is the reason the provider interfaces are safe to consume from
/// <c>NetRumbleGame.Update</c>.
/// </para>
/// <para>
/// The old P/Invoke layer kept that promise by owning the GDK task queue: it created one
/// in <c>XTaskQueueDispatchMode::Manual</c> and drained it from <c>Pump</c>, so
/// completions could only ever run there. GDK.Net does not offer that. Its
/// <c>GameTaskQueue</c> is deliberately <c>internal</c>, and
/// <c>GameRuntime.Initialize</c> names no queue at all, so the Gaming Runtime resolves
/// the process default - a thread-pool queue - and continuations arrive on the thread
/// pool. GDK.Net documents this and expects a title that must touch its renderer to
/// marshal to its own thread, which is exactly what this class does.
/// </para>
/// <para>
/// So the contract is now kept one level up, at this provider's own boundary rather than
/// at the GDK's: GDK.Net's <see cref="Task"/> may complete anywhere, but the task
/// <em>we</em> return completes on the pump. The observable behaviour is unchanged; only
/// the mechanism moved.
/// </para>
/// <para>
/// <b>Cost.</b> A completion is delayed by at most one frame. That was already true of
/// the manual queue, which also only ran during <c>Pump</c>.
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
    /// Cancellation is deliberately not re-observed here. <paramref name="source"/>
    /// already carries the caller's token, and faulting or cancelling the mirror early
    /// would let the continuation run off the pump - the one thing this type exists to
    /// prevent.
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

    /// <inheritdoc cref="Marshal{T}(Task{T})"/>
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
