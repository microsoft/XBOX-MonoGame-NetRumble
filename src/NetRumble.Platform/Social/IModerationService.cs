namespace NetRumble.Platform;

/// <summary>
/// Text/name moderation and player reporting. Ported from
/// <c>scripts/services/moderation_service.gd</c>.
/// </summary>
public interface IModerationService
{
    /// <summary>
    /// Runs platform string verification over user-entered text (XR-018). Returns the
    /// sanitized text, or a failure explaining why it was refused.
    /// </summary>
    Task<PlatformResult<string>> VerifyTextAsync(
        string text,
        CancellationToken cancellationToken = default);

    /// <summary>Submits a player report.</summary>
    Task<PlatformResult> ReportPlayerAsync(
        string targetXboxUserId,
        PlayerReportType reportType,
        string reason = "",
        CancellationToken cancellationToken = default);

    /// <summary>Shows the platform profile card, which carries the report/block actions.</summary>
    Task<PlatformResult> ShowProfileCardAsync(
        string targetXboxUserId,
        CancellationToken cancellationToken = default);
}

public enum PlayerReportType
{
    Communications,
    Cheating,
    UnsportingBehavior,
    Other,
}
