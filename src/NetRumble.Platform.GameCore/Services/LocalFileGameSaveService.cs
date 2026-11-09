using System.Text.RegularExpressions;

namespace NetRumble.Platform.GameCore.Services;

/// <summary>
/// Local-disk save storage. A real implementation, not a stub.
/// </summary>
/// <remarks>
/// <para>
/// Replaces Godot's <c>user://</c> paths (<c>PlayerProfile</c> settings and
/// <c>Services.MATCH_HISTORY_PATH</c>). Files land under
/// <c>%LOCALAPPDATA%\NetRumble\</c>.
/// </para>
/// <para>
/// Providers that add cloud save should inherit this rather than reimplement it: the
/// local tier must keep working identically when the cloud tier is added, because
/// settings and match history are read before sign-in completes.
/// </para>
/// </remarks>
public partial class LocalFileGameSaveService : IGameSaveService
{
    private readonly string _root;
    private readonly Func<string?>? _currentUserId;

    /// <summary>
    /// The unsigned session's saves, held in memory and never written to disk (XR-052).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Boot-time reads must not adopt a previous user's progress before sign-in.
    /// Until an account exists, data stays in memory so it cannot be attributed to
    /// another account later (XR-052).
    /// </para>
    /// <para>
    /// There is no file a save can belong to before an identity exists, so an unsigned
    /// session gets a bucket that lives and dies with the process. Everything still
    /// works - nothing has to special-case "no user" - and nothing survives to be
    /// attributed to the wrong person.
    /// </para>
    /// </remarks>
    private readonly Dictionary<string, byte[]> _anonymous = new(StringComparer.Ordinal);

    /// <param name="root">The save root. Defaults to <c>%LOCALAPPDATA%\NetRumble</c>.</param>
    /// <param name="currentUserId">
    /// Resolves the caller's stable id (<see cref="PlatformUser.LocalId"/> or an
    /// equivalent per-account key) at the moment of each save/load, not once at
    /// construction - the local tier is read during boot, before anyone is signed in.
    /// Every key is scoped under a per-user subfolder, so two Xbox accounts on the same
    /// console or PC never read or overwrite each other's settings, achievement stats or
    /// match history (XR-052). When this returns nothing, the save is held in memory for
    /// the life of the process instead: see <see cref="_anonymous"/>.
    /// </param>
    public LocalFileGameSaveService(
        string? root = null,
        Func<string?>? currentUserId = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetRumble");
        _currentUserId = currentUserId;
    }

    public async Task<PlatformResult> SaveLocalAsync(
        string key,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolvePath(key, out var path))
        {
            lock (_anonymous)
            {
                _anonymous[key] = data.ToArray();
            }

            return PlatformResult.Ok();
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Write-then-move so a crash mid-write cannot corrupt an existing save.
            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, data.ToArray(), cancellationToken)
                .ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);

            return PlatformResult.Ok();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PlatformResult.Fail(
                PlatformStatus.Failed,
                "Could not save your data.",
                ex.Message);
        }
    }

    public async Task<PlatformResult<byte[]>> LoadLocalAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolvePath(key, out var path))
        {
            lock (_anonymous)
            {
                return PlatformResult<byte[]>.Ok(
                    _anonymous.TryGetValue(key, out var held) ? held : []);
            }
        }

        try
        {
            if (!File.Exists(path))
            {
                // Absent is not an error: first run has no settings and no history.
                return PlatformResult<byte[]>.Ok([]);
            }

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken)
                .ConfigureAwait(false);
            return PlatformResult<byte[]>.Ok(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PlatformResult<byte[]>.Fail(
                PlatformStatus.Failed,
                "Could not read your saved data.",
                ex.Message);
        }
    }

    public virtual Task<PlatformResult> SaveCloudAsync(
        string key,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
        => Task.FromResult(PlatformResult.Unavailable());

    public virtual Task<PlatformResult<byte[]>> LoadCloudAsync(
        string key,
        CancellationToken cancellationToken = default)
        => Task.FromResult(PlatformResult<byte[]>.Unavailable());

    /// <summary>
    /// Maps a save key onto a user-scoped file path, rejecting anything that could escape
    /// the save root.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when no user is established, in which case the caller uses
    /// the in-memory <see cref="_anonymous"/> bucket instead. See its remarks for why
    /// there is no on-disk fallback (XR-052).
    /// </returns>
    private bool TryResolvePath(string key, out string path)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Save key must not be empty.", nameof(key));
        }

        var userId = _currentUserId?.Invoke();
        if (string.IsNullOrWhiteSpace(userId))
        {
            path = string.Empty;
            return false;
        }

        var safe = SafeKeyPattern().Replace(key, "_");

        path = Path.Combine(
            _root, "users", SafeKeyPattern().Replace(userId, "_"), safe + ".dat");

        return true;
    }

    [GeneratedRegex(@"[^A-Za-z0-9_\-.]")]
    private static partial Regex SafeKeyPattern();
}
