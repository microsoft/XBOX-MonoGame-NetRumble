namespace NetRumble.Platform;

/// <summary>
/// Lifecycle and the per-frame pump for the underlying platform runtime.
/// </summary>
/// <remarks>
/// <para>
/// The GDK and PlayFab SDKs are <b>pump-based</b>: completions are delivered only when
/// the title drains a task queue (<c>XTaskQueueDispatch</c>) or processes state changes
/// (<c>PartyManager::StartProcessingStateChanges</c>). The Godot port did this from the
/// GDExtension's own <c>dispatch()</c>; here the game calls <see cref="Pump"/> once per
/// frame from <c>Game.Update</c>.
/// </para>
/// <para>
/// <b>Threading contract, and why it matters for provider swapping.</b> Providers must
/// resolve every <see cref="Task"/> they hand out, and raise every event they declare,
/// on the thread that calls <see cref="Pump"/>. Game code is single-threaded and must
/// never need a lock or a dispatcher. A provider that completes tasks on an SDK
/// callback thread would push that burden into gameplay code and break the abstraction.
/// </para>
/// </remarks>
public interface IPlatformRuntime
{
    /// <summary>True once <see cref="InitializeAsync"/> has succeeded.</summary>
    bool IsInitialized { get; }

    /// <summary>
    /// True when the native runtime is actually present. False on a dev box without the
    /// GDK installed, which is a supported configuration — the game runs offline.
    /// </summary>
    bool IsRuntimeAvailable { get; }

    /// <summary>
    /// The kind of hardware the title is running on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ported from <c>NRScreen.is_console()</c>, but asked of the platform rather than of
    /// the build. Godot tests the <c>scarlett</c> feature tag, which is a property of the
    /// export preset; this is a property of the machine. The distinction is not academic
    /// here - the GDK provider is the one used on a desktop dev box, so a build-time
    /// answer would call that a console and take console behaviour with it.
    /// </para>
    /// <para>
    /// Deliberately two values and not the GDK's nine. Nothing in this title wants to
    /// know whether it is on a Lockhart or an Anaconda; every caller wants to know
    /// whether the platform owns the way out of the title, which is the same answer for
    /// all of them, devkits included.
    /// </para>
    /// </remarks>
    PlatformDeviceKind DeviceKind { get; }

    /// <summary>
    /// Whether the platform actually answers the connectivity question on this machine
    /// (XR-074). False on a desktop box and in any build without the GDK.
    /// </summary>
    /// <remarks>
    /// The UI uses this to stay quiet rather than assert "connected" where it cannot
    /// know. Ported from <c>ConnectivityService.is_supported()</c>.
    /// </remarks>
    bool IsConnectivityKnown { get; }

    /// <summary>
    /// Whether online play is worth offering: true whenever the platform is not certain
    /// the answer is no (XR-074).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This deliberately fails open, and that is the whole design.</b> The GDK
    /// documents <c>NetworkInitialized</c> as the only authoritative field of
    /// <c>XNetworkingGetConnectivityHint</c> and warns that the rest is best-effort that
    /// must not be treated as a reachability test for a specific endpoint. So this
    /// reports offline only on a platform certainty - the network stack is not
    /// initialised, or the level is an explicit <c>None</c> - and treats everything else,
    /// <c>Unknown</c> included, as "let the player try".
    /// </para>
    /// <para>
    /// The asymmetry is the point. A false "offline" locks a player who has a working
    /// connection out of online play with no recourse, which is a worse defect than the
    /// one being fixed. A false "online" costs one failed attempt that the existing
    /// failure handling already turns into a dialog and a trip back to the main menu.
    /// Given a hint explicitly documented as approximate, the only safe direction to be
    /// wrong in is the optimistic one.
    /// </para>
    /// <para>
    /// <c>LocalAccess</c> and <c>ConstrainedInternetAccess</c> are therefore treated as
    /// online even though PlayFab almost certainly cannot be reached through either.
    /// They are the cases the hint is least reliable about - a captive portal is
    /// explicitly not guaranteed to be detected - and being wrong about them costs a
    /// dialog rather than a lockout.
    /// </para>
    /// <para>
    /// None of this replaces the reactive path. This is the proactive half; a Party
    /// network loss remains the backstop for a drop this never sees, and both end in the
    /// same place.
    /// </para>
    /// </remarks>
    bool IsOnline { get; }

    /// <summary>
    /// Why online play is unavailable, phrased for the player; empty when it is
    /// available.
    /// </summary>
    /// <remarks>
    /// Distinguishes "no network at all" from "the network is up but reports no
    /// connectivity", because those need different things done about them.
    /// </remarks>
    string OfflineReason { get; }

    /// <summary>
    /// Raised when <see cref="IsOnline"/> changes, carrying the new value so a handler
    /// need not re-query. Transitions only; the initial state is seeded silently.
    /// </summary>
    event Action<bool>? ConnectivityChanged;

    /// <summary>Raised when the runtime hits an unrecoverable error after startup.</summary>
    event Action<PlatformResult>? RuntimeError;

    /// <summary>
    /// Raised when the OS suspends or resumes the title. Providers surface the GDK
    /// lifecycle here so the game can pause the simulation and drop the network.
    /// </summary>
    event Action<PlatformLifecycleEvent>? LifecycleChanged;

    /// <summary>Starts the runtime. Safe to call more than once.</summary>
    Task<PlatformResult> InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Drains pending native completions and raises the resulting events. Must be
    /// called once per frame from the main thread.
    /// </summary>
    void Pump();

    /// <summary>Shuts the runtime down. Safe to call when never initialized.</summary>
    Task ShutdownAsync();
}

public enum PlatformDeviceKind
{
    /// <summary>A PC. The title owns its own window, and therefore its own way out.</summary>
    Desktop,

    /// <summary>
    /// An Xbox console, retail or devkit. The platform owns leaving the title, through
    /// the Guide.
    /// </summary>
    Console,
}

public enum PlatformLifecycleEvent
{
    Suspending,
    Resumed,

    /// <summary>The active user changed or signed out; the game must return to sign-in.</summary>
    UserChanged,

    /// <summary>Constrained/low-power mode (console). Throttle rendering.</summary>
    Constrained,

    /// <summary>Returned from constrained mode.</summary>
    Unconstrained,
}
