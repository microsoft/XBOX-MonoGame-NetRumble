namespace NetRumble.Platform;

/// <summary>
/// Persistent storage for settings, match history and progression.
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>scripts/services/game_save_service.gd</c> plus the <c>user://</c>
/// paths in <c>PlayerProfile</c> and <c>Services.MATCH_HISTORY_PATH</c>.
/// </para>
/// <para>
/// Two tiers. <see cref="SaveLocalAsync"/> always works — it is plain local storage
/// and backs settings and match history so the front end runs offline. Cloud methods
/// are best-effort and no-op without a signed-in user.
/// </para>
/// </remarks>
public interface IGameSaveService
{
    /// <summary>Writes a local blob under the title's storage root. Always available.</summary>
    Task<PlatformResult> SaveLocalAsync(
        string key,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a local blob. Returns an empty result when absent.</summary>
    Task<PlatformResult<byte[]>> LoadLocalAsync(
        string key,
        CancellationToken cancellationToken = default);

    /// <summary>Writes to cloud save. Best-effort.</summary>
    Task<PlatformResult> SaveCloudAsync(
        string key,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default);

    /// <summary>Reads from cloud save. Best-effort.</summary>
    Task<PlatformResult<byte[]>> LoadCloudAsync(
        string key,
        CancellationToken cancellationToken = default);
}
