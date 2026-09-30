using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace NetRumble.Platform.PlayFab;

/// <summary>
/// Turns a credential into a <see cref="PlayFabSession"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the piece the port has been missing. Every online feature - Party, Lobby,
/// cloud save - is gated on a PlayFab entity id, and until now nothing
/// could produce one, so <see cref="PlatformUser.EntityId"/> was always empty and
/// <c>GameCorePartyService</c> refused at the door.
/// </para>
/// <para>
/// Two ways in, and the difference is worth being clear about because it decides what
/// can be tested and where:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     <see cref="LoginWithXboxAsync"/> is the shipping path. It takes an XSTS token
///     obtained from the GDK (<c>XUserGetTokenAndSignatureAsync</c> against
///     <c>https://playfabapi.com/</c>) and is the only path that yields a real,
///     Xbox-backed identity. It cannot run without a console or a signed-in Xbox user.
///     </description>
///   </item>
///   <item>
///     <description>
///     <see cref="LoginWithCustomIdAsync"/> is the developer path behind
///     <c>--pf-user=&lt;name&gt;</c>. It needs no Xbox account, no GDK and no console -
///     just a title id - which makes it the only way to exercise the entity chain, the
///     lobby on a desktop dev box, and the reason two instances
///     can be run side by side on one machine when the GDK permits only one Xbox user.
///     </description>
///   </item>
/// </list>
/// <para>
/// <b>Custom-id login must never reach a shipping build.</b> It authenticates nobody:
/// anyone who guesses the string owns the account, and PlayFab creates it on first use.
/// Enforcing that is the caller's job - see <see cref="SignInOptions.DeveloperCustomId"/>,
/// which providers are required to ignore outside debug desktop builds - because this
/// class cannot tell what build it is in.
/// </para>
/// </remarks>
internal sealed class PlayFabAuthClient
{
    private readonly PlayFabRestClient _client;

    public PlayFabAuthClient(PlayFabRestClient client) => _client = client;

    /// <summary>
    /// Signs in with an Xbox Live XSTS token. The shipping path.
    /// </summary>
    /// <param name="xboxToken">
    /// The full <c>XBL3.0 x=&lt;userhash&gt;;&lt;token&gt;</c> string as the GDK returns
    /// it. PlayFab wants it exactly as issued - not the bare token, not URL-encoded -
    /// and rejects anything else as an invalid Xbox token, which is an unhelpfully
    /// generic error to have to diagnose.
    /// </param>
    /// <param name="createAccount">
    /// Whether PlayFab may provision a new publisher account when this Xbox user has
    /// none (XR-013). The caller decides from <c>XUserGetAgeGroup</c>; child and
    /// unknown-age accounts pass false, because parental consent has to come from a
    /// compliant account flow and cannot be inferred from XBOX network participation.
    /// With false, PlayFab signs in to an existing account and returns
    /// <c>AccountNotFound</c> rather than creating one.
    /// </param>
    public Task<PlatformResult<PlayFabSession>> LoginWithXboxAsync(
        string xboxToken,
        bool createAccount,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(xboxToken))
        {
            return Task.FromResult(PlatformResult<PlayFabSession>.Fail(
                PlatformStatus.NotSignedIn,
                "Could not read your Xbox account.",
                "LoginWithXbox called with an empty XSTS token."));
        }

        return LoginAsync(
            "/Client/LoginWithXbox",
            new XboxLoginRequest
            {
                TitleId = _client.Configuration.TitleId,
                XboxToken = xboxToken,
                CreateAccount = createAccount,
            },
            PlayFabJsonContext.Default.XboxLoginRequest,
            cancellationToken);
    }

    /// <summary>
    /// Signs in with a developer custom id. Debug desktop builds only.
    /// </summary>
    /// <remarks>
    /// This one does still create its account unconditionally, and XR-013 does not apply
    /// to it: the id comes from the <c>--pf-user</c> command-line override, there is no
    /// XBOX account behind it to have an age group, and the path is compiled out of any
    /// build that can be submitted.
    /// </remarks>
    public Task<PlatformResult<PlayFabSession>> LoginWithCustomIdAsync(
        string customId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(customId))
        {
            return Task.FromResult(PlatformResult<PlayFabSession>.Fail(
                PlatformStatus.NotSignedIn,
                "Could not sign in.",
                "LoginWithCustomID called with an empty id."));
        }

        return LoginAsync(
            "/Client/LoginWithCustomID",
            new CustomIdLoginRequest
            {
                TitleId = _client.Configuration.TitleId,
                CustomId = customId,
                CreateAccount = true,
            },
            PlayFabJsonContext.Default.CustomIdLoginRequest,
            cancellationToken);
    }

    private async Task<PlatformResult<PlayFabSession>> LoginAsync<TRequest>(
        string path,
        TRequest request,
        JsonTypeInfo<TRequest> requestTypeInfo,
        CancellationToken cancellationToken)
    {
        var response = await _client
            .PostAsync(path, request, requestTypeInfo, PlayFabAuth.None, session: null, cancellationToken)
            .ConfigureAwait(false);

        if (response.Failed)
        {
            // Preserve the transport's status and diagnostics verbatim. Collapsing a
            // network failure and a rejected token into one "sign-in failed" is exactly
            // the loss of information that makes sign-in bugs expensive.
            return PlatformResult<PlayFabSession>.Fail(
                response.Status,
                response.Message ?? "Could not sign in.",
                response.Result.Diagnostics);
        }

        return ReadSession(response.Value);
    }

    /// <summary>
    /// Extracts a session from a login response body.
    /// </summary>
    /// <remarks>
    /// Internal so the spike can run it over a captured PlayFab login payload. That is
    /// the only way to prove this parsing is right without a live title, and the shape
    /// is fiddly enough to be worth proving: the entity key is nested two levels deep
    /// inside <c>EntityToken</c>, which is itself an object rather than the string its
    /// name suggests.
    /// </remarks>
    internal static PlatformResult<PlayFabSession> ReadSession(JsonElement data)
    {
        var sessionTicket = PlayFabRestClient.ReadString(data, "SessionTicket");
        var playFabId = PlayFabRestClient.ReadString(data, "PlayFabId");

        if (sessionTicket.Length == 0)
        {
            return PlatformResult<PlayFabSession>.Fail(
                PlatformStatus.Failed,
                "Could not sign in to the online service.",
                "Login response was missing the client session ticket.");
        }

        if (!data.TryGetProperty("EntityToken", out var entityToken)
            || entityToken.ValueKind != JsonValueKind.Object)
        {
            // A login that returns no entity token is a title configuration problem, not
            // a transient one, so it is worth naming precisely rather than retrying.
            return PlatformResult<PlayFabSession>.Fail(
                PlatformStatus.Failed,
                "Could not sign in to the online service.",
                "Login succeeded but the response carried no EntityToken object.");
        }

        var token = PlayFabRestClient.ReadString(entityToken, "EntityToken");

        var entityId = string.Empty;
        var entityType = PlayFabSession.TitlePlayerEntityType;
        if (entityToken.TryGetProperty("Entity", out var entity)
            && entity.ValueKind == JsonValueKind.Object)
        {
            entityId = PlayFabRestClient.ReadString(entity, "Id");
            var type = PlayFabRestClient.ReadString(entity, "Type");
            if (type.Length > 0)
            {
                entityType = type;
            }
        }

        if (token.Length == 0 || entityId.Length == 0)
        {
            return PlatformResult<PlayFabSession>.Fail(
                PlatformStatus.Failed,
                "Could not sign in to the online service.",
                "Login response was missing the entity token or the entity id.");
        }

        // PlayFab sends TokenExpiration as ISO-8601 UTC. If it is ever absent or
        // unparseable, assume the shortest lifetime PlayFab actually issues (24h) is
        // already spent rather than treating the token as immortal: an over-eager
        // refresh costs one request, a stale token costs a failed match join.
        var expires = DateTimeOffset.UtcNow;
        var expirationText = PlayFabRestClient.ReadString(entityToken, "TokenExpiration");
        if (expirationText.Length > 0
            && DateTimeOffset.TryParse(
                expirationText,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal
                    | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            expires = parsed;
        }

        var created = data.TryGetProperty("NewlyCreated", out var newlyCreated)
            && newlyCreated.ValueKind == JsonValueKind.True;

        return PlatformResult<PlayFabSession>.Ok(new PlayFabSession
        {
            SessionTicket = sessionTicket,
            EntityToken = token,
            EntityId = entityId,
            EntityType = entityType,
            PlayFabId = playFabId,
            EntityTokenExpires = expires,
            AccountWasCreated = created,
        });
    }

    internal sealed class XboxLoginRequest
    {
        public required string TitleId { get; init; }
        public required string XboxToken { get; init; }
        public required bool CreateAccount { get; init; }
    }

    internal sealed class CustomIdLoginRequest
    {
        public required string TitleId { get; init; }
        public required string CustomId { get; init; }
        public required bool CreateAccount { get; init; }
    }
}
