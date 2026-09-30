using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace NetRumble.Platform.PlayFab;

/// <summary>Which credential a PlayFab endpoint expects.</summary>
/// <remarks>
/// Picking the wrong one is a 401 that looks exactly like an expired token, so the
/// choice is made explicitly at every call site rather than inferred from the path.
/// </remarks>
internal enum PlayFabAuth
{
    /// <summary>Login and other pre-auth calls. No credential header.</summary>
    None,

    /// <summary><c>X-Authorization</c>: the session ticket. All <c>/Client/*</c> calls.</summary>
    SessionTicket,

    /// <summary><c>X-EntityToken</c>: the entity token. <c>/Lobby/*</c> and friends.</summary>
    EntityToken,
}

/// <summary>
/// The one place that speaks HTTP to PlayFab.
/// </summary>
/// <remarks>
/// <para>
/// Everything above this - auth, cloud save, lobby - is request shaping
/// and response reading. Concentrating the transport here means the awkward parts get
/// solved once: PlayFab's envelope, its error body, which header carries which token,
/// and the mapping from all of that onto <see cref="PlatformResult"/>.
/// </para>
/// <para>
/// <b>Every failure returns a result; none throws.</b> That is the same rule the rest
/// of the platform layer follows, and it matters more here than anywhere else, because
/// the failures are the common case on a dev box: no title id, no network, a title id
/// that was deleted, a token that expired overnight. Game code that has to wrap every
/// cloud-save read in a try/catch will eventually forget one, and the crash will
/// happen in front of a player.
/// </para>
/// <para>
/// The <see cref="HttpMessageHandler"/> is injectable so the spike can prove URL shape,
/// header selection and error mapping against canned responses. Without that seam none
/// of this could be verified without a live title, and "we think it builds the right
/// URL" is not verification.
/// </para>
/// </remarks>
internal sealed class PlayFabRestClient : IDisposable
{
    /// <summary>
    /// PlayFab's per-title host. The title id is a subdomain, which is why an
    /// unconfigured title id produces a DNS failure rather than an HTTP error.
    /// </summary>
    private const string HostFormat = "https://{0}.playfabapi.com";

    private readonly HttpClient _http;

    /// <summary>
    /// Creates a client for <paramref name="configuration"/>.
    /// </summary>
    /// <param name="handler">
    /// Transport override. Null uses a real socket-backed handler. Supplying one does
    /// not transfer ownership of it.
    /// </param>
    public PlayFabRestClient(
        PlayFabConfiguration configuration,
        HttpMessageHandler? handler = null,
        TimeSpan? timeout = null)
    {
        Configuration = configuration;

        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = new Uri(string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            HostFormat,
            configuration.TitleId));

        // Shorter than HttpClient's 100-second default on purpose. This sits in front of
        // a sign-in screen with a spinner on it; two minutes of spinner is indisting-
        // uishable from a hang, and every call here is a small JSON round trip that has
        // no business taking longer than this.
        _http.Timeout = timeout ?? TimeSpan.FromSeconds(20);

        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public PlayFabConfiguration Configuration { get; }

    /// <summary>The absolute URL a given API path resolves to. Exposed for verification.</summary>
    public Uri BuildUri(string path) => new(_http.BaseAddress!, path);

    /// <summary>
    /// Posts <paramref name="body"/> to <paramref name="path"/> and returns the
    /// envelope's <c>data</c> object.
    /// </summary>
    /// <param name="bodyTypeInfo">
    /// The source-generated contract for <typeparamref name="TBody"/>, from
    /// <see cref="PlayFabJsonContext"/>. Passed explicitly rather than looked up, so that
    /// a request type nobody registered is a build error instead of a NativeAOT-only
    /// throw - see that class's remarks.
    /// </param>
    /// <remarks>
    /// The returned element is cloned out of the parsed document, so it stays valid
    /// after this method's <c>JsonDocument</c> is disposed. Returning a view into a
    /// disposed document is the classic System.Text.Json footgun and it fails as an
    /// <see cref="ObjectDisposedException"/> far from the cause.
    /// </remarks>
    public async Task<PlatformResult<JsonElement>> PostAsync<TBody>(
        string path,
        TBody body,
        JsonTypeInfo<TBody> bodyTypeInfo,
        PlayFabAuth auth,
        PlayFabSession? session,
        CancellationToken cancellationToken = default)
    {
        // Refuse before opening a socket. A placeholder title id resolves to
        // https://PLACEHOLDER.playfabapi.com, whose DNS failure takes seconds and
        // reports as a network problem - which would send whoever is debugging it
        // looking at their wi-fi instead of their configuration.
        if (Configuration.IsPlaceholder)
        {
            return PlatformResult<JsonElement>.Unavailable(Configuration.UnavailableReason);
        }

        var credentialError = TryDescribeMissingCredential(auth, session);
        if (credentialError is not null)
        {
            return PlatformResult<JsonElement>.Fail(
                PlatformStatus.NotSignedIn,
                "You need to be signed in to do that.",
                credentialError);
        }

        var bodyJson = JsonSerializer.Serialize(body, bodyTypeInfo);

        // XR-132: PlayFab throttles (429) and its own transient 5xx failures are
        // retried with a bounded, jittered exponential backoff rather than surfaced
        // to the player on the first response - a title that hammers a throttled
        // service with immediate manual retries is exactly the client behaviour rate
        // limits exist to stop. A permanent failure (auth, bad request, etc.) is
        // never retried.
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(bodyJson, Encoding.UTF8, "application/json"),
            };

            switch (auth)
            {
                case PlayFabAuth.SessionTicket:
                    request.Headers.TryAddWithoutValidation("X-Authorization", session!.SessionTicket);
                    break;
                case PlayFabAuth.EntityToken:
                    request.Headers.TryAddWithoutValidation("X-EntityToken", session!.EntityToken);
                    break;
            }

            HttpResponseMessage response;
            string payload;
            try
            {
                response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The caller pulled out - a cancelled sign-in, a screen torn down. Not a
                // failure, and the UI must not show a dialog for it.
                return PlatformResult<JsonElement>.Canceled();
            }
            catch (OperationCanceledException)
            {
                // Cancellation without the caller's token means HttpClient.Timeout fired.
                // .NET reports that as a cancellation rather than a timeout, which is why
                // this is a separate catch and not folded into the one above.
                return PlatformResult<JsonElement>.Fail(
                    PlatformStatus.TimedOut,
                    "PlayFab did not respond in time.",
                    $"POST {path} exceeded {_http.Timeout}.");
            }
            catch (HttpRequestException ex)
            {
                return PlatformResult<JsonElement>.Fail(
                    PlatformStatus.NetworkFailure,
                    "Could not reach the online service.",
                    $"POST {path}: {DescribeExceptionChain(ex)}");
            }

            using (response)
            {
                if (attempt < MaxRetryAttempts && IsRetryable(response.StatusCode))
                {
                    var delay = RetryDelay(response, attempt);

                    try
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return PlatformResult<JsonElement>.Canceled();
                    }

                    continue;
                }

                return ReadEnvelope(path, response.StatusCode, payload);
            }
        }
    }

    /// <summary>Bounded so a persistently throttled or degraded service still fails in a
    /// predictable amount of time rather than retrying indefinitely against it.</summary>
    private const int MaxRetryAttempts = 3;

    /// <summary>The status codes worth a retry: PlayFab's own throttling and the
    /// transient half of the 5xx family. Everything else is a permanent answer.</summary>
    /// <remarks>
    /// 500 is included (XR-132). It is the one 5xx a service returns for an unhandled
    /// fault on its side, which is exactly the kind of failure a second attempt often
    /// clears, and omitting it meant a single blip took an achievement write or a save
    /// with it. The bounded backoff below is what keeps that from becoming a retry storm.
    /// </remarks>
    private static bool IsRetryable(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.TooManyRequests => true,
        HttpStatusCode.InternalServerError => true,
        HttpStatusCode.ServiceUnavailable => true,
        HttpStatusCode.BadGateway => true,
        HttpStatusCode.GatewayTimeout => true,
        _ => false,
    };

    /// <summary>
    /// How long to wait before the next attempt: honours a numeric <c>Retry-After</c>
    /// header if the service sent one, otherwise a jittered exponential backoff
    /// (roughly 0.5s, 1s, 2s before the small random offset) so retries from many
    /// clients do not all land on the service at once.
    /// </summary>
    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter is { Delta: { } delta })
        {
            return delta;
        }

        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var until = date - DateTimeOffset.UtcNow;
            if (until > TimeSpan.Zero)
            {
                return until;
            }
        }

        var baseDelayMs = 500 * (1 << attempt);
        var jitterMs = Random.Shared.Next(0, 250);
        return TimeSpan.FromMilliseconds(baseDelayMs + jitterMs);
    }

    private static string DescribeExceptionChain(Exception exception)
    {
        var description = new StringBuilder();

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (description.Length > 0)
            {
                description.Append(" -> ");
            }

            description.Append(current.GetType().Name);
            description.Append(": ");
            description.Append(current.Message);
        }

        return description.ToString();
    }

    /// <summary>
    /// Turns a PlayFab response body into a result.
    /// </summary>
    /// <remarks>
    /// Split out from <see cref="PostAsync"/> and left internal so the spike can feed
    /// it real captured bodies - including malformed ones - without a socket.
    /// </remarks>
    internal static PlatformResult<JsonElement> ReadEnvelope(
        string path,
        HttpStatusCode statusCode,
        string payload)
    {
        // PlayFab answers with JSON for both success and failure, so a body that will
        // not parse means something else replied: a proxy, a captive portal login page,
        // or a DNS wildcard. Saying so is more useful than "invalid response".
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(payload) ? "{}" : payload);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return PlatformResult<JsonElement>.Fail(
                PlatformStatus.NetworkFailure,
                "The online service sent something unexpected.",
                $"POST {path} returned {(int)statusCode} with a body that is not JSON: {ex.Message}");
        }

        if (statusCode == HttpStatusCode.OK && root.TryGetProperty("data", out var data))
        {
            return PlatformResult<JsonElement>.Ok(data.Clone());
        }

        var errorName = ReadString(root, "error");
        var errorCode = root.TryGetProperty("errorCode", out var codeElement)
            && codeElement.ValueKind == JsonValueKind.Number
                ? codeElement.GetInt32()
                : 0;
        var errorMessage = ReadString(root, "errorMessage");

        var diagnostics =
            $"POST {path} -> {(int)statusCode} {errorName} ({errorCode}): {errorMessage}";

        return PlatformResult<JsonElement>.Fail(
            MapStatus(statusCode, errorCode),
            DescribeForPlayer(statusCode, errorCode),
            diagnostics);
    }

    /// <summary>
    /// Maps PlayFab's answer onto the platform's status vocabulary.
    /// </summary>
    /// <remarks>
    /// Only the distinctions the game actually branches on are drawn. PlayFab defines
    /// hundreds of error codes; inventing a status for each would produce a mapping
    /// nobody maintains and callers that switch on values they have never seen. The
    /// exact code is preserved in the diagnostics string for whoever is reading a log.
    /// </remarks>
    private static PlatformStatus MapStatus(HttpStatusCode statusCode, int errorCode)
        => (statusCode, errorCode) switch
        {
            // 1000 InvalidParams and 1001 AccountNotFound arrive as 400s but mean the
            // caller is not who it claims, which is a sign-in problem to the player.
            (HttpStatusCode.Unauthorized, _) => PlatformStatus.NotSignedIn,
            (HttpStatusCode.Forbidden, _) => PlatformStatus.NotSignedIn,
            (_, 1001) => PlatformStatus.NotSignedIn,

            // 1074 APIRequestsDisabledForTitle and 1075 InvalidSharedGroupId aside, the
            // 5xx family and PlayFab's own throttling are transient: worth retrying,
            // not worth telling the player their account is broken.
            (HttpStatusCode.TooManyRequests, _) => PlatformStatus.NetworkFailure,
            (HttpStatusCode.ServiceUnavailable, _) => PlatformStatus.NetworkFailure,
            (HttpStatusCode.BadGateway, _) => PlatformStatus.NetworkFailure,
            (HttpStatusCode.GatewayTimeout, _) => PlatformStatus.TimedOut,
            (HttpStatusCode.RequestTimeout, _) => PlatformStatus.TimedOut,

            _ => PlatformStatus.Failed,
        };

    private static string DescribeForPlayer(HttpStatusCode statusCode, int errorCode)
        => MapStatus(statusCode, errorCode) switch
        {
            PlatformStatus.NotSignedIn => "Your session has expired. Sign in again to continue.",
            PlatformStatus.NetworkFailure => "The online service is busy. Try again in a moment.",
            PlatformStatus.TimedOut => "The online service did not respond in time.",
            _ => "The online service could not complete that request.",
        };

    /// <summary>
    /// Returns why the requested credential cannot be supplied, or null when it can.
    /// </summary>
    private static string? TryDescribeMissingCredential(PlayFabAuth auth, PlayFabSession? session)
    {
        if (auth == PlayFabAuth.None)
        {
            return null;
        }

        if (session is null)
        {
            return $"{auth} required but no PlayFab session has been established.";
        }

        return auth switch
        {
            PlayFabAuth.SessionTicket when session.SessionTicket.Length == 0
                => "Session ticket required but the session carries none.",
            PlayFabAuth.EntityToken when session.EntityToken.Length == 0
                => "Entity token required but the session carries none.",
            _ => null,
        };
    }

    internal static string ReadString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>
    /// A request body with no parameters.
    /// </summary>
    /// <remarks>
    /// Several PlayFab endpoints take none, and an anonymous <c>new { }</c> cannot be
    /// used any more: anonymous types have no name for <see cref="PlayFabJsonContext"/>
    /// to register, which is the point of routing every body through a source-generated
    /// contract. Serializes to <c>{}</c>.
    /// </remarks>
    internal sealed class EmptyRequest;

    /// <summary>
    /// Disposes the <see cref="HttpClient"/> this instance created, but never an
    /// injected handler - <c>disposeHandler: false</c> above is what guarantees that.
    /// A spike that reuses one stub handler across several clients would otherwise find
    /// it dead after the first client was disposed.
    /// </summary>
    public void Dispose() => _http.Dispose();
}
