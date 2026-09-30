using GDK.Net;
using GDK.Net.XboxLive;
using NetRumble.Platform.Diagnostics;

namespace NetRumble.Platform.GameCore.Services;

/// <summary>
/// GDK text moderation, player reporting and the system profile card.
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>scripts/services/moderation_service.gd</c>. Three surfaces, matching
/// the Godot source's own three: <see cref="StringVerificationService.VerifyStringAsync"/>
/// before any player-authored chat text is sent,
/// <see cref="SocialService.SubmitReputationFeedbackAsync"/> for reporting a player, and
/// <see cref="GameUiManager.ShowPlayerProfileCardAsync"/> for the system profile card
/// that carries Xbox's own blocking and evidence-based reporting.
/// </para>
/// <para>
/// <b>Verification fails closed; everything else fails open.</b> This is the one
/// asymmetry the Godot source calls out explicitly and it is preserved exactly: a
/// privilege check that cannot reach the service assumes "allowed", because refusing a
/// match over a timed-out query is worse than the risk it guards against. Publishing
/// unverified player text *is* the violation being guarded against, so a failed - or
/// unreachable - verification call holds the message back rather than letting it
/// through. XR-018 admits no exception for a missing Xbox Services context: on this
/// provider that is a broken moderation service, not an absent audience. Offline and LAN
/// chat reach <c>OfflineModerationService</c> instead.
/// </para>
/// <para>
/// GDK.Net's projection is fail-closed by construction, which lines up with that: a
/// check that could not complete comes back with
/// <see cref="StringVerificationResult.WasVerified"/> <see langword="false"/> rather than
/// as an exception, and is reported here as unverified rather than as a rejection - the
/// player is told to try again, not that they said something wrong.
/// </para>
/// <para>
/// <b>Reporting is not gated on the communications privilege.</b> Matches the Godot
/// source's own comment: an account that may not chat can still be on the receiving end
/// of something worth reporting.
/// </para>
/// <para>
/// <b>Unverifiable in this environment.</b> All three calls need a live Xbox Services
/// connection and, for the profile card, a real target xuid. This class was verified to
/// build and to degrade cleanly with no context; the actual service responses were not
/// observed. See <c>docs/design-notes.md</c>.
/// </para>
/// </remarks>
internal sealed class GameCoreModerationService : IModerationService
{
    /// <summary>
    /// What the player is told when their own message is held back. Deliberately vague,
    /// matching the Godot source: the service reports the first offending substring, and
    /// quoting it back is both a filter-bypass hint and a second exposure of the text.
    /// </summary>
    private const string RejectedMessage = "That message can't be sent. Please rephrase it.";

    private const string UnverifiedMessage = "That message couldn't be checked right now. Please try again.";

    private readonly GameCoreRuntime _runtime;
    private readonly GameCoreIdentityService _identity;
    private readonly GameCoreXblContext _xbl;

    internal GameCoreModerationService(GameCoreRuntime runtime, GameCoreIdentityService identity, GameCoreXblContext xbl)
    {
        _runtime = runtime;
        _identity = identity;
        _xbl = xbl;
    }

    public async Task<PlatformResult<string>> VerifyTextAsync(string text, CancellationToken cancellationToken = default)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return PlatformResult<string>.Fail(PlatformStatus.Failed, RejectedMessage);
        }

        if (!_runtime.IsInitialized || !_identity.TryGetUser(out _)
            || !_xbl.TryGetContext(out var context))
        {
            // XR-018 fails closed here too. This provider is only reached on a live GDK
            // session, so "no Xbox Services context" does not mean "offline play with
            // nobody to protect" - it means a moderation service that should be there is
            // not answering, while player-authored text is still about to be published
            // to other people. Offline and LAN chat never come through this class; they
            // reach OfflineModerationService, which still passes text through.
            CrashLog.MarkOnce(
                "verify-no-context",
                "moderation: no Xbox Services context, holding player text back (XR-018)");

            return PlatformResult<string>.Fail(PlatformStatus.Unavailable, UnverifiedMessage);
        }

        try
        {
            var result = await _runtime.Dispatcher
                .Marshal(context.StringVerification.VerifyStringAsync(trimmed, cancellationToken))
                .ConfigureAwait(false);

            if (result.IsAcceptable)
            {
                return PlatformResult<string>.Ok(trimmed);
            }

            // Fails closed: the service is known to be available (the context was
            // obtained above), so anything short of an explicit pass withholds the
            // message rather than publishing something never actually checked. The two
            // cases are told apart only in what the player is asked to do about it.
            if (!result.WasVerified || result.ResultCode == VerifyStringResultCode.UnknownError)
            {
                return PlatformResult<string>.Fail(PlatformStatus.Failed, UnverifiedMessage);
            }

            // Logged for operators, never shown to the player - see the constant's own
            // remarks on why the substring itself is not surfaced.
            return PlatformResult<string>.Fail(
                PlatformStatus.Failed,
                RejectedMessage,
                result.FirstOffendingSubstring ?? result.ResultCode.ToString());
        }
        catch (GameRuntimeException ex)
        {
            return PlatformResult<string>.Fail(PlatformStatus.Failed, UnverifiedMessage, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return PlatformResult<string>.Fail(PlatformStatus.Failed, UnverifiedMessage);
        }
    }

    public async Task<PlatformResult> ReportPlayerAsync(
        string targetXboxUserId,
        PlayerReportType reportType,
        string reason = "",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetXboxUserId))
        {
            return PlatformResult.Fail(PlatformStatus.Failed, "No player was specified to report.");
        }

        if (!_runtime.IsInitialized || !_identity.TryGetUser(out _)
            || !_xbl.TryGetContext(out var context) || !ulong.TryParse(targetXboxUserId, out var targetXuid))
        {
            // A dropped report, reported honestly as a failure - matching the Godot
            // source's own choice to say the report did not go anywhere rather than
            // thank the player for one that was never sent.
            return PlatformResult.Unavailable("No Xbox session is available to submit this report.");
        }

        try
        {
            await _runtime.Dispatcher
                .Marshal(context.Social.SubmitReputationFeedbackAsync(
                    targetXuid,
                    ToFeedbackType(reportType),
                    sessionReference: null,
                    reasonMessage: reason,
                    evidenceResourceId: null,
                    cancellationToken))
                .ConfigureAwait(false);

            return PlatformResult.Ok();
        }
        catch (GameRuntimeException ex)
        {
            return PlatformResult.Fail(PlatformStatus.Failed, "The report could not be submitted.", ex.Message);
        }
        catch (OperationCanceledException)
        {
            return PlatformResult.Fail(PlatformStatus.Failed, "The report was canceled.");
        }
    }

    public async Task<PlatformResult> ShowProfileCardAsync(
        string targetXboxUserId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetXboxUserId))
        {
            return PlatformResult.Fail(PlatformStatus.Failed, "No player was specified.");
        }

        if (!_runtime.IsInitialized || _runtime.Runtime is null || !_identity.TryGetUser(out var user)
            || !ulong.TryParse(targetXboxUserId, out var targetXuid))
        {
            return PlatformResult.Unavailable("The system profile card is not available in this build.");
        }

        try
        {
            await _runtime.Dispatcher
                .Marshal(_runtime.Runtime.GameUi.ShowPlayerProfileCardAsync(user, targetXuid, cancellationToken))
                .ConfigureAwait(false);

            return PlatformResult.Ok();
        }
        catch (GameRuntimeException ex)
        {
            return PlatformResult.Fail(PlatformStatus.Failed, "The profile card could not be shown.", ex.Message);
        }
        catch (OperationCanceledException)
        {
            return PlatformResult.Fail(PlatformStatus.Failed, "The profile card was dismissed before it opened.");
        }
    }

    /// <summary>Maps the title's four report reasons to their XSAPI equivalents, matching
    /// <c>REPORT_REASONS</c> in the Godot source.</summary>
    private static ReputationFeedbackType ToFeedbackType(PlayerReportType reportType) => reportType switch
    {
        PlayerReportType.Communications => ReputationFeedbackType.CommunicationsAbusiveVoice,
        PlayerReportType.Cheating => ReputationFeedbackType.FairPlayCheater,
        PlayerReportType.UnsportingBehavior => ReputationFeedbackType.FairPlayUnsporting,
        PlayerReportType.Other => ReputationFeedbackType.InappropriateUserGeneratedContent,
        _ => ReputationFeedbackType.InappropriateUserGeneratedContent,
    };
}
