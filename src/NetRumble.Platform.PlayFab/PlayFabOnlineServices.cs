using GDK.Net.PlayFab;
using GDK.Net.Users;
using NetRumble.Platform.Diagnostics;

namespace NetRumble.Platform.PlayFab;

/// <summary>Owns the native Xbox-backed PlayFab identity and lobby client.</summary>
/// <remarks>
/// Pump and dispose on the provider's pump thread. Await <see cref="ShutdownAsync"/>
/// before releasing the Gaming Runtime.
/// </remarks>
public sealed class PlayFabOnlineServices : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _signInGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HashSet<PlayFabEntityLease> _leases = [];
    private TaskCompletionSource _idle = CompletedSource();
    private PlayFabServiceConfig? _serviceConfig;
    private PlayFabLocalUser? _localUser;
    private PlayFabEntity? _entity;
    private PlayFabSession? _session;
    private long _generation;
    private bool _disposed;

    public PlayFabOnlineServices(PlayFabConfiguration configuration)
    {
        Configuration = configuration;
        Lobbies = new PlayFabLobbyClient(configuration, AcquireEntity, _shutdown.Token);
        Lobbies.ConnectionLost += failure =>
        {
            if (failure.Status == PlatformStatus.NotSignedIn)
            {
                SignOut();
            }
        };
    }

    public PlayFabConfiguration Configuration { get; }
    public PlayFabSession? Session => Volatile.Read(ref _session);
    public bool IsSignedIn => Session is not null;
    public string EntityId => Session?.EntityId ?? string.Empty;
    internal PlayFabLobbyClient Lobbies { get; }
    public event Action<PlayFabSession?>? SessionChanged;

    internal PlayFabEntityLease? AcquireEntity()
    {
        lock (_gate)
        {
            if (_disposed || _entity is null)
            {
                return null;
            }
            var duplicate = _entity.Duplicate();
            if (_leases.Count == 0)
            {
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            var lease = new PlayFabEntityLease(duplicate, ReleaseLease);
            _leases.Add(lease);
            return lease;
        }
    }

    internal Task<PlatformResult<PlayFabSession>> SignInWithXboxUserAsync(
        User user, bool createAccount, CancellationToken cancellationToken = default)
        => SignInAsync(async (config, token) =>
        {
            var localUser = PlayFabLocalUser.CreateForXboxUser(config, user);
            try
            {
                var login = await localUser.LoginAsync(createAccount, token).ConfigureAwait(false);
                return (login, localUser);
            }
            catch
            {
                localUser.Dispose();
                throw;
            }
        }, cancellationToken);

    private async Task<PlatformResult<PlayFabSession>> SignInAsync(
        Func<PlayFabServiceConfig, CancellationToken, Task<(PlayFabLoginResult Login, PlayFabLocalUser? User)>> login,
        CancellationToken cancellationToken)
    {
        if (Configuration.IsPlaceholder)
        {
            return PlatformResult<PlayFabSession>.Unavailable(Configuration.UnavailableReason);
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var entered = false;
        PlayFabServiceConfig? config = null;
        PlayFabLocalUser? localUser = null;
        PlayFabEntity? entity = null;
        try
        {
            long generation;
            lock (_gate)
            {
                if (_disposed)
                {
                    return PlatformResult<PlayFabSession>.Unavailable("PlayFab has been shut down.");
                }
                generation = _generation;
            }
            await _signInGate.WaitAsync(lifetime.Token).ConfigureAwait(false);
            entered = true;
            lock (_gate)
            {
                if (_disposed || generation != _generation)
                {
                    return PlatformResult<PlayFabSession>.Canceled("The signed-in account changed.");
                }
            }
            if (!PlayFabRuntime.IsInitialized)
            {
                PlayFabRuntime.Initialize();
            }
            config = new PlayFabServiceConfig(
                $"https://{Configuration.TitleId}.playfabapi.com", Configuration.TitleId);
            var signedIn = await login(config, lifetime.Token).ConfigureAwait(false);
            using var result = signedIn.Login;
            localUser = signedIn.User;
            entity = result.Detach();
            var key = entity.Key;
            if (string.IsNullOrWhiteSpace(key.Id) || string.IsNullOrWhiteSpace(key.Type))
            {
                throw new InvalidOperationException("The native login returned an incomplete entity key.");
            }
            var session = new PlayFabSession
            {
                EntityId = key.Id,
                EntityType = key.Type,
                PlayFabId = result.Result.PlayFabId ?? string.Empty,
                AccountWasCreated = result.Result.NewlyCreated,
            };
            lock (_gate)
            {
                if (_disposed || generation != _generation)
                {
                    return PlatformResult<PlayFabSession>.Canceled("The signed-in account changed.");
                }
                Lobbies.Reset();
                ReleaseIdentity();
                _serviceConfig = config;
                _localUser = localUser;
                _entity = entity;
                Volatile.Write(ref _session, session);
                config = null;
                localUser = null;
                entity = null;
            }
            SessionChanged?.Invoke(session);
            return PlatformResult<PlayFabSession>.Ok(session);
        }
        catch (Exception ex) when (PlayFabFailure.IsExpected(ex))
        {
            var failure = PlayFabFailure.FromException(ex, "PlayFab sign-in could not be completed.");
            if (!failure.WasCanceled)
            {
                CrashLog.Mark($"playfab: native sign-in failed ({ex.GetType().Name}): {ex.Message}");
            }
            return PlayFabFailure.For<PlayFabSession>(failure);
        }
        finally
        {
            entity?.Dispose();
            localUser?.Dispose();
            config?.Dispose();
            if (entered)
            {
                _signInGate.Release();
            }
        }
    }

    public void SignOut()
    {
        lock (_gate)
        {
            _generation++;
            ReleaseIdentity();
        }
        Lobbies.Reset();
        SessionChanged?.Invoke(null);
    }

    private void ReleaseIdentity()
    {
        Volatile.Write(ref _session, null);
        _entity?.Dispose();
        _entity = null;
        _localUser?.Dispose();
        _localUser = null;
        _serviceConfig?.Dispose();
        _serviceConfig = null;
    }

    /// <summary>Drains native Lobby state changes on the platform pump thread.</summary>
    public void Pump() => Lobbies.Pump();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _generation++;
            ReleaseIdentity();
        }
        _shutdown.Cancel();
        Lobbies.Dispose();
    }

    /// <summary>Cancels work and waits for borrowed native handles before runtime shutdown.</summary>
    public async Task ShutdownAsync()
    {
        Dispose();
        await _signInGate.WaitAsync().ConfigureAwait(false);
        _signInGate.Release();
        Task idle;
        lock (_gate)
        {
            idle = _idle.Task;
        }
        await idle.ConfigureAwait(false);
    }

    private void ReleaseLease(PlayFabEntityLease lease)
    {
        lock (_gate)
        {
            _leases.Remove(lease);
            if (_leases.Count == 0)
            {
                _idle.TrySetResult();
            }
        }
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}

internal sealed class PlayFabEntityLease(PlayFabEntity entity, Action<PlayFabEntityLease> release) : IDisposable
{
    public PlayFabEntity Entity => entity;

    public void Dispose()
    {
        entity.Dispose();
        release(this);
    }
}
