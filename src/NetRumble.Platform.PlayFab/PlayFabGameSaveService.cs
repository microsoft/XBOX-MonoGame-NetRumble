using GDK.Net.PlayFab;
using NetRumble.Platform.Offline;

namespace NetRumble.Platform.PlayFab;

/// <summary>Local files plus PlayFab user data accessed through GDK.Net.</summary>
internal sealed class PlayFabGameSaveService : LocalFileGameSaveService
{
    public const int MaxCloudBytes = 750;
    private readonly Func<PlayFabEntityLease?> _acquireEntity;
    private readonly CancellationToken _shutdown;
    private readonly PlayFabConfiguration _configuration;

    internal PlayFabGameSaveService(
        PlayFabConfiguration configuration, Func<PlayFabEntityLease?> acquireEntity, CancellationToken shutdown = default,
        string? root = null, Func<string?>? currentUserId = null)
        : base(root, currentUserId)
    {
        _acquireEntity = acquireEntity;
        _shutdown = shutdown;
        _configuration = configuration;
    }

    public override async Task<PlatformResult> SaveCloudAsync(
        string key, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key) || data.Length > MaxCloudBytes)
        {
            return PlatformResult.Fail(PlatformStatus.Failed, "Could not save your data.",
                $"Invalid user-data key or payload exceeds {MaxCloudBytes} bytes.");
        }
        if (_configuration.IsPlaceholder)
        {
            return PlatformResult.Unavailable(_configuration.UnavailableReason);
        }
        try
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown);
            lifetime.Token.ThrowIfCancellationRequested();
            using var lease = _acquireEntity();
            if (lease is null)
            {
                return PlatformResult.Fail(PlatformStatus.NotSignedIn, "Sign in before saving online.");
            }
            await PlayerDataManagement.ClientUpdateUserDataAsync(lease.Entity,
                new PlayerDataManagementClientUpdateUserDataRequest
                {
                    Data = new Dictionary<string, string> { [key] = Convert.ToBase64String(data.Span) },
                }, lifetime.Token).ConfigureAwait(false);
            return PlatformResult.Ok();
        }
        catch (Exception ex) when (PlayFabFailure.IsExpected(ex))
        {
            return PlayFabFailure.FromException(ex, "Could not save your data online.");
        }
    }

    public override async Task<PlatformResult<byte[]>> LoadCloudAsync(
        string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return PlatformResult<byte[]>.Fail(PlatformStatus.Failed, "Could not read your saved data.",
                "The user-data key was empty.");
        }
        if (_configuration.IsPlaceholder)
        {
            return PlatformResult<byte[]>.Unavailable(_configuration.UnavailableReason);
        }
        try
        {
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown);
            lifetime.Token.ThrowIfCancellationRequested();
            using var lease = _acquireEntity();
            if (lease is null)
            {
                return PlatformResult<byte[]>.Fail(PlatformStatus.NotSignedIn, "Sign in before loading online.");
            }
            var result = await PlayerDataManagement.ClientGetUserDataAsync(lease.Entity,
                new PlayerDataManagementGetUserDataRequest { Keys = [key] }, lifetime.Token).ConfigureAwait(false);
            return ReadUserDataValue(result.Data, key);
        }
        catch (Exception ex) when (PlayFabFailure.IsExpected(ex))
        {
            return PlayFabFailure.For<byte[]>(PlayFabFailure.FromException(ex, "Could not read your saved data."));
        }
    }

    internal static PlatformResult<byte[]> ReadUserDataValue(
        IReadOnlyDictionary<string, UserDataRecord>? data, string key)
    {
        if (data is null || !data.TryGetValue(key, out var record) || string.IsNullOrEmpty(record.Value))
        {
            return PlatformResult<byte[]>.Ok([]);
        }
        try
        {
            return PlatformResult<byte[]>.Ok(Convert.FromBase64String(record.Value));
        }
        catch (FormatException ex)
        {
            return PlatformResult<byte[]>.Fail(PlatformStatus.Failed, "Your saved data could not be read.",
                $"User data key '{key}' is not Base64: {ex.Message}");
        }
    }
}
