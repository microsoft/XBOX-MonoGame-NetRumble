using GDK.Net;
using GDK.Net.GameSave;
using GDK.Net.SystemInfo;
using GDK.Net.Users;
using NetRumble.Platform.Diagnostics;
using NetRumble.Platform.Offline;

namespace NetRumble.Platform.GameCore.Services;

/// <summary>
/// Adds a cloud tier to <see cref="LocalFileGameSaveService"/> using the GDK's connected
/// storage - <c>XGameSave</c>, reached through GDK.Net's
/// <see cref="GameSaveProvider"/> (XR-050, M10).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this rather than PlayFab user data.</b> The cloud tier used to be PlayFab
/// <c>/Client/UpdateUserData</c>, which works anywhere and needs no console - but it is
/// not the save system XBOX certification is written against. Connected storage is what
/// roams: the platform syncs a container between every device the account signs in on,
/// resolves conflicts, survives the title being suspended mid-write, and is what the
/// shell's own "syncing your saved data" UI is driven by. A title that stores progress
/// in PlayFab user data instead looks to the platform like a title with no saved data at
/// all.
/// </para>
/// <para>
/// <b>One container, one blob per key.</b> The container is the unit the platform syncs
/// and resolves conflicts on, so putting settings, achievement stats and match history
/// in one container means they move as a set and cannot be restored half from one device
/// and half from another. Splitting them would buy nothing: the whole set is smaller than
/// a single blob's ceiling.
/// </para>
/// <para>
/// <b>The local tier still exists and still comes first.</b> Settings and match history
/// are read before sign-in completes, so the inherited local tier is what boots the
/// front end; connected storage is the roaming copy on top. That layering is the base
/// class's contract, not an extra this class invented.
/// </para>
/// <para>
/// <b>Nothing here has been observed running.</b> Connected storage needs a signed-in
/// account on a real device, and a desktop GDK will initialize a provider but has no
/// second device to roam to. The code path is written against the API contract; see
/// <c>docs/design-notes.md</c> for what still has to be proven on hardware.
/// </para>
/// </remarks>
internal sealed class GameCoreGameSaveService : LocalFileGameSaveService, IDisposable
{
    /// <summary>
    /// The container every one of this title's blobs lives in.
    /// </summary>
    /// <remarks>
    /// Container names are restricted to letters, digits, underscore, hyphen and period,
    /// so this is deliberately plain. It is also permanent: renaming it in a later build
    /// would orphan every existing player's saved data, because the platform keys the
    /// roamed container on this string.
    /// </remarks>
    private const string ContainerName = "NetRumbleSave";

    /// <summary>What the shell shows when it talks about this container.</summary>
    private const string ContainerDisplayName = "NetRumble";

    /// <summary>
    /// Largest blob this title will write.
    /// </summary>
    /// <remarks>
    /// Connected storage itself allows far more than this - blobs run to megabytes and a
    /// container to hundreds of them - so the cap is not the platform's, it is a sanity
    /// bound on a title whose entire save set is settings, ten achievement counters and a
    /// short match history. A write larger than this is a bug in the caller, and failing
    /// at the door with a diagnostic that says so beats filling a player's quota.
    /// </remarks>
    public const int MaxCloudBytes = 256 * 1024;

    private readonly GameCoreRuntime _runtime;
    private readonly GameCoreIdentityService _identity;

    private GameSaveProvider? _provider;
    private User? _boundUser;
    private bool _disposed;

    /// <param name="runtime">The Gaming Runtime, for its pump dispatcher.</param>
    /// <param name="identity">Lends the signed-in <see cref="User"/> the provider is bound to.</param>
    /// <param name="root">The local save root. See the base class.</param>
    /// <param name="currentUserId">
    /// Scopes the local tier per account (XR-052). See the base class; this provider
    /// passes the xuid rather than a PlayFab entity id, because connected storage is
    /// keyed on the Xbox account and a save tier backed by it must not need a PlayFab
    /// login to decide where a local file goes.
    /// </param>
    /// <param name="legacyUserId">
    /// The key the previous backend scoped local files under, read on a miss so a build
    /// that switches backends does not look to the player like a factory reset. See
    /// <see cref="LocalFileGameSaveService"/>.
    /// </param>
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
    }

    public override async Task<PlatformResult> SaveCloudAsync(
        string key,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return PlatformResult.Fail(
                PlatformStatus.Failed,
                "Could not save your data.",
                "SaveCloudAsync called with an empty key.");
        }

        if (data.Length > MaxCloudBytes)
        {
            return PlatformResult.Fail(
                PlatformStatus.Failed,
                "That save is too large to store online.",
                $"SaveCloudAsync got {data.Length} bytes for '{key}'; this title caps a " +
                $"connected-storage blob at {MaxCloudBytes} bytes.");
        }

        if (!TryGetProvider(out var provider))
        {
            return PlatformResult.Unavailable();
        }

        try
        {
            // The container handle and the update are both native resources with a
            // defined lifetime, and an update that is never submitted is discarded - so
            // a cancellation or a throw between CreateUpdate and Submit leaves nothing
            // half-written, which is the property the write-then-move in the local tier
            // is imitating.
            using var container = provider.CreateContainer(ContainerName);
            using var update = container.CreateUpdate(ContainerDisplayName);

            update.Write(key, data.ToArray());

            await _runtime.Dispatcher
                .Marshal(update.SubmitAsync(cancellationToken))
                .ConfigureAwait(false);

            return PlatformResult.Ok();
        }
        catch (OperationCanceledException)
        {
            return PlatformResult.Canceled();
        }
        catch (GameRuntimeException ex)
        {
            CrashLog.MarkOnce(
                $"gamesave-write-{key}",
                $"gamesave: writing '{key}' to connected storage failed - {ex.Message}");

            return PlatformResult.Fail(
                PlatformStatus.Failed,
                "Could not save your data online.",
                ex.Message);
        }
    }

    public override async Task<PlatformResult<byte[]>> LoadCloudAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return PlatformResult<byte[]>.Fail(
                PlatformStatus.Failed,
                "Could not read your saved data.",
                "LoadCloudAsync called with an empty key.");
        }

        if (!TryGetProvider(out var provider))
        {
            return PlatformResult<byte[]>.Unavailable();
        }

        try
        {
            using var container = provider.CreateContainer(ContainerName);

            // Asking for a blob that is not there is an error at this API, not an empty
            // read, so the enumeration is what makes "this player has never saved" the
            // non-event it has to be: first run must not report a failure.
            var present = container
                .EnumerateBlobs()
                .Any(b => string.Equals(b.Name, key, StringComparison.Ordinal));

            if (!present)
            {
                return PlatformResult<byte[]>.Ok([]);
            }

            var blobs = await _runtime.Dispatcher
                .Marshal(container.ReadBlobsAsync([key], cancellationToken))
                .ConfigureAwait(false);

            var blob = blobs.FirstOrDefault(b => string.Equals(b.Name, key, StringComparison.Ordinal));

            return PlatformResult<byte[]>.Ok(blob?.Data ?? []);
        }
        catch (OperationCanceledException)
        {
            return PlatformResult<byte[]>.Canceled();
        }
        catch (GameRuntimeException ex)
        {
            CrashLog.MarkOnce(
                $"gamesave-read-{key}",
                $"gamesave: reading '{key}' from connected storage failed - {ex.Message}");

            return PlatformResult<byte[]>.Fail(
                PlatformStatus.Failed,
                "Could not read your saved data.",
                ex.Message);
        }
    }

    /// <summary>
    /// Lends a provider bound to the signed-in user, creating one on first use and
    /// replacing it when the account changes.
    /// </summary>
    /// <remarks>
    /// Bound per user for the same reason <see cref="GameCoreXblContext"/> binds its
    /// context per user: a provider is the storage space of one account, and keeping a
    /// previous account's provider alive across a sign-out would write one player's
    /// progress into another player's roamed container - the XR-052 failure, in the one
    /// place where it would also be replicated to every device that account owns.
    /// </remarks>
    private bool TryGetProvider(out GameSaveProvider provider)
    {
        provider = null!;

        if (_disposed || !_runtime.IsInitialized)
        {
            return false;
        }

        if (!_identity.TryGetUser(out var user))
        {
            ReleaseProvider();
            return false;
        }

        if (_provider is not null && ReferenceEquals(_boundUser, user))
        {
            provider = _provider;
            return true;
        }

        ReleaseProvider();

        try
        {
            var titleId = GameLauncher.GetXboxTitleId();
            var configuration = XboxServicesConfiguration.Resolve(
                titleId, Environment.GetCommandLineArgs());

            // Synchronous rather than the async overload: this runs behind a save or load
            // that is already off the game thread, and the initialize is what every
            // subsequent call on this provider depends on, so there is nothing useful to
            // overlap it with. syncOnDemand stays false - the platform syncs the
            // container up front, which is what makes the first read after a device
            // change return that other device's data rather than this one's stale copy.
            _provider = GameSaveProvider.Initialize(
                user, configuration.Scid, syncOnDemand: false);

            _boundUser = user;
            provider = _provider;

            CrashLog.MarkOnce(
                "gamesave-provider",
                $"gamesave: connected storage initialized on SCID {configuration.Scid} " +
                $"from {configuration.Source}");

            return true;
        }
        catch (Exception ex) when (ex is GameRuntimeException
                                      or InvalidOperationException
                                      or DllNotFoundException
                                      or EntryPointNotFoundException)
        {
            // XR-055: a wrong SCID fails here rather than at the first write, which is
            // the better of the two - but only if somebody is told, because the cloud
            // tier degrades to "unavailable" and the local tier carries on working, so
            // nothing the player sees would otherwise change.
            CrashLog.MarkOnce(
                "gamesave-provider",
                $"gamesave: connected storage is unavailable - {ex.Message}");

            return false;
        }
    }

    private void ReleaseProvider()
    {
        _provider?.Dispose();
        _provider = null;
        _boundUser = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseProvider();
    }
}
