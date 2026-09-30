using GDK.Net;

namespace NetRumble.Platform.GameCore.Interop;

/// <summary>
/// The one place <c>XGameRuntimeInitialize</c> is called, and the one object that owns the
/// handle it returns.
/// </summary>
/// <remarks>
/// <para>
/// Every GDK entry point requires the Gaming Runtime to have been initialised first, and
/// the failure mode for calling one before that is not an error code - it is the same
/// shape as the Party crash described in <see cref="PartyRuntimeProbe"/>: the callee
/// dereferences state it never got and takes the process out with an access violation.
/// Nothing in an import table or a probe reveals the ordering requirement, so it has to
/// be enforced by construction.
/// </para>
/// <para>
/// It became load-bearing when the console's writable save location started coming from
/// <c>XPersistentLocalStorageGetPath</c>. That answer is needed while the game object is
/// being constructed - the save root is a constructor argument to the platform provider -
/// which is long before <see cref="Services.GameCoreRuntime.InitializeAsync"/> runs. One
/// idempotent, refcount-free entry point lets the early caller initialise the runtime and
/// the later one find it already up, rather than either calling the GDK too early or
/// initialising it twice.
/// </para>
/// <para>
/// Exceptions propagate. <see cref="Services.GameCoreRuntime.InitializeAsync"/> maps them
/// to the "GDK unavailable" states the front end already understands, and a caller that
/// only wants an answer catches them itself; swallowing them here would hide a genuinely
/// incompatible SDK behind a silent fallback.
/// </para>
/// </remarks>
internal static class GameRuntimeHost
{
    private static readonly object Gate = new();

    private static GameRuntime? _runtime;

    /// <summary>True once <c>XGameRuntimeInitialize</c> has succeeded in this process.</summary>
    internal static bool IsInitialized
    {
        get
        {
            lock (Gate)
            {
                return _runtime is not null;
            }
        }
    }

    /// <summary>The initialised runtime, or <see langword="null"/> when there is none.</summary>
    internal static GameRuntime? Current
    {
        get
        {
            lock (Gate)
            {
                return _runtime;
            }
        }
    }

    /// <summary>
    /// Initialises the Gaming Runtime if it is not already up.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when there is no usable GDK in this process, in which case
    /// no GDK entry point may be called at all.
    /// </returns>
    internal static bool EnsureInitialized()
    {
        lock (Gate)
        {
            if (_runtime is not null)
            {
                return true;
            }

            if (!GameRuntimeProbe.IsAvailable)
            {
                return false;
            }

            _runtime = GameRuntime.Initialize();

            return true;
        }
    }

    /// <summary>
    /// Releases the runtime handle. Called once, at shutdown, by whichever owner is left.
    /// </summary>
    internal static void Shutdown()
    {
        lock (Gate)
        {
            _runtime?.Dispose();
            _runtime = null;
        }
    }
}
