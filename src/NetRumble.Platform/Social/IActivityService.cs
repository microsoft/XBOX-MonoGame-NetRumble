namespace NetRumble.Platform;

/// <summary>
/// Multiplayer activity, invites, presence and recent players.
/// </summary>
/// <remarks>
/// Ported from <c>scripts/services/activity_service.gd</c>. Drives XR-064 / XR-124 /
/// XR-067: a joinable activity must be published while the session is joinable and
/// deleted when it is not.
/// </remarks>
public interface IActivityService
{
    /// <summary>
    /// Raised when the user accepts an invite or a "join game" from the platform UI.
    /// The payload is a connection string for
    /// <see cref="IPartyService.JoinByConnectionStringAsync"/>.
    /// </summary>
    event Action<string>? InviteAccepted;

    /// <summary>Publishes or updates the joinable activity for the current session.</summary>
    Task<PlatformResult> SetActivityAsync(
        string connectionString,
        int maxPlayers,
        int currentPlayers,
        string groupId = "",
        CancellationToken cancellationToken = default);

    /// <summary>Clears the activity. Called whenever a session ends.</summary>
    Task<PlatformResult> DeleteActivityAsync(CancellationToken cancellationToken = default);

    /// <summary>Shows the platform invite composer.</summary>
    Task<PlatformResult> ShowInviteUiAsync(CancellationToken cancellationToken = default);

    /// <summary>Which of the given players are in a joinable activity, keyed by Xbox user id.</summary>
    Task<PlatformResult<IReadOnlyDictionary<string, string>>> GetJoinableActivitiesAsync(
        IReadOnlyList<string> xboxUserIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports players encountered this session (XR-067). Providers must de-duplicate;
    /// the game may call this repeatedly as the roster changes.
    /// </summary>
    Task<PlatformResult> ReportRecentPlayersAsync(
        IReadOnlyList<string> xboxUserIds,
        CancellationToken cancellationToken = default);

    /// <summary>Flushes any batched recent-player reports.</summary>
    Task<PlatformResult> FlushRecentPlayersAsync(CancellationToken cancellationToken = default);

    /// <summary>Sets rich presence.</summary>
    Task<PlatformResult> SetPresenceAsync(string status, CancellationToken cancellationToken = default);
}
