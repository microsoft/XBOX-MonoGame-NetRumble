using GDK.Net;
using GDK.Net.SystemInfo;
using GDK.Net.Users;
using GDK.Net.XboxLive;
using NetRumble.Platform.GameCore.Interop;
using NetRumble.Platform.Diagnostics;

namespace NetRumble.Platform.GameCore.Services;

/// <summary>
/// Shared Xbox Services (XSAPI) lifecycle: <c>XblInitialize</c> once via GDK.Net's
/// <see cref="XboxLiveService"/>, and one <see cref="XboxLiveContext"/> per signed-in
/// user, lent to every Xbl-backed service in this provider.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists as its own class.</b> Achievements, Social, Moderation and
/// Privacy all need an <see cref="XboxLiveContext"/>, and creating one is neither cheap
/// nor something each service should duplicate. Centralising it here also means
/// <see cref="XboxLiveService.Initialize(XboxLiveOptions)"/> runs exactly once no matter
/// how many of those services end up constructed, matching the one-time
/// <c>gdk_bootstrap.gd</c> initialization the Godot source relies on.
/// </para>
/// <para>
/// <b>The SCID is configuration, not a derivation</b> (XR-055). It comes from
/// <see cref="XboxServicesConfiguration"/>: the <c>--xbl-scid=</c> switch, the
/// <c>NETRUMBLE_XBL_SCID</c> environment variable, or <c>xboxservices.config</c> staged
/// beside the executable, in that order. The old title-id derivation
/// (<c>00000000-0000-0000-0000-0000{titleId:x8}</c>) survives only as a last resort and
/// is logged as such when used, because it is correct for a title on the default service
/// configuration and silently wrong for any other - and a wrong SCID does not fail here,
/// it fails at the first achievement write, several features away from its cause. This
/// was not confirmed end-to-end in this environment: doing so needs a successful
/// <see cref="XboxLiveService.Initialize(XboxLiveOptions)"/> followed by a real service
/// call, and no signed-in Xbox account was available. See <c>docs/design-notes.md</c>.
/// </para>
/// <para>
/// <b>Contexts are per-user, not per-process.</b>
/// <see cref="XboxLiveService.CreateContext"/> takes the signed-in
/// <see cref="GDK.Net.User"/> directly, and the context this class hands out is only
/// valid for as long as that same user stays signed in. <see cref="TryGetContext"/>
/// recreates it if the identity service's user has changed since the last call, so
/// callers never have to think about sign-out/sign-in themselves.
/// </para>
/// </remarks>
internal sealed class GameCoreXblContext
{
    private readonly GameCoreRuntime _runtime;
    private readonly GameCoreIdentityService _identity;

    private bool _xblInitialized;
    private User? _boundUser;
    private XboxLiveContext? _context;

    internal GameCoreXblContext(GameCoreRuntime runtime, GameCoreIdentityService identity)
    {
        _runtime = runtime;
        _identity = identity;
    }

    /// <summary>
    /// Lends an <see cref="XboxLiveContext"/> bound to the currently signed-in user.
    /// Callers must not dispose it - lifetime is owned here, matching
    /// <see cref="GameCoreIdentityService.TryGetUser"/>.
    /// </summary>
    internal bool TryGetContext([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out XboxLiveContext? context)
    {
        context = null;

        if (!_runtime.IsInitialized || _runtime.Runtime is null || !XblRuntimeProbe.IsAvailable)
        {
            return false;
        }

        if (!_identity.TryGetUser(out var user))
        {
            ReleaseContext();
            return false;
        }

        if (_context is not null && ReferenceEquals(_boundUser, user))
        {
            context = _context;
            return true;
        }

        // The bound user changed (sign-out/sign-in, or account switch) - the old
        // context is for a user who may no longer be signed in, so it is dropped
        // before a new one is created.
        ReleaseContext();

        if (!EnsureXblInitialized())
        {
            return false;
        }

        try
        {
            _context = _runtime.Runtime.XboxLive.CreateContext(user);
            _boundUser = user;
            context = _context;
            return true;
        }
        catch (GameRuntimeException)
        {
            return false;
        }
    }

    /// <summary>
    /// Initializes Xbox Services once for the process. Idempotent and safe to call from
    /// every service that needs a context - only the first caller pays for it.
    /// </summary>
    private bool EnsureXblInitialized()
    {
        if (_xblInitialized)
        {
            return true;
        }

        try
        {
            var titleId = GameLauncher.GetXboxTitleId();
            var configuration = XboxServicesConfiguration.Resolve(
                titleId, Environment.GetCommandLineArgs());

            if (configuration.IsDerived)
            {
                // XR-055: not fatal, but never silent. A derived SCID is right for a
                // title on the default service configuration and wrong for any other,
                // and the wrong one fails at the first achievement write rather than
                // here - so the log has to make the connection for whoever reads it.
                CrashLog.MarkOnce(
                    "xbl-scid",
                    $"xbl: no {XboxServicesConfiguration.FileName}, --xbl-scid or " +
                    $"{XboxServicesConfiguration.EnvironmentVariable}; deriving SCID " +
                    $"{configuration.Scid} from title id {titleId:X8}. If Xbox Services " +
                    "calls fail, this is the first thing to check.");
            }
            else
            {
                CrashLog.MarkOnce(
                    "xbl-scid",
                    $"xbl: SCID {configuration.Scid} from {configuration.Source}");
            }

            _runtime.Runtime!.XboxLive.Initialize(new XboxLiveOptions { Scid = configuration.Scid });
            _xblInitialized = true;
            return true;
        }
        catch (Exception ex) when (ex is GameRuntimeException
                                      or InvalidOperationException
                                      or DllNotFoundException
                                      or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private void ReleaseContext()
    {
        _context?.Dispose();
        _context = null;
        _boundUser = null;
    }

    /// <summary>
    /// Tears down Xbox Services. Called once from
    /// <see cref="GameCorePlatformProvider.DisposeAsync"/>, while the Gaming Runtime is
    /// still initialized so the cleanup has something to run against, and driven through
    /// <see cref="GameCoreRuntime.PumpUntil"/> so the marshalled completion below has
    /// somewhere to land.
    /// </summary>
    internal async Task ShutdownAsync()
    {
        ReleaseContext();

        if (!_xblInitialized || _runtime.Runtime is null)
        {
            return;
        }

        _xblInitialized = false;

        try
        {
            await _runtime.Dispatcher
                .Marshal(_runtime.Runtime.XboxLive.CleanupAsync())
                .ConfigureAwait(false);
        }
        catch (GameRuntimeException)
        {
            // Best-effort: the process is shutting down either way.
        }
    }
}
