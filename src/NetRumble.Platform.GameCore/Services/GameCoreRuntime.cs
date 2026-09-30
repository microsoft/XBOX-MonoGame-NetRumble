using GDK.Net;
using GDK.Net.Networking;
using GDK.Net.SystemInfo;
using NetRumble.Platform.GameCore.Interop;

namespace NetRumble.Platform.GameCore.Services;

/// <summary>
/// GDK-backed runtime: <c>XGameRuntimeInitialize</c>, through GDK.Net, plus the per-frame
/// drain that keeps this provider's callbacks single-threaded.
/// </summary>
internal sealed class GameCoreRuntime : IPlatformRuntime
{
    private GameRuntime? _runtime;

    /// <summary>The most recent hint, kept for <see cref="OfflineReason"/>.</summary>
    private NetworkingConnectivityHint? _hint;

    public bool IsInitialized { get; private set; }

    public bool IsRuntimeAvailable => GameRuntimeProbe.IsAvailable;

    /// <summary>
    /// Asked of the GDK rather than of the build. <c>XSystemGetDeviceType</c> answers for
    /// the machine, so the same binary reports <see cref="PlatformDeviceKind.Desktop"/> on
    /// a dev box and <see cref="PlatformDeviceKind.Console"/> on hardware.
    /// </summary>
    /// <remarks>
    /// Devkits count as consoles. They behave like one in every way this answer is used
    /// for, and a title that offered a different way out on a devkit than on retail would
    /// be testing something it does not ship.
    /// </remarks>
    public PlatformDeviceKind DeviceKind
    {
        get
        {
            if (!IsRuntimeAvailable)
            {
                return PlatformDeviceKind.Desktop;
            }

            try
            {
                return GameSystem.DeviceType switch
                {
                    SystemDeviceType.Pc or SystemDeviceType.Unknown => PlatformDeviceKind.Desktop,
                    _ => PlatformDeviceKind.Console,
                };
            }
            catch (GameRuntimeException)
            {
                // Answering "desktop" keeps every caller's fallback the permissive one -
                // Quit stays offered - which is the right way round: a console that was
                // wrongly told it was a desktop shows one extra menu row, where the
                // reverse strands a desktop player with no way to close the game.
                return PlatformDeviceKind.Desktop;
            }
        }
    }

    /// <inheritdoc />
    public bool IsConnectivityKnown { get; private set; }

    /// <inheritdoc />
    public bool IsOnline { get; private set; } = true;

    /// <inheritdoc />
    public string OfflineReason
    {
        get
        {
            if (IsOnline)
            {
                return string.Empty;
            }

            // Two different problems with two different things to do about them, so they
            // are not collapsed into one sentence.
            return _hint is { NetworkInitialized: false }
                ? "This console is not connected to a network."
                : "This console has no internet connection.";
        }
    }

    /// <inheritdoc />
    public event Action<bool>? ConnectivityChanged;

    public event Action<PlatformResult>? RuntimeError;

    /// <summary>
    /// Raised from <see cref="Pump"/> when the OS suspends, resumes, constrains or
    /// unconstrains the title.
    /// </summary>
    /// <remarks>
    /// The GDK delivers these on an OS thread, so <see cref="RegisterLifecycle"/> posts
    /// them through <see cref="Dispatcher"/> rather than raising them inline. That keeps
    /// the provider's promise that every event the game sees surfaces on the pump thread
    /// - which matters more here than elsewhere, because the handlers touch the screen
    /// stack, the audio device and the match simulation.
    /// </remarks>
    public event Action<PlatformLifecycleEvent>? LifecycleChanged;

    /// <summary>
    /// The live GDK.Net runtime, and through it every subsystem manager the other
    /// services in this provider use. Null until <see cref="InitializeAsync"/> succeeds.
    /// </summary>
    internal GameRuntime? Runtime => GameRuntimeHost.Current;

    /// <summary>
    /// Marshals GDK.Net's thread-pool completions onto the pump, shared with every other
    /// service in this provider. See <see cref="PumpDispatcher"/> for why the provider
    /// rather than the GDK now owns that guarantee.
    /// </summary>
    internal PumpDispatcher Dispatcher { get; } = new();

    /// <summary>
    /// The activity/activation service, set once by <see cref="GameCorePlatformProvider"/>
    /// after construction. Pumped here rather than given its own per-frame hook because
    /// <see cref="Pump"/> is the single call <c>NetRumbleGame.Update</c> makes every
    /// frame. See the "Completions land on the pump thread" invariant in
    /// <c>docs/platform-abstraction.md</c>, which this keeps for activation callbacks too.
    /// </summary>
    internal GameCoreActivityService? Activity { get; set; }

    /// <summary>
    /// The Party networking service, set by the provider after construction and drained
    /// from this same frame hook. Party's own async model is a state-change pump rather
    /// than GDK.Net <see cref="Task"/> completions, but the observable contract is the
    /// same as for <see cref="Dispatcher"/>: tasks and events become visible only from
    /// <see cref="Pump"/>.
    /// </summary>
    internal GameCorePartyService? Party { get; set; }

    public Task<PlatformResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (IsInitialized)
        {
            return Task.FromResult(PlatformResult.Ok());
        }

        if (!GameRuntimeProbe.IsAvailable)
        {
            return Task.FromResult(PlatformResult.Unavailable(GameRuntimeProbe.UnavailableReason));
        }

        // Defence in depth. The probe verifies one sentinel export, but an SDK version
        // mismatch can still leave an individual entry point missing. A platform service
        // failing to bind must degrade to "unavailable", never crash the game.
        try
        {
            // Through GameRuntimeHost rather than GameRuntime.Initialize directly: the
            // save-root lookup during construction may already have brought the runtime
            // up, and initialising it twice is not a supported thing to do.
            if (!GameRuntimeHost.EnsureInitialized())
            {
                return Task.FromResult(PlatformResult.Unavailable(GameRuntimeProbe.UnavailableReason));
            }

            _runtime = GameRuntimeHost.Current;
            IsInitialized = true;
            RegisterLifecycle();
            RegisterConnectivity();
            return Task.FromResult(PlatformResult.Ok());
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException
                                      or DllNotFoundException
                                      or PlatformNotSupportedException)
        {
            return Task.FromResult(PlatformResult.Unavailable(
                "The installed Microsoft GDK is not compatible with this build. " +
                $"({ex.Message})"));
        }
        catch (GameRuntimeException ex)
        {
            return Task.FromResult(PlatformResult.Fail(
                PlatformStatus.Failed,
                "The Microsoft GDK could not be started.",
                ex.Message));
        }
    }

    /// <summary>
    /// Binds the GDK's PLM notifications to <see cref="LifecycleChanged"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Failure is not an error. PLM is a console concept and a desktop GDK exports no
    /// registration entry points, so a dev box simply never sees a lifecycle event - the
    /// game still runs, and <c>LifecycleCoordinator</c> on the game side drives constrain
    /// from window activation instead. See <see cref="PlmInterop"/>.
    /// </para>
    /// <para>
    /// <b>Suspend is the one event that cannot go through the pump.</b> Everything else
    /// here is posted to <see cref="Dispatcher"/> and surfaces on the next frame, which
    /// is the provider's normal threading contract. A suspend has no next frame: the
    /// process is frozen the moment the callback returns, and the platform may terminate
    /// the title rather than resume it. Work parked on the dispatcher would run after the
    /// freeze, which is to say never. So <see cref="PlatformLifecycleEvent.Suspending"/>
    /// is raised inline, on the OS thread, and the handler is written to be straight-line
    /// and finite - see <c>LifecycleCoordinator.OnSuspending</c>.
    /// </para>
    /// <para>
    /// Resume is not deadline-bound and goes back through the pump, so the handler that
    /// touches screens and audio does so on the thread that owns them.
    /// </para>
    /// </remarks>
    private void RegisterLifecycle()
    {
        PlmInterop.TryRegister(
            quiesced =>
            {
                if (quiesced)
                {
                    LifecycleChanged?.Invoke(PlatformLifecycleEvent.Suspending);
                    return;
                }

                Dispatcher.Post(() => LifecycleChanged?.Invoke(PlatformLifecycleEvent.Resumed));
            },
            constrained => Dispatcher.Post(() =>
                LifecycleChanged?.Invoke(
                    constrained ? PlatformLifecycleEvent.Constrained : PlatformLifecycleEvent.Unconstrained)));
    }

    /// <summary>
    /// Binds <c>XNetworkingRegisterConnectivityHintChanged</c> and takes the first
    /// reading (XR-074).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The hint is device-wide and costs nothing to read, which is what makes it usable
    /// <i>before</i> an attempt rather than only as an explanation afterwards.
    /// </para>
    /// <para>
    /// Seeded silently: whatever the machine reports now is the baseline, and there is
    /// nothing to announce about a state the player is already in. Changes go through
    /// the dispatcher like everything else, so the game sees them on the pump thread.
    /// </para>
    /// <para>
    /// Failure is not an error, for the same reason it is not for PLM. A build with no
    /// networking manager simply never answers, <see cref="IsConnectivityKnown"/> stays
    /// false, and <see cref="IsOnline"/> stays optimistic - which is exactly how the
    /// title behaved before any of this existed.
    /// </para>
    /// </remarks>
    private void RegisterConnectivity()
    {
        var networking = _runtime?.Networking;

        if (networking is null)
        {
            return;
        }

        try
        {
            networking.ConnectivityHintChanged += (_, args) =>
            {
                var online = Evaluate(args.Hint);
                Dispatcher.Post(() =>
                {
                    IsConnectivityKnown = true;
                    _hint = args.Hint;
                    SetOnline(online);
                });
            };

            _hint = networking.GetConnectivityHint();
            IsConnectivityKnown = true;
            IsOnline = Evaluate(_hint.Value);
        }
        catch (Exception ex) when (ex is GameRuntimeException
                                      or EntryPointNotFoundException
                                      or DllNotFoundException
                                      or PlatformNotSupportedException)
        {
            IsConnectivityKnown = false;
            IsOnline = true;
        }
    }

    /// <summary>
    /// The gate. Reports offline only on a platform certainty - see
    /// <see cref="IPlatformRuntime.IsOnline"/> for why it fails open.
    /// </summary>
    private static bool Evaluate(NetworkingConnectivityHint hint)
        => hint.NetworkInitialized && hint.ConnectivityLevel != NetworkingConnectivityLevelHint.None;

    private void SetOnline(bool value)
    {
        if (IsOnline == value)
        {
            return;
        }

        IsOnline = value;
        ConnectivityChanged?.Invoke(value);
    }

    /// <summary>
    /// Runs the completions GDK.Net finished since the last frame. Every <c>Task</c>
    /// handed out by this provider resolves inside this call, which is what keeps game
    /// code single-threaded.
    /// </summary>
    public void Pump()
    {
        if (!IsInitialized)
        {
            return;
        }

        try
        {
            Dispatcher.Drain();
            Activity?.Pump();
            Party?.Pump();
        }
        catch (Exception ex)
        {
            RuntimeError?.Invoke(PlatformResult.Fail(
                PlatformStatus.Failed,
                "The platform runtime stopped responding.",
                ex.Message));
        }
    }

    /// <summary>
    /// Drives <see cref="Pump"/> until <paramref name="work"/> finishes, or the budget
    /// runs out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Teardown is the one place the pump contract turns against itself.
    /// <c>NetRumbleGame.OnExiting</c> disposes the provider synchronously
    /// (<c>DisposeAsync().AsTask().GetAwaiter().GetResult()</c>) on the same thread that
    /// calls <see cref="Pump"/>. Anything in that teardown path that awaits a
    /// pump-marshalled task therefore waits for a drain that can no longer happen,
    /// because the only thread that drains is the one now blocked on the await. The game
    /// would hang on exit - and only on a real GDK machine with a live session, since
    /// every one of those calls short-circuits when the runtime is absent.
    /// </para>
    /// <para>
    /// So shutdown pumps for itself. This is deliberately the only place that does; it is
    /// safe here precisely because the frame loop has stopped, and it must not be used to
    /// wait on platform work during a frame, where it would be a re-entrant pump.
    /// </para>
    /// <para>
    /// The budget matters. Cleanup that never completes - a service call that will not
    /// return because the console is already tearing the title down - must not stop the
    /// process from exiting, so this gives up rather than blocking forever. GDK.Net's own
    /// <c>GameRuntime.Dispose</c> takes the same position with the same five seconds.
    /// </para>
    /// </remarks>
    internal void PumpUntil(Task work, TimeSpan budget)
    {
        var deadline = Environment.TickCount64 + (long)budget.TotalMilliseconds;

        while (!work.IsCompleted && Environment.TickCount64 < deadline)
        {
            Pump();

            if (work.IsCompleted)
            {
                break;
            }

            // Completions arrive on the thread pool and are only queued here, so there is
            // nothing to do but wait for one. A millisecond keeps a shutdown that is
            // waiting on the network from spinning a core.
            Thread.Sleep(1);
        }

        // One final drain: the work may have completed on the thread pool after the last
        // Pump, leaving its continuation queued and unrun.
        Pump();

        // Teardown swallows failures by design - the process is closing either way - but
        // a faulted task that is never observed would surface later as an unobserved
        // exception on the finalizer thread. Observe it here and drop it.
        _ = work.Exception;
    }

    public Task ShutdownAsync()
    {
        if (!IsInitialized)
        {
            return Task.CompletedTask;
        }

        IsInitialized = false;

        // Before anything else is torn down: a PLM callback landing mid-shutdown would
        // reach services that are on their way out.
        PlmInterop.Unregister();

        Party?.Dispose();
        Activity?.Shutdown();

        // Disposed before the queue is cleared: disposing the runtime can complete
        // outstanding operations, and anything they post has nowhere to run now. The
        // handle belongs to GameRuntimeHost, which may have been the one to create it.
        GameRuntimeHost.Shutdown();
        _runtime = null;
        Dispatcher.Clear();

        return Task.CompletedTask;
    }
}
