namespace NetRumble.Platform.PlayFab;

/// <summary>
/// Owns the PlayFab session and hands out the services that depend on it.
/// </summary>
/// <remarks>
/// <para>
/// The composition root for this project, and the only public type in it. Everything
/// else is internal, because a provider should be able to adopt PlayFab by holding one
/// object and forwarding three properties - not by learning the REST client, the
/// session record and the auth client.
/// </para>
/// <para>
/// Sign-in state lives here rather than in each service so that one login serves all
/// of them, and a re-login is picked up everywhere at once. The services read
/// <see cref="Session"/> through a callback for exactly that reason.
/// </para>
/// <para>
/// <b>Threading.</b> <see cref="Session"/> is written on whichever thread completes a
/// sign-in and read by every service call, so it is guarded by a volatile reference
/// swap - the record is immutable, so a reader either sees the whole old session or
/// the whole new one, never a mixture. This type does <i>not</i> impose the
/// <c>IPlatformRuntime.Pump()</c> threading contract; a provider that adopts it is
/// responsible for marshalling results onto the pump thread, the same as it already
/// does for its own async work.
/// </para>
/// </remarks>
public sealed class PlayFabOnlineServices : IDisposable
{
    private readonly PlayFabRestClient _client;
    private readonly PlayFabAuthClient _auth;

    private PlayFabSession? _session;

    /// <summary>
    /// Creates the service set for <paramref name="configuration"/>.
    /// </summary>
    /// <param name="handler">
    /// Transport override for tests. Null uses a real HTTPS handler; an injected one is
    /// not disposed by this object.
    /// </param>
    /// <param name="localSaveRoot">
    /// Overrides where the local save tier writes. Null uses the default under
    /// <c>%LOCALAPPDATA%</c>.
    /// </param>
    public PlayFabOnlineServices(
        PlayFabConfiguration configuration,
        HttpMessageHandler? handler = null,
        string? localSaveRoot = null)
    {
        Configuration = configuration;

        _client = new PlayFabRestClient(configuration, handler);
        _auth = new PlayFabAuthClient(_client);

        // Scoped by EntityId (XR-052): the local tier must never mix one signed-in
        // account's settings, stats or history into another's on a shared device.
        GameSaves = new PlayFabGameSaveService(_client, () => Session, localSaveRoot, () => Session?.EntityId);
        Lobbies = new PlayFabLobbyClient(_client, () => Session);
    }

    /// <summary>The title these services talk to.</summary>
    public PlayFabConfiguration Configuration { get; }

    /// <summary>The current session, or null when not signed in.</summary>
    public PlayFabSession? Session
    {
        get => Volatile.Read(ref _session);
        private set => Volatile.Write(ref _session, value);
    }

    /// <summary>True when a session exists and its entity token is still usable.</summary>
    public bool IsSignedIn => Session?.IsEntityTokenValid(DateTimeOffset.UtcNow) == true;

    /// <summary>
    /// The PlayFab entity id for the signed-in player, or empty.
    /// </summary>
    /// <remarks>
    /// This is what a provider copies into <see cref="PlatformUser.EntityId"/>, and the
    /// value the whole online feature set was blocked on.
    /// </remarks>
    public string EntityId => Session?.EntityId ?? string.Empty;

    /// <summary>Local save plus a PlayFab user-data cloud tier.</summary>
    public IGameSaveService GameSaves { get; }

    /// <summary>Lobby create, join and find. Used to resolve join codes.</summary>
    internal PlayFabLobbyClient Lobbies { get; }

    /// <summary>
    /// Raised whenever the session is established or cleared.
    /// </summary>
    /// <remarks>
    /// A provider hooks this to update <see cref="PlatformUser.EntityId"/> and to raise
    /// its own <c>UserChanged</c>. Without it a provider would have to poll, and an
    /// entity id that appears half a second after sign-in "finishes" is precisely the
    /// race that makes a match-join fail once in twenty attempts.
    /// </remarks>
    public event Action<PlayFabSession?>? SessionChanged;

    /// <summary>
    /// Signs in with an Xbox XSTS token. The shipping path.
    /// </summary>
    /// <param name="xboxToken">
    /// The GDK's <c>XBL3.0 x=&lt;hash&gt;;&lt;token&gt;</c> string, obtained from
    /// <c>XUserGetTokenAndSignatureAsync</c> for the relying party
    /// <c>https://playfabapi.com/</c>.
    /// </param>
    /// <param name="createAccount">
    /// Whether PlayFab may provision a publisher account for this user if none exists
    /// (XR-013). Decided by the caller from the platform age group.
    /// </param>
    public Task<PlatformResult<PlayFabSession>> SignInWithXboxAsync(
        string xboxToken,
        bool createAccount,
        CancellationToken cancellationToken = default)
        => AdoptAsync(_auth.LoginWithXboxAsync(xboxToken, createAccount, cancellationToken));

    /// <summary>
    /// Signs in with a developer custom id. Debug desktop builds only; see
    /// <see cref="SignInOptions.DeveloperCustomId"/>.
    /// </summary>
    public Task<PlatformResult<PlayFabSession>> SignInWithCustomIdAsync(
        string customId,
        CancellationToken cancellationToken = default)
        => AdoptAsync(_auth.LoginWithCustomIdAsync(customId, cancellationToken));

    /// <summary>Drops the session. Does not tell PlayFab; there is nothing to tell.</summary>
    public void SignOut()
    {
        if (Session is null)
        {
            return;
        }

        Session = null;
        SessionChanged?.Invoke(null);
    }

    private async Task<PlatformResult<PlayFabSession>> AdoptAsync(
        Task<PlatformResult<PlayFabSession>> login)
    {
        var result = await login.ConfigureAwait(false);

        if (result.Failed)
        {
            // Leave any existing session alone. A failed refresh should not sign the
            // player out of a session that is still working.
            return result;
        }

        Session = result.Value;
        SessionChanged?.Invoke(result.Value);
        return result;
    }

    public void Dispose() => _client.Dispose();
}
