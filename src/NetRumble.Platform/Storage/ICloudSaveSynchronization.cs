namespace NetRumble.Platform;

/// <summary>Reconciles synchronized cloud data before suspended-session writes resume.</summary>
public interface ICloudSaveSynchronization
{
    /// <summary>True until the title has reconciled the current resumed save session.</summary>
    bool RequiresReconciliation { get; }

    /// <summary>
    /// When reconciliation is required, acquires the synchronized folder and runs the
    /// title's read/merge callback.
    /// Cloud writes remain blocked until the callback succeeds for the same save session.
    /// </summary>
    Task<PlatformResult> ReconcileAsync(
        Func<CancellationToken, Task<PlatformResult>> reconcile,
        CancellationToken cancellationToken = default);
}
