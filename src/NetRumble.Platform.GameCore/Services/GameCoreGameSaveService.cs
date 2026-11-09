using GDK.Net;
using GDK.Net.GameSave;
using GDK.Net.SystemInfo;
using GDK.Net.Users;
using NetRumble.Platform.Diagnostics;
using NetRumble.Platform.Offline;

namespace NetRumble.Platform.GameCore.Services;

/// <summary>
/// Adds roaming saves through GDK.Net's <see cref="GameSaveFiles"/> wrapper.
/// The platform synchronizes the returned folder; the title uses ordinary file I/O.
/// </summary>
internal sealed class GameCoreGameSaveService : LocalFileGameSaveService, ICloudSaveSynchronization, IDisposable
{
    // Keep the old container/key names: XGameSaveFiles maps existing blobs to these paths.
    private const string ContainerName = "NetRumbleSave";
    public const int MaxCloudBytes = 256 * 1024;

    private readonly GameCoreRuntime _runtime;
    private readonly GameCoreIdentityService _identity;
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _cloudGate = new(1, 1);
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private CancellationTokenSource _sessionCancellation = new();
    private User? _saveUser;
    private SaveFolder? _folder;
    private long _revision;
    private bool _disposed;
    private bool _suspended;
    private bool _requiresReconciliation;

    public bool RequiresReconciliation
    {
        get { lock (_stateGate) { return _requiresReconciliation; } }
    }

    internal GameCoreGameSaveService(
        GameCoreRuntime runtime,
        GameCoreIdentityService identity,
        string? root,
        Func<string?> currentUserId,
        Func<string?>? legacyUserId = null)
        : base(root, currentUserId, legacyUserId)
    {
        _runtime = runtime;
        _identity = identity;
        _saveUser = identity.TryGetUser(out var user) ? user : null;
        _identity.UserChanged += OnUserChanged;
        _runtime.LifecycleChanged += OnLifecycleChanged;
    }

    public override Task<PlatformResult> SaveCloudAsync(
        string key, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        => !_runtime.IsInitialized
            ? Task.FromResult(PlatformResult.Unavailable("Sign in to Xbox to access roaming saves."))
            : _runtime.Dispatcher.Marshal(SaveCloudWorkerAsync(key, data, cancellationToken));

    private async Task<PlatformResult> SaveCloudWorkerAsync(
        string key, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (!IsValidCloudKey(key))
        {
            return InvalidKey<bool>().Result;
        }
        if (data.Length > MaxCloudBytes)
        {
            return PlatformResult.Fail(PlatformStatus.Failed, "That save is too large to store online.",
                $"SaveCloudAsync got {data.Length} bytes for '{key}'; this title caps a save file at {MaxCloudBytes} bytes.");
        }
        var bytes = data.ToArray();
        var result = await WithFolderAsync(key, async (folder, token) =>
        {
            var directory = Path.Combine(folder.Path, ContainerName);
            var path = Path.Combine(directory, key);
            var temp = Path.Combine(directory, $"nr-{Guid.NewGuid():N}.tmp");
            try
            {
                Directory.CreateDirectory(directory);
                await File.WriteAllBytesAsync(temp, bytes, token).ConfigureAwait(false);
                lock (_stateGate)
                {
                    CheckCurrent(folder.User, folder.Revision, token);
                    if (_requiresReconciliation)
                    {
                        throw new OperationCanceledException("Roaming saves are waiting for resume reconciliation.");
                    }
                    File.Move(temp, path, overwrite: true);
                }
                return true;
            }
            finally
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    CrashLog.Mark($"gamesave: temporary save cleanup failed: {ex.Message}");
                }
            }
        }, cancellationToken, writing: true).ConfigureAwait(false);
        return result.Result;
    }

    public override Task<PlatformResult<byte[]>> LoadCloudAsync(
        string key, CancellationToken cancellationToken = default)
        => !_runtime.IsInitialized
            ? Task.FromResult(PlatformResult<byte[]>.Unavailable("Sign in to Xbox to access roaming saves."))
            : _runtime.Dispatcher.Marshal(LoadCloudWorkerAsync(key, cancellationToken));

    private Task<PlatformResult<byte[]>> LoadCloudWorkerAsync(
        string key, CancellationToken cancellationToken)
    {
        if (!IsValidCloudKey(key))
        {
            return Task.FromResult(InvalidKey<byte[]>());
        }
        return WithFolderAsync(key, async (folder, token) =>
        {
            try
            {
                await using var stream = new FileStream(Path.Combine(folder.Path, ContainerName, key),
                    FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
                if (stream.Length > MaxCloudBytes)
                {
                    throw new InvalidDataException($"Save '{key}' exceeds the {MaxCloudBytes}-byte limit.");
                }
                var bytes = new byte[checked((int)stream.Length)];
                await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
                return bytes;
            }
            catch (FileNotFoundException)
            {
                return [];
            }
            catch (DirectoryNotFoundException)
            {
                return [];
            }
        }, cancellationToken);
    }

    private async Task<PlatformResult<T>> WithFolderAsync<T>(
        string key, Func<SaveFolder, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken, bool writing = false)
    {
        using var lifetime = CreateLifetime(cancellationToken);
        var token = lifetime.Token;
        var entered = false;
        try
        {
            token.ThrowIfCancellationRequested();
            User user;
            long revision;
            lock (_stateGate)
            {
                if (_disposed || _suspended || !_runtime.IsInitialized || !_identity.TryGetUser(out user))
                {
                    return PlatformResult<T>.Unavailable("Sign in to Xbox to access roaming saves.");
                }
                if (writing && _requiresReconciliation)
                {
                    return PlatformResult<T>.Unavailable("Roaming saves are waiting for resume reconciliation.");
                }
                revision = _revision;
            }
            await _cloudGate.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            SaveFolder? folder;
            lock (_stateGate)
            {
                CheckCurrent(user, revision, token);
                folder = _folder;
            }

            if (folder is null || folder.Revision != revision || !ReferenceEquals(folder.User, user))
            {
                using var heldUser = user.Duplicate();
                var configuration = XboxServicesConfiguration.Resolve(
                    GameLauncher.GetXboxTitleId(), Environment.GetCommandLineArgs());
                var path = await GameSaveFiles.GetFolderWithUiAsync(
                    heldUser, configuration.Scid, token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                {
                    throw new InvalidOperationException("XGameSaveFiles returned an invalid save folder.");
                }
                folder = new SaveFolder(user, revision, path);
                lock (_stateGate)
                {
                    CheckCurrent(user, revision, token);
                    _folder = folder;
                }
                CrashLog.Mark($"gamesave: XGameSaveFiles initialized on SCID {configuration.Scid} from {configuration.Source}");
            }
            var value = await operation(folder, token).ConfigureAwait(false);
            lock (_stateGate)
            {
                CheckCurrent(user, revision, token);
            }
            return PlatformResult<T>.Ok(value);
        }
        catch (OperationCanceledException)
        {
            return PlatformResult<T>.Canceled();
        }
        catch (GameRuntimeException ex) when (ex.HResult == HResult.EGsUserCanceled)
        {
            return PlatformResult<T>.Canceled();
        }
        catch (Exception ex) when (ex is GameRuntimeException or IOException or InvalidDataException or UnauthorizedAccessException
            or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException
            or PlatformNotSupportedException)
        {
            CrashLog.Mark($"gamesave: XGameSaveFiles operation '{key}' failed: {ex.Message}");
            var status = ex is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException
                ? PlatformStatus.Unavailable : PlatformStatus.Failed;
            return PlatformResult<T>.Fail(status, "Could not access your saved data.", ex.Message);
        }
        finally
        {
            if (entered)
            {
                _cloudGate.Release();
            }
        }
    }

    private void CheckCurrent(User user, long revision, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_disposed || _suspended || revision != _revision || !_identity.TryGetUser(out var current)
            || !ReferenceEquals(current, user))
        {
            throw new OperationCanceledException("The signed-in account or save session changed.");
        }
    }

    private void OnUserChanged(PlatformUser? user)
    {
        lock (_stateGate)
        {
            _identity.TryGetUser(out var current);
            if (_disposed || ReferenceEquals(_saveUser, current))
            {
                return;
            }
            _saveUser = current;
            InvalidateFolder();
            _requiresReconciliation = false;
        }
        if (user is not null)
        {
            _ = InitializeAsync();
        }
    }

    private void OnLifecycleChanged(PlatformLifecycleEvent change)
    {
        if (change is PlatformLifecycleEvent.Suspending or PlatformLifecycleEvent.Resumed)
        {
            lock (_stateGate)
            {
                _suspended = change == PlatformLifecycleEvent.Suspending;
                _requiresReconciliation = true;
                InvalidateFolder();
            }
            if (change == PlatformLifecycleEvent.Resumed)
            {
                _ = InitializeAsync();
            }
        }
    }

    private Task<PlatformResult<bool>> InitializeAsync()
        => WithFolderAsync("initialize", (_, _) => Task.FromResult(true), CancellationToken.None);

    private void InvalidateFolder()
    {
        lock (_stateGate)
        {
            _revision++;
            _folder = null;
            _sessionCancellation.Cancel();
            _sessionCancellation.Dispose();
            _sessionCancellation = new CancellationTokenSource();
        }
    }

    public Task<PlatformResult> ReconcileAsync(
        Func<CancellationToken, Task<PlatformResult>> reconcile,
        CancellationToken cancellationToken = default)
        => _runtime.Dispatcher.Marshal(ReconcileWorkerAsync(reconcile, cancellationToken));

    private async Task<PlatformResult> ReconcileWorkerAsync(
        Func<CancellationToken, Task<PlatformResult>> reconcile, CancellationToken cancellationToken)
    {
        using var lifetime = CreateLifetime(cancellationToken);
        var entered = false;
        try
        {
            await _reconcileGate.WaitAsync(lifetime.Token).ConfigureAwait(false);
            entered = true;
            lock (_stateGate)
            {
                if (!_requiresReconciliation)
                {
                    return PlatformResult.Ok();
                }
            }
            var initialized = await WithFolderAsync("reconcile",
                (folder, _) => Task.FromResult(folder), lifetime.Token).ConfigureAwait(false);
            if (initialized.Failed || initialized.Value is not { } folder)
            {
                return initialized.Result;
            }
            var completion = new TaskCompletionSource<PlatformResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var token = lifetime.Token;
            _runtime.Dispatcher.Post(async () =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    completion.TrySetResult(await reconcile(token));
                }
                catch (OperationCanceledException)
                {
                    completion.TrySetCanceled(token);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            });
            var result = await completion.Task.ConfigureAwait(false);
            lock (_stateGate)
            {
                CheckCurrent(folder.User, folder.Revision, lifetime.Token);
                if (result.Succeeded)
                {
                    _requiresReconciliation = false;
                }
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            return PlatformResult.Canceled();
        }
        finally
        {
            if (entered)
            {
                _reconcileGate.Release();
            }
        }
    }

    private CancellationTokenSource CreateLifetime(CancellationToken cancellationToken)
    {
        lock (_stateGate)
        {
            return CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _shutdown.Token, _sessionCancellation.Token);
        }
    }

    internal static bool IsValidCloudKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 65 || key is "." or ".."
            || key.EndsWith('.') || key.EndsWith(' ') || key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }
        var stem = key.Split('.')[0];
        return !new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5",
            "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5",
            "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem, StringComparer.OrdinalIgnoreCase);
    }

    private static PlatformResult<T> InvalidKey<T>()
        => PlatformResult<T>.Fail(PlatformStatus.Failed, "Could not access your saved data.",
            "Save keys must be valid Windows file names of at most 65 characters.");

    public void Dispose()
    {
        lock (_stateGate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _revision++;
            _folder = null;
            _sessionCancellation.Cancel();
        }
        _identity.UserChanged -= OnUserChanged;
        _runtime.LifecycleChanged -= OnLifecycleChanged;
        _shutdown.Cancel();
    }

    internal async Task ShutdownAsync()
    {
        Dispose();
        await _cloudGate.WaitAsync().ConfigureAwait(false);
        _cloudGate.Release();
    }

    private sealed record SaveFolder(User User, long Revision, string Path);
}
