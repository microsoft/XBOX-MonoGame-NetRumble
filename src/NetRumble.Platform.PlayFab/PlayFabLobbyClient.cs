using System.Collections.Concurrent;
using GDK.Net.PlayFab;
using GDK.Net.PlayFab.Multiplayer;
using NetRumble.Platform.Diagnostics;
using NetRumble.Platform.Networking;

namespace NetRumble.Platform.PlayFab;

/// <summary>A snapshot of the native Lobby data needed by the game.</summary>
public sealed record PlayFabLobby
{
    public required string LobbyId { get; init; }
    public required string ConnectionString { get; init; }
    public string JoinCode { get; init; } = string.Empty;
    public int MaxPlayers { get; init; }
    public int CurrentPlayers { get; init; }
    public string OwnerEntityId { get; init; } = string.Empty;
    public string ProtocolVersion { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> LobbyData { get; init; } = new Dictionary<string, string>();
}

/// <summary>GDK.Net Lobby operations, issued and completed on the platform pump.</summary>
internal sealed class PlayFabLobbyClient : IDisposable
{
    public const string JoinCodeSearchKey = "string_key1";
    public const string JoinCodeAlphabet = "ABCDEFGHJKLMNPQRTUVWXYZ2346789";
    public const int JoinCodeLength = 5;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);
    private readonly PlayFabConfiguration _configuration;
    private readonly Func<PlayFabEntityLease?> _acquireEntity;
    private readonly CancellationToken _shutdown;
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly Dictionary<OperationId, PendingOperation> _pending = [];
    private readonly Dictionary<string, Lobby> _lobbies = new(StringComparer.Ordinal);
    private readonly HashSet<OperationId> _retiring = [];
    private readonly Dictionary<OperationId, Lobby> _closing = [];
    private readonly HashSet<string> _hosted = new(StringComparer.Ordinal);
    private PlayFabMultiplayer? _multiplayer;
    private long _revision;
    private volatile bool _disposed;
    private bool _refreshingToken;
    private DateTimeOffset _refreshTokenAt = DateTimeOffset.MaxValue;
    private bool _resetting;
    private long _resetDeadline;

    internal PlayFabLobbyClient(PlayFabConfiguration configuration,
        Func<PlayFabEntityLease?> acquireEntity, CancellationToken shutdown = default)
    {
        _configuration = configuration;
        _acquireEntity = acquireEntity;
        _shutdown = shutdown;
    }

    internal event Action<PlatformResult>? ConnectionLost;

    public static string NewJoinCode() => string.Create(JoinCodeLength, 0, static (span, _) =>
    {
        for (var i = 0; i < span.Length; i++)
        {
            span[i] = JoinCodeAlphabet[Random.Shared.Next(JoinCodeAlphabet.Length)];
        }
    });

    public static bool IsJoinCode(string? code)
        => code is { Length: JoinCodeLength }
            && code.All(c => JoinCodeAlphabet.Contains(char.ToUpperInvariant(c), StringComparison.Ordinal));

    internal static string BuildJoinCodeFilter(string code)
        => $"{JoinCodeSearchKey} eq '{code.ToUpperInvariant()}'";

    public async Task<PlatformResult<PlayFabLobby>> CreateLobbyWithJoinCodeAsync(
        int maxPlayers, bool isPrivate = false, IReadOnlyDictionary<string, string>? lobbyData = null,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var code = NewJoinCode();
            var existing = await FindLobbyByJoinCodeAsync(code, cancellationToken).ConfigureAwait(false);
            if (existing.Failed)
            {
                return PlayFabFailure.For<PlayFabLobby>(existing.Result);
            }
            if (existing.Value is not null)
            {
                continue;
            }
            return await RunAsync<PlayFabLobby>((manager, key, complete, token) =>
            {
                var created = manager.CreateAndJoinLobby(key, new LobbyCreateConfiguration
                {
                    MaxMemberCount = (uint)Math.Clamp(maxPlayers, 2, 128),
                    AccessPolicy = isPrivate ? LobbyAccessPolicy.Private : LobbyAccessPolicy.Public,
                    OwnerMigrationPolicy = LobbyOwnerMigrationPolicy.None,
                    SearchProperties = new Dictionary<string, string>
                    {
                        [JoinCodeSearchKey] = code,
                        [NRProtocol.LobbyKey] = NRProtocol.VersionString(),
                    },
                    LobbyProperties = lobbyData,
                });
                Register(created.Operation, token, complete, change =>
                {
                    var lobby = change.Lobby ?? throw new InvalidOperationException("The created lobby was missing.");
                    var id = lobby.Id ?? throw new InvalidOperationException("The created lobby has no id.");
                    _lobbies.Add(id, lobby);
                    _hosted.Add(id);
                    return Snapshot(lobby);
                });
            }, cancellationToken).ConfigureAwait(false);
        }
        return PlatformResult<PlayFabLobby>.Fail(PlatformStatus.Failed,
            "Could not create the match. Please try again.", "No unused join code was found.");
    }

    public Task<PlatformResult<PlayFabLobby?>> FindLobbyByJoinCodeAsync(
        string joinCode, CancellationToken cancellationToken = default)
    {
        if (!IsJoinCode(joinCode))
        {
            return Task.FromResult(PlatformResult<PlayFabLobby?>.Fail(PlatformStatus.Failed,
                $"That code doesn't look right. Codes are {JoinCodeLength} letters and numbers.",
                "The join code contained invalid characters."));
        }
        return RunAsync<PlayFabLobby?>((manager, key, complete, token) =>
        {
            var operation = manager.FindLobbies(key, new LobbySearchConfiguration
            {
                FilterString = BuildJoinCodeFilter(joinCode),
                ClientSearchResultCount = 4,
            });
            Register(operation, token, complete, change =>
            {
                var results = ((FindLobbiesCompleted)change).Results;
                var match = results.FirstOrDefault(row => row.MembershipLock == LobbyMembershipLock.Unlocked);
                return match is null ? null : Snapshot(match);
            });
        }, cancellationToken);
    }

    public Task<PlatformResult<string>> JoinLobbyAsync(
        string connectionString, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return Task.FromResult(PlatformResult<string>.Fail(PlatformStatus.Failed,
                "That invite is no longer valid.", "The lobby connection string was empty."));
        }
        return RunAsync<string>((manager, key, complete, token) =>
        {
            var joined = manager.JoinLobby(key, connectionString);
            Register(joined.Operation, token, complete, change =>
            {
                var lobby = change.Lobby ?? throw new InvalidOperationException("The joined lobby was missing.");
                var id = lobby.Id ?? throw new InvalidOperationException("The joined lobby has no id.");
                _lobbies[id] = lobby;
                return id;
            });
        }, cancellationToken);
    }

    public Task<PlatformResult<PlayFabLobby?>> GetLobbyAsync(
        string lobbyId, CancellationToken cancellationToken = default)
        => RunAsync<PlayFabLobby?>((_, _, complete, _) =>
        {
            if (!_lobbies.TryGetValue(lobbyId, out var lobby) || !lobby.IsValid)
            {
                complete.TrySetException(new InvalidOperationException("The local lobby is no longer available."));
                return;
            }
            complete.TrySetResult(Snapshot(lobby));
        }, cancellationToken);

    public async Task<PlatformResult> LeaveLobbyAsync(
        string lobbyId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(lobbyId))
        {
            return PlatformResult.Ok();
        }
        var result = await RunAsync<bool>((_, key, complete, token) =>
        {
            if (!_lobbies.TryGetValue(lobbyId, out var lobby) || !lobby.IsValid)
            {
                complete.TrySetResult(true);
                return;
            }
            Register(lobby.Leave(key), token, complete, _ =>
            {
                _lobbies.Remove(lobbyId);
                _hosted.Remove(lobbyId);
                return true;
            });
        }, cancellationToken).ConfigureAwait(false);
        return result.Result;
    }

    public async Task<PlatformResult> CloseAndLeaveLobbyAsync(
        string lobbyId, CancellationToken cancellationToken = default)
    {
        // Client lobbies cannot call ServerDeleteLobby. Close discovery and joins before
        // the non-migrating owner leaves; the SDK then owns connection-based cleanup.
        var closed = await RunAsync<bool>((_, key, complete, token) =>
        {
            if (!_lobbies.TryGetValue(lobbyId, out var lobby) || !lobby.IsValid)
            {
                complete.TrySetResult(true);
                return;
            }
            Register(lobby.PostUpdate(key, new LobbyDataUpdate
            {
                AccessPolicy = LobbyAccessPolicy.Private,
                MembershipLock = LobbyMembershipLock.Locked,
            }), token, complete, _ => true);
        }, cancellationToken).ConfigureAwait(false);
        var left = await LeaveLobbyAsync(lobbyId, cancellationToken).ConfigureAwait(false);
        return closed.Failed ? closed.Result : left;
    }

    private async Task<PlatformResult<T>> RunAsync<T>(
        Action<PlayFabMultiplayer, EntityKey, TaskCompletionSource<T>, CancellationToken> begin,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown);
        timeout.CancelAfter(OperationTimeout);
        var operationToken = timeout.Token;
        var revision = Interlocked.Read(ref _revision);
        try
        {
            if (_disposed)
            {
                return PlatformResult<T>.Unavailable("PlayFab has been shut down.");
            }
            if (_configuration.IsPlaceholder)
            {
                return PlatformResult<T>.Unavailable(_configuration.UnavailableReason);
            }
            operationToken.ThrowIfCancellationRequested();
            using var lease = _acquireEntity();
            if (lease is null)
            {
                return PlatformResult<T>.Fail(PlatformStatus.NotSignedIn, "Sign in before using online matches.");
            }
            var key = lease.Entity.Key;
            var token = await lease.Entity.GetEntityTokenAsync(operationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(token.Token))
            {
                throw new InvalidOperationException("PlayFab returned no entity token.");
            }
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Start()
            {
                try
                {
                    if (_disposed || revision != Interlocked.Read(ref _revision))
                    {
                        completion.TrySetCanceled();
                        return;
                    }
                    operationToken.ThrowIfCancellationRequested();
                    if (_resetting)
                    {
                        _commands.Enqueue(Start);
                        return;
                    }
                    _multiplayer ??= PlayFabMultiplayer.Initialize(_configuration.TitleId);
                    _multiplayer.SetEntityToken(key, token.Token);
                    _refreshTokenAt = RefreshAt(token, DateTimeOffset.UtcNow);
                    begin(_multiplayer, key, completion, operationToken);
                }
                catch (Exception ex) when (PlayFabFailure.IsExpected(ex))
                {
                    completion.TrySetException(ex);
                }
            }
            _commands.Enqueue(Start);
            return PlatformResult<T>.Ok(await completion.Task.WaitAsync(operationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (PlayFabFailure.IsExpected(ex))
        {
            if (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested && !_shutdown.IsCancellationRequested
                && timeout.IsCancellationRequested)
            {
                return PlatformResult<T>.Fail(PlatformStatus.TimedOut, "The match did not respond in time.");
            }
            return PlayFabFailure.For<T>(PlayFabFailure.FromException(ex, "The online match request could not be completed."));
        }
    }

    private void Register<T>(OperationId operation, CancellationToken token,
        TaskCompletionSource<T> completion, Func<LobbyOperationCompleted, T> read)
    {
        _pending.Add(operation, new PendingOperation(token,
            () => completion.TrySetCanceled(token),
            change =>
            {
                if (completion.Task.IsCompleted || token.IsCancellationRequested)
                {
                    completion.TrySetCanceled(token);
                    if (!change.Failed && change is CreateAndJoinLobbyCompleted or JoinLobbyCompleted)
                    {
                        if (change.Lobby is { IsValid: true } abandoned)
                        {
                            RetireLobby(abandoned, change is CreateAndJoinLobbyCompleted);
                        }
                    }
                }
                else if (change.Failed)
                {
                    completion.TrySetException(change.Error ?? new InvalidOperationException(
                        $"Lobby operation failed: 0x{change.ResultCode:X8}."));
                }
                else
                {
                    try
                    {
                        completion.TrySetResult(read(change));
                    }
                    catch (Exception ex) when (PlayFabFailure.IsExpected(ex))
                    {
                        completion.TrySetException(ex);
                    }
                }
            }));
    }

    internal void Pump()
    {
        var budget = _commands.Count;
        while (budget-- > 0 && _commands.TryDequeue(out var command))
        {
            command();
        }
        foreach (var pending in _pending.Values)
        {
            if (pending.Token.IsCancellationRequested)
            {
                pending.Cancel();
            }
        }
        if (_multiplayer is null)
        {
            if (_resetting)
            {
                CompleteReset();
            }
            return;
        }
        if (_resetting)
        {
            try
            {
                ProcessChanges();
            }
            catch (Exception ex) when (PlayFabFailure.IsExpected(ex))
            {
                CrashLog.Mark($"playfab: lobby retirement pump failed: {ex.Message}");
                CompleteReset();
                return;
            }
            if ((_pending.Count == 0 && _retiring.Count == 0) || Environment.TickCount64 >= _resetDeadline)
            {
                CompleteReset();
            }
            return;
        }
        ProcessChanges();
        if (!_disposed && !_refreshingToken && DateTimeOffset.UtcNow >= _refreshTokenAt)
        {
            _refreshingToken = true;
            _ = RefreshTokenAsync(Interlocked.Read(ref _revision));
        }
    }

    private void ProcessChanges()
    {
        foreach (var change in _multiplayer!.ProcessLobbyStateChanges())
        {
            if (change is LobbyOperationCompleted operation && _pending.Remove(operation.Operation, out var pending))
            {
                pending.Complete(operation);
            }
            if (change is LeaveLobbyCompleted left && _retiring.Remove(left.Operation) && left.Failed)
            {
                CrashLog.Mark($"playfab: abandoned lobby leave failed: 0x{left.ResultCode:X8}");
            }
            if (change is PostUpdateCompleted closed && _closing.Remove(closed.Operation, out var retiring))
            {
                _retiring.Remove(closed.Operation);
                if (closed.Failed)
                {
                    CrashLog.Mark($"playfab: lobby could not be closed during shutdown: 0x{closed.ResultCode:X8}");
                }
                _retiring.Add(retiring.Leave());
            }
            if (change is LobbyDisconnected && change.Lobby is { } lobby)
            {
                var removed = false;
                foreach (var id in _lobbies.Where(pair => ReferenceEquals(pair.Value, lobby)).Select(pair => pair.Key).ToArray())
                {
                    _lobbies.Remove(id);
                    _hosted.Remove(id);
                    removed = true;
                }
                if (removed)
                {
                    ConnectionLost?.Invoke(PlatformResult.Fail(PlatformStatus.NetworkFailure,
                        "The match directory connection was lost."));
                }
            }
        }
    }

    private async Task RefreshTokenAsync(long revision)
    {
        try
        {
            using var lease = _acquireEntity();
            if (lease is null)
            {
                return;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown);
            timeout.CancelAfter(OperationTimeout);
            var key = lease.Entity.Key;
            var token = await lease.Entity.GetEntityTokenAsync(timeout.Token).ConfigureAwait(false);
            _commands.Enqueue(() =>
            {
                if (_disposed || revision != Interlocked.Read(ref _revision) || _multiplayer is null)
                {
                    return;
                }
                try
                {
                    if (string.IsNullOrEmpty(token.Token))
                    {
                        throw new InvalidOperationException("PlayFab returned no refreshed entity token.");
                    }
                    _multiplayer.SetEntityToken(key, token.Token);
                    _refreshTokenAt = RefreshAt(token, DateTimeOffset.UtcNow);
                }
                catch (Exception ex) when (PlayFabFailure.IsExpected(ex))
                {
                    TokenRefreshFailed(ex);
                }
                _refreshingToken = false;
            });
        }
        catch (Exception ex) when (PlayFabFailure.IsExpected(ex))
        {
            _commands.Enqueue(() =>
            {
                if (!_disposed && revision == Interlocked.Read(ref _revision))
                {
                    TokenRefreshFailed(ex);
                    _refreshingToken = false;
                }
            });
        }
    }

    private void TokenRefreshFailed(Exception ex)
    {
        var failure = PlayFabFailure.FromException(ex, "The match directory could not refresh your sign-in.");
        var retryable = failure.Status is PlatformStatus.NetworkFailure or PlatformStatus.TimedOut
            or PlatformStatus.Canceled;
        _refreshTokenAt = retryable ? DateTimeOffset.UtcNow.AddSeconds(10) : DateTimeOffset.MaxValue;
        CrashLog.Mark($"playfab: Lobby token refresh failed: {ex.Message}");
        if (!retryable)
        {
            ConnectionLost?.Invoke(failure);
        }
    }

    internal static DateTimeOffset RefreshAt(EntityToken token, DateTimeOffset now)
    {
        var refresh = token.Expiration?.Subtract(TimeSpan.FromMinutes(5)) ?? now.AddMinutes(10);
        // The SDK may return the same cached token until it renews in the background.
        return refresh > now.AddSeconds(30) ? refresh : now.AddSeconds(30);
    }

    internal static PlayFabLobby Snapshot(LobbySearchResult row) => new()
    {
        LobbyId = row.LobbyId ?? string.Empty,
        ConnectionString = row.ConnectionString ?? string.Empty,
        JoinCode = row.SearchProperties?.GetValueOrDefault(JoinCodeSearchKey) ?? string.Empty,
        ProtocolVersion = row.SearchProperties?.GetValueOrDefault(NRProtocol.LobbyKey) ?? string.Empty,
        OwnerEntityId = row.OwnerEntity?.Id ?? string.Empty,
        MaxPlayers = (int)row.MaxMemberCount,
        CurrentPlayers = (int)row.CurrentMemberCount,
    };

    private static PlayFabLobby Snapshot(Lobby lobby) => new()
    {
        LobbyId = lobby.Id ?? string.Empty,
        ConnectionString = lobby.ConnectionString ?? string.Empty,
        JoinCode = lobby.SearchProperties.GetValueOrDefault(JoinCodeSearchKey) ?? string.Empty,
        ProtocolVersion = lobby.SearchProperties.GetValueOrDefault(NRProtocol.LobbyKey) ?? string.Empty,
        OwnerEntityId = lobby.Owner?.Id ?? string.Empty,
        MaxPlayers = (int)lobby.MaxMemberCount,
        CurrentPlayers = lobby.Members.Count,
        LobbyData = new Dictionary<string, string>(lobby.Properties, StringComparer.Ordinal),
    };

    internal void Reset()
    {
        Interlocked.Increment(ref _revision);
        _commands.Enqueue(BeginReset);
    }

    private void BeginReset()
    {
        if (_resetting)
        {
            return;
        }
        _resetting = true;
        _resetDeadline = Environment.TickCount64 + 1000;
        _refreshTokenAt = DateTimeOffset.MaxValue;
        _refreshingToken = false;
        foreach (var pending in _pending.Values)
        {
            pending.Cancel();
        }
        foreach (var (id, lobby) in _lobbies)
        {
            if (lobby.IsValid)
            {
                try
                {
                    RetireLobby(lobby, _hosted.Contains(id));
                }
                catch (Exception ex) when (PlayFabFailure.IsExpected(ex))
                {
                    CrashLog.Mark($"playfab: lobby could not be retired: {ex.Message}");
                }
            }
        }
        _lobbies.Clear();
        _hosted.Clear();
    }

    private void CompleteReset()
    {
        if (_pending.Count > 0 || _retiring.Count > 0)
        {
            CrashLog.Mark("playfab: cleanup ended before all lobby operations drained.");
        }
        _pending.Clear();
        _retiring.Clear();
        _closing.Clear();
        _multiplayer?.Dispose();
        _multiplayer = null;
        _refreshTokenAt = DateTimeOffset.MaxValue;
        _refreshingToken = false;
        _resetting = false;
    }

    private void RetireLobby(Lobby lobby, bool close)
    {
        if (close && lobby.Owner is { } owner)
        {
            try
            {
                var operation = lobby.PostUpdate(owner, new LobbyDataUpdate
                {
                    AccessPolicy = LobbyAccessPolicy.Private,
                    MembershipLock = LobbyMembershipLock.Locked,
                });
                _closing.Add(operation, lobby);
                _retiring.Add(operation);
                return;
            }
            catch (Exception ex) when (PlayFabFailure.IsExpected(ex))
            {
                CrashLog.Mark($"playfab: lobby close failed, attempting to leave: {ex.Message}");
            }
        }
        _retiring.Add(lobby.Leave());
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Interlocked.Increment(ref _revision);
        Pump();
        BeginReset();
        while (_multiplayer is not null && (_pending.Count > 0 || _retiring.Count > 0)
            && Environment.TickCount64 < _resetDeadline)
        {
            try
            {
                ProcessChanges();
            }
            catch (Exception ex) when (PlayFabFailure.IsExpected(ex))
            {
                CrashLog.Mark($"playfab: lobby shutdown pump failed: {ex.Message}");
                break;
            }
            if (_pending.Count > 0 || _retiring.Count > 0)
            {
                Thread.Sleep(1);
            }
        }
        CompleteReset();
    }

    private sealed record PendingOperation(
        CancellationToken Token, Action Cancel, Action<LobbyOperationCompleted> Complete);
}
