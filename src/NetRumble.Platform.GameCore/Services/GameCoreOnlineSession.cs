using GDK.Net;
using GDK.Net.PlayFab;
using GDK.Net.Users;
using NetRumble.Platform.PlayFab;

namespace NetRumble.Platform.GameCore.Services;

/// <summary>
/// Owns the native PlayFab identity that the GameCore provider layers on top of Xbox
/// sign-in.
/// </summary>
/// <remarks>
/// <para>
/// Xbox sign-in and PlayFab sign-in are intentionally split. <see cref="GameCoreIdentityService"/>
/// owns the <see cref="User"/> because every Xbox service needs that borrowed handle;
/// this class owns the <see cref="PlayFabLocalUser"/> and <see cref="PlayFabEntity"/>
/// because Party and Lobby need the PlayFab identity that is derived from that same
/// Xbox user. Keeping the PlayFab objects here prevents a native handle from leaking
/// into <see cref="PlatformUser"/> while still allowing Party to borrow the entity
/// through <see cref="Entity"/>.
/// </para>
/// <para>
/// <b>Startup order is part of the contract.</b> GDK.Net's lifetime guard requires
/// <c>GameRuntime.Initialize()</c> first, <c>PlayFabRuntime.Initialize()</c> second and
/// <c>PartyManager.Initialize(titleId)</c> last. The runtime service performs the first
/// step; this class performs the second before any local-user handle is created; and
/// <see cref="GameCorePartyService"/> performs the third only after this class has a
/// logged-in entity.
/// </para>
/// <para>
/// <b>The REST session carries both credentials.</b> GDK.Net's
/// <see cref="PlayFabLocalUser.LoginAsync(bool, CancellationToken)"/> returns the native
/// entity handle Party needs, but it does not expose the legacy client session ticket
/// that cloud save needs. After the native login succeeds, this class
/// asks the same Xbox user for a PlayFab XSTS token and runs the existing REST
/// <c>/Client/LoginWithXbox</c> path. The result is still one Xbox-backed PlayFab
/// identity, but the session now contains both <c>X-EntityToken</c> and
/// <c>X-Authorization</c> credentials.
/// </para>
/// </remarks>
internal sealed class GameCoreOnlineSession : IDisposable
{
    /// <summary>The PlayFab relying party for <c>XUserGetTokenAndSignatureAsync</c>.</summary>
    private const string PlayFabRelyingParty = "https://playfabapi.com/";

    private readonly GameCoreRuntime _runtime;
    private readonly PlayFabConfiguration _configuration;
    private readonly PlayFabRestClient _rest;
    private readonly PlayFabAuthClient _auth;
    private readonly PlayFabLobbyClient _lobbies;
    private readonly PlayFabGameSaveService _gameSaves;

    private PlayFabServiceConfig? _serviceConfig;
    private PlayFabLocalUser? _localUser;
    private PlayFabEntity? _entity;
    private PlayFabSession? _session;
    private string _lastSignInFailure = string.Empty;
    private string? _lastSignInDiagnostics;
    private PlatformStatus _lastSignInStatus = PlatformStatus.Ok;
    private bool _playFabRuntimeInitialized;
    private bool _disposed;

    internal GameCoreOnlineSession(
        GameCoreRuntime runtime,
        PlayFabConfiguration configuration,
        string? localSaveRoot = null)
    {
        _runtime = runtime;
        _configuration = configuration;
        _rest = new PlayFabRestClient(configuration);
        _auth = new PlayFabAuthClient(_rest);
        _lobbies = new PlayFabLobbyClient(_rest, () => Session);
        // Scoped by EntityId (XR-052): the local tier must never mix one signed-in
        // account's settings, stats or history into another's on a shared console.
        _gameSaves = new PlayFabGameSaveService(_rest, () => Session, localSaveRoot, () => Session?.EntityId);
    }

    /// <summary>The native entity Party needs to create a local user.</summary>
    internal PlayFabEntity? Entity => _entity;

    /// <summary>The PlayFab entity id for the signed-in Xbox user, or empty.</summary>
    internal string EntityId => _session?.EntityId ?? string.Empty;

    /// <summary>The Xbox-backed PlayFab session used by every REST call.</summary>
    internal PlayFabSession? Session => _session;

    /// <summary>The lobby directory used by Party to publish and resolve join codes.</summary>
    internal PlayFabLobbyClient Lobbies => _lobbies;

    /// <summary>Local save plus a PlayFab user-data cloud tier.</summary>
    internal IGameSaveService GameSaves => _gameSaves;

    /// <summary>The title id and placeholder state this native session was built for.</summary>
    internal PlayFabConfiguration Configuration => _configuration;

    /// <summary>True when a real title id is configured and native PlayFab login can be attempted.</summary>
    internal bool CanAttemptLogin => !_configuration.IsPlaceholder;

    /// <summary>Why the PlayFab half of online play is unavailable, or empty when it can run.</summary>
    internal string UnavailableReason
        => _configuration.IsPlaceholder ? _configuration.UnavailableReason : _lastSignInFailure;

    /// <summary>Provider-specific detail from the latest failed PlayFab sign-in.</summary>
    internal string? LastSignInDiagnostics => _lastSignInDiagnostics;

    /// <summary>
    /// How the latest PlayFab sign-in ended, or <see cref="PlatformStatus.Ok"/> when none
    /// has failed yet.
    /// </summary>
    /// <remarks>
    /// Kept because "there is no entity" is not one condition but two, and they call for
    /// opposite handling. A title id that cannot be used, or an account that may not have
    /// a publisher account provisioned, is a standing refusal. A sign-in that timed out or
    /// could not reach the service - the shape a corporate VPN or a proxy that blocks
    /// <c>playfabapi.com</c> produces - is transient, and left indistinguishable from the
    /// first it disabled online play for the rest of the session even once the network
    /// recovered. Callers use this both to word the refusal honestly and to decide whether
    /// another attempt is worth making.
    /// </remarks>
    internal PlatformStatus LastSignInStatus => _lastSignInStatus;

    /// <summary>
    /// True when the last sign-in failure was a network condition that may since have
    /// cleared, so a user-initiated online action may retry it.
    /// </summary>
    internal bool CanRetrySignIn => _lastSignInStatus
        is PlatformStatus.Ok
        or PlatformStatus.TimedOut
        or PlatformStatus.NetworkFailure
        or PlatformStatus.Canceled;

    /// <summary>
    /// Logs the current Xbox user in to PlayFab and replaces the previous PlayFab state.
    /// The task returned by this method is marshalled through <see cref="PumpDispatcher"/>
    /// because the underlying GDK.Net login completes on the PlayFab thread-pool queue.
    /// </summary>
    /// <param name="allowAccountCreation">
    /// Whether a PlayFab publisher account may be created for this user if none exists
    /// (XR-013). Supplied by the caller from the platform age group, and false for child
    /// and unknown-age accounts, which need parental consent through a compliant flow
    /// before an account may be created for them. When false, an account that already
    /// exists is still signed in to.
    /// </param>
    internal async Task<PlatformResult<PlayFabSession>> SignInAsync(
        User user,
        bool allowAccountCreation,
        CancellationToken cancellationToken = default)
    {
        _lastSignInFailure = string.Empty;
        _lastSignInDiagnostics = null;
        _lastSignInStatus = PlatformStatus.Ok;

        if (_disposed)
        {
            return RememberFailure(
                PlatformResult<PlayFabSession>.Unavailable("The online session has been shut down."));
        }

        if (!_runtime.IsInitialized || _runtime.Runtime is null)
        {
            return RememberFailure(
                PlatformResult<PlayFabSession>.Unavailable("The Microsoft GDK is not running in this build."));
        }

        if (_configuration.IsPlaceholder)
        {
            return RememberFailure(
                PlatformResult<PlayFabSession>.Unavailable(_configuration.UnavailableReason));
        }

        Clear();
        var stage = "starting the PlayFab runtime";

        try
        {
            if (!PlayFabRuntime.IsInitialized)
            {
                PlayFabRuntime.Initialize();
                _playFabRuntimeInitialized = true;
            }

            stage = "creating the PlayFab Xbox user";
            _serviceConfig = new PlayFabServiceConfig(
                $"https://{_configuration.TitleId}.playfabapi.com",
                _configuration.TitleId);
            _localUser = PlayFabLocalUser.CreateForXboxUser(_serviceConfig, user);

            stage = "authenticating the Xbox user with PlayFab";
            using PlayFabLoginResult login = await _runtime.Dispatcher
                .Marshal(_localUser.LoginAsync(createAccount: allowAccountCreation, cancellationToken))
                .ConfigureAwait(false);

            _entity = login.Detach();
            var key = _entity.Key;

            stage = "requesting an Xbox token for PlayFab";
            var token = await _runtime.Dispatcher
                .Marshal(user.GetTokenAndSignatureAsync(
                    TokenAndSignatureOptions.None,
                    "GET",
                    PlayFabRelyingParty,
                    headers: null,
                    body: null,
                    cancellationToken))
                .ConfigureAwait(false);

            stage = "exchanging the Xbox token with PlayFab";
            var rest = await _auth.LoginWithXboxAsync(token.Token, allowAccountCreation, cancellationToken)
                .ConfigureAwait(false);

            if (rest.Failed)
            {
                Clear();
                return RememberFailure(rest);
            }

            if (rest.Value is null || string.IsNullOrEmpty(rest.Value.SessionTicket))
            {
                Clear();
                return RememberFailure(
                    PlatformResult<PlayFabSession>.Fail(
                        PlatformStatus.Failed,
                        "PlayFab returned no client session ticket.",
                        "LoginWithXbox returned no client session ticket."));
            }

            if (!string.IsNullOrEmpty(key.Id)
                && !string.Equals(key.Id, rest.Value.EntityId, StringComparison.OrdinalIgnoreCase))
            {
                Clear();
                return RememberFailure(
                    PlatformResult<PlayFabSession>.Fail(
                        PlatformStatus.Failed,
                        "The native and REST PlayFab identities did not match.",
                        $"Native PlayFab entity '{key.Id}' did not match REST entity '{rest.Value.EntityId}'."));
            }

            _session = rest.Value;

            return PlatformResult<PlayFabSession>.Ok(_session);
        }
        catch (OperationCanceledException)
        {
            Clear();
            return RememberFailure(
                PlatformResult<PlayFabSession>.Canceled("PlayFab sign-in was cancelled."));
        }
        catch (Exception ex) when (ex is GameRuntimeException
                                      or DllNotFoundException
                                      or EntryPointNotFoundException
                                      or PlatformNotSupportedException
                                      or InvalidOperationException)
        {
            Clear();
            return RememberFailure(
                PlatformResult<PlayFabSession>.Fail(
                    PlatformStatus.Failed,
                    $"PlayFab sign-in failed while {stage}.",
                    ex.Message));
        }
    }

    /// <summary>Clears the PlayFab identity without touching the Xbox user.</summary>
    internal void SignOut()
    {
        Clear();
        _lastSignInFailure = string.Empty;
        _lastSignInDiagnostics = null;
        _lastSignInStatus = PlatformStatus.Ok;
    }

    private PlatformResult<PlayFabSession> RememberFailure(PlatformResult<PlayFabSession> result)
    {
        _lastSignInStatus = result.Status;
        _lastSignInFailure =
            $"PlayFab title '{_configuration.TitleId}': " +
            (result.Message ?? "The Xbox user could not be signed in.");
        _lastSignInDiagnostics = result.Result.Diagnostics;
#if DEBUG
        if (!string.IsNullOrWhiteSpace(_lastSignInDiagnostics))
        {
            _lastSignInFailure += $" [{_lastSignInDiagnostics}]";
        }
#endif
        return result;
    }

    private void Clear()
    {
        _session = null;
        _entity?.Dispose();
        _entity = null;
        _localUser?.Dispose();
        _localUser = null;
        _serviceConfig?.Dispose();
        _serviceConfig = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Clear();
        _rest.Dispose();

        if (_playFabRuntimeInitialized && PlayFabRuntime.IsInitialized)
        {
            try
            {
                PlayFabRuntime.UninitializeAsync().Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // GameRuntime.Dispose has the same bounded fallback. Dispose must never
                // surface a shutdown failure while the game is already closing.
            }
        }
    }
}
