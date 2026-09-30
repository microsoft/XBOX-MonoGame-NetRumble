namespace NetRumble.Platform.PlayFab;

/// <summary>
/// Decorates another provider, adding a PlayFab identity and cloud save.
/// </summary>
/// <remarks>
/// <para>
/// A decorator, for the same reason <c>LanPlatformProvider</c> is one: PlayFab changes
/// three services and nothing else, so re-implementing the other nine would produce a
/// copy of <c>OfflinePlatformProvider</c> that drifts from it.
/// </para>
/// <para>
/// <b>What this composes with matters.</b> Over the offline provider and a
/// <c>--pf-user</c> custom id, this is a complete, working online identity on any dev
/// box: a real entity id, real cloud save, real lobbies, with no GDK,
/// no console and no Xbox account - only a title id. That is the configuration this was
/// built to make possible, because it is the only one that can actually be run and
/// checked here.
/// </para>
/// <para>
/// The GDK provider is intentionally not wrapped by this decorator. It owns a
/// <c>GameCoreOnlineSession</c> directly, because its Xbox user can now obtain the XSTS
/// token for <c>/Client/LoginWithXbox</c> and that same login has to feed Party, Lobby
/// and cloud save. Wrapping it here would put a second
/// <see cref="PlayFabIdentityService"/> on top and risk two PlayFab identities for one
/// player. See <c>docs/design-notes.md</c>.
/// </para>
/// </remarks>
public sealed class PlayFabPlatformProvider : IPlatformProvider
{
    private readonly IPlatformProvider _inner;
    private readonly PlayFabOnlineServices _services;
    private readonly PlayFabIdentityService _identity;

    public PlayFabPlatformProvider(
        IPlatformProvider inner,
        PlayFabConfiguration configuration,
        HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(inner);

        _inner = inner;
        _services = new PlayFabOnlineServices(configuration, handler);
        _identity = new PlayFabIdentityService(inner.Identity, _services);
    }

    public string Name => $"{_inner.Name}+PlayFab";

    /// <summary>
    /// The inner provider's capabilities, plus cloud save <i>only</i>
    /// when a real title id is configured.
    /// </summary>
    /// <remarks>
    /// Conditional on purpose. The whole point of this flags enum is to let the UI hide
    /// what will not work instead of offering it and handling a predictable failure;
    /// advertising cloud save against the placeholder title would produce a surface
    /// whose only possible outcome is an error dialog.
    /// </remarks>
    public PlatformCapabilities Capabilities
        => _services.Configuration.IsPlaceholder
            ? _inner.Capabilities
            : _inner.Capabilities | PlatformCapabilities.CloudSave;

    /// <summary>The PlayFab session and the lobby client, for code that needs them directly.</summary>
    public PlayFabOnlineServices Online => _services;

    public IIdentityService Identity => _identity;

    public IGameSaveService GameSaves => _services.GameSaves;

    public IPlatformRuntime Runtime => _inner.Runtime;

    public IPartyService Party => _inner.Party;

    public IActivityService Activity => _inner.Activity;

    public IPrivilegeService Privileges => _inner.Privileges;

    public IPrivacyService Privacy => _inner.Privacy;

    public IModerationService Moderation => _inner.Moderation;

    public ISocialService Social => _inner.Social;

    public IAchievementService Achievements => _inner.Achievements;

    public IGameInputService GameInput => _inner.GameInput;

    public IPlatformUiService PlatformUi => _inner.PlatformUi;

    public async ValueTask DisposeAsync()
    {
        _services.Dispose();
        await _inner.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Runs the inner provider's sign-in, then exchanges it for a PlayFab session and
/// republishes the user with its entity id filled in.
/// </summary>
/// <remarks>
/// <para>
/// The entity id is the reason this class exists. <see cref="PlatformUser.EntityId"/>
/// has been empty for the whole port, and every online feature is gated on it -
/// <c>GameCorePartyService</c> refuses at the door without one, and Party keys every
/// endpoint by it. It stays inside the platform layer: it is not advertised, not put in
/// an invite, and no longer travels in roster messages (XR-014).
/// </para>
/// <para>
/// The inner sign-in still runs first and still decides success. PlayFab is an
/// <i>additional</i> identity layered on the platform one, not a replacement for it: on
/// console the Xbox user is the real identity and the account the player owns, and a
/// provider that quietly signed a different player in because a PlayFab call succeeded
/// would be a serious bug.
/// </para>
/// </remarks>
internal sealed class PlayFabIdentityService : IIdentityService
{
    private readonly IIdentityService _inner;
    private readonly PlayFabOnlineServices _services;
    private volatile bool _signingIn;

    public PlayFabIdentityService(IIdentityService inner, PlayFabOnlineServices services)
    {
        _inner = inner;
        _services = services;

        // Forward, but re-decorate: a user change raised by the inner provider must not
        // reach game code still carrying an entity id from the previous player.
        _inner.UserChanged += user => UserChanged?.Invoke(Decorate(user));
    }

    public PlatformUser? CurrentUser => Decorate(_inner.CurrentUser);

    public event Action<PlatformUser?>? UserChanged;

    /// <summary>
    /// Passed straight through. There is nothing to decorate: the value of this event is
    /// its kind, and the kind is the platform's, not PlayFab's.
    /// </summary>
    public event Action<PlatformAccountChange>? AccountChanged
    {
        add => _inner.AccountChanged += value;
        remove => _inner.AccountChanged -= value;
    }

    /// <inheritdoc />
    public bool TracksControllerAssociations => _inner.TracksControllerAssociations;

    /// <inheritdoc />
    public bool HasAssociatedController => _inner.HasAssociatedController;

    /// <summary>Passed through: controllers pair with an Xbox account, not a PlayFab one.</summary>
    public event Action? ControllerAssociationChanged
    {
        add => _inner.ControllerAssociationChanged += value;
        remove => _inner.ControllerAssociationChanged -= value;
    }

    public event Action<string>? SignInStageChanged
    {
        add => _inner.SignInStageChanged += value;
        remove => _inner.SignInStageChanged -= value;
    }

    public string CurrentStage => _inner.CurrentStage;

    /// <inheritdoc />
    /// <remarks>
    /// Covers this layer's own PlayFab step as well as the inner platform chain: the
    /// entity id only exists once <see cref="SignInAsync"/> below has returned, and a
    /// caller that waited on the inner service alone would act on an entity-less user.
    /// </remarks>
    public bool IsSignInInProgress => _signingIn || _inner.IsSignInInProgress;

    public async Task<PlatformResult<PlatformUser>> SignInAsync(
        SignInOptions options = default,
        CancellationToken cancellationToken = default)
    {
        _signingIn = true;
        try
        {
            return await SignInCoreAsync(options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _signingIn = false;
        }
    }

    private async Task<PlatformResult<PlatformUser>> SignInCoreAsync(
        SignInOptions options,
        CancellationToken cancellationToken)
    {
        var inner = await _inner.SignInAsync(options, cancellationToken).ConfigureAwait(false);

        if (inner.Failed)
        {
            return inner;
        }

        // No title id means no PlayFab, but the platform sign-in already succeeded and
        // the front end, settings and practice match all work without an entity id.
        // Failing the whole sign-in here would take a working game down over an unset
        // environment variable.
        if (_services.Configuration.IsPlaceholder)
        {
            return inner;
        }

        if (!string.IsNullOrWhiteSpace(options.DeveloperCustomId))
        {
#if DEBUG
            // The result is deliberately not propagated. The player is already signed in
            // to the platform; if PlayFab declines, online features decline individually,
            // which is a far better experience than bouncing back to the sign-in screen
            // with nothing to do about it. The entity id simply stays empty.
            await _services
                .SignInWithCustomIdAsync(options.DeveloperCustomId, cancellationToken)
                .ConfigureAwait(false);

            // The inner service raised its own UserChanged before this login ran, so that
            // event carried no entity id. Anything holding out for one - an invite waiting
            // to join - needs to be told the user is now complete.
            var completed = Decorate(_inner.CurrentUser);
            if (completed is not null && completed.EntityId.Length > 0)
            {
                UserChanged?.Invoke(completed);
            }
#else
            // XR-013: this path must never exist in a retail build - LoginWithCustomID
            // authenticates nobody, and a caller that somehow supplied a
            // DeveloperCustomId here (composition error, not the normal desktop caller,
            // which is compiled out in NetRumbleGame.ResolveUserToken) is silently
            // ignored rather than honoured.
#endif
        }

        return PlatformResult<PlatformUser>.Ok(Decorate(inner.Value)!);
    }

    public async Task SignOutAsync()
    {
        _services.SignOut();
        await _inner.SignOutAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Copies the current PlayFab entity id onto a user.
    /// </summary>
    /// <remarks>
    /// Applied on every read rather than stored, so a PlayFab sign-in that completes
    /// after the platform one - or a re-login later in the session - is reflected
    /// immediately. A snapshot taken at sign-in is exactly how a player ends up
    /// carrying an empty entity id into a match they joined thirty seconds later.
    /// </remarks>
    private PlatformUser? Decorate(PlatformUser? user)
    {
        if (user is null)
        {
            return null;
        }

        var entityId = _services.EntityId;

        return entityId.Length == 0 || user.EntityId == entityId
            ? user
            : user with { EntityId = entityId };
    }
}
