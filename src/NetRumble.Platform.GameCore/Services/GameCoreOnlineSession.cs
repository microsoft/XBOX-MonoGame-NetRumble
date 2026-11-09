using GDK.Net.Users;
using NetRumble.Platform.PlayFab;

namespace NetRumble.Platform.GameCore.Services;

/// <summary>Xbox-owned native PlayFab identity shared by Party and Lobby.</summary>
internal sealed class GameCoreOnlineSession : IDisposable
{
    private readonly GameCoreRuntime _runtime;
    private readonly PlayFabOnlineServices _services;
    private string _lastSignInFailure = string.Empty;
    private string? _lastSignInDiagnostics;
    private PlatformStatus _lastSignInStatus = PlatformStatus.Ok;
    private bool _disposed;

    internal GameCoreOnlineSession(GameCoreRuntime runtime, PlayFabConfiguration configuration)
    {
        _runtime = runtime;
        _services = new PlayFabOnlineServices(configuration);
    }

    internal bool IsSignedIn => _services.IsSignedIn;
    internal PlayFabEntityLease? AcquireEntity() => _services.AcquireEntity();
    internal string EntityId => _services.EntityId;
    internal PlayFabLobbyClient Lobbies => _services.Lobbies;
    internal PlayFabConfiguration Configuration => _services.Configuration;
    internal bool CanAttemptLogin => !Configuration.IsPlaceholder;
    internal string UnavailableReason => Configuration.IsPlaceholder
        ? Configuration.UnavailableReason : _lastSignInFailure;
    internal string? LastSignInDiagnostics => _lastSignInDiagnostics;
    internal PlatformStatus LastSignInStatus => _lastSignInStatus;
    internal bool CanRetrySignIn => _lastSignInStatus is PlatformStatus.Ok or PlatformStatus.TimedOut
        or PlatformStatus.NetworkFailure or PlatformStatus.Canceled;

    internal async Task<PlatformResult<PlayFabSession>> SignInAsync(
        User user, bool allowAccountCreation, CancellationToken cancellationToken = default)
    {
        // A replacement login must not retain the previous account if it fails.
        SignOut();
        if (_disposed || !_runtime.IsInitialized || _runtime.Runtime is null)
        {
            return RememberFailure(PlatformResult<PlayFabSession>.Unavailable(
                "The Microsoft GDK is not running in this build."));
        }

        var result = await _runtime.Dispatcher.Marshal(
            _services.SignInWithXboxUserAsync(user, allowAccountCreation, cancellationToken)).ConfigureAwait(false);
        return result.Failed ? RememberFailure(result) : result;
    }

    internal void SignOut()
    {
        _services.SignOut();
        _lastSignInFailure = string.Empty;
        _lastSignInDiagnostics = null;
        _lastSignInStatus = PlatformStatus.Ok;
    }

    internal void Pump() => _services.Pump();
    internal Task ShutdownAsync()
    {
        Dispose();
        return _services.ShutdownAsync();
    }

    private PlatformResult<PlayFabSession> RememberFailure(PlatformResult<PlayFabSession> result)
    {
        _lastSignInStatus = result.Status;
        _lastSignInFailure = $"PlayFab title '{Configuration.TitleId}': " +
            (result.Message ?? "The Xbox user could not be signed in.");
        _lastSignInDiagnostics = result.Result.Diagnostics;
        return result;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _services.Dispose();
    }
}
