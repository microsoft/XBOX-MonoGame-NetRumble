using GDK.Net;
using GDK.Net.Users;
using NetRumble.Platform.Diagnostics;

namespace NetRumble.Platform.GameCore.Services;

/// <summary>
/// GDK-backed sign-in implementing the check to silent to UI fallback chain.
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>scripts/services/identity_service.gd</c>. The PlayFab exchange
/// (<c>sign_in_with_xuser_async</c>) lands in Phase 5 once the PlayFab interop is in;
/// this class currently produces the Xbox half of <see cref="PlatformUser"/>.
/// </para>
/// <para>
/// <b>User ownership.</b> The GDK.Net <see cref="User"/> never leaves this class. Other
/// services that need it ask via <see cref="TryGetUser"/>, which is <c>internal</c> to
/// the provider. <see cref="User"/> owns an <c>XUserHandle</c> and is disposable, so the
/// borrowing rule that applied to the raw handle still applies: callers must not dispose
/// what they are lent.
/// </para>
/// </remarks>
internal sealed class GameCoreIdentityService : IIdentityService
{
    private readonly GameCoreRuntime _runtime;
    private readonly GameCoreOnlineSession? _online;
    private User? _user;
    private bool _accountNotificationsHooked;
    private volatile bool _signInInProgress;
    private bool _pendingUserNotification;

    internal GameCoreIdentityService(GameCoreRuntime runtime, GameCoreOnlineSession? online = null)
    {
        _runtime = runtime;
        _online = online;
    }

    public PlatformUser? CurrentUser { get; private set; }

    public string CurrentStage { get; private set; } = string.Empty;

    public event Action<PlatformUser?>? UserChanged;
    public event Action<PlatformAccountChange>? AccountChanged;
    public event Action? ControllerAssociationChanged;
    public event Action<string>? SignInStageChanged;

    /// <summary>
    /// The controllers the platform has paired with the signed-in account. Ported from
    /// <c>DeviceService._device_ids</c>.
    /// </summary>
    private readonly HashSet<AppLocalDeviceId> _associatedDevices = [];

    /// <inheritdoc />
    public bool TracksControllerAssociations { get; private set; }

    /// <inheritdoc />
    public bool HasAssociatedController => _associatedDevices.Count > 0;

    /// <inheritdoc />
    public bool IsSignInInProgress => _signInInProgress;

    public async Task<PlatformResult<PlatformUser>> SignInAsync(
        SignInOptions options = default,
        CancellationToken cancellationToken = default)
    {
        if (!_runtime.IsInitialized || _runtime.Runtime is null)
        {
            return PlatformResult<PlatformUser>.Unavailable(
                "The Microsoft GDK is not running in this build.");
        }

        _signInInProgress = true;
        try
        {
            return await RunSignInAsync(options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _signInInProgress = false;

            // Raised here, after the flag has cleared and the PlayFab step has had its
            // turn, so the first thing a listener sees is the finished user rather than
            // the Xbox half of one. An invite waiting on sign-in joins on this event, and
            // a join attempted with no entity id is refused by Party outright.
            if (_pendingUserNotification)
            {
                _pendingUserNotification = false;
                UserChanged?.Invoke(CurrentUser);
            }
        }
    }

    private async Task<PlatformResult<PlatformUser>> RunSignInAsync(
        SignInOptions options,
        CancellationToken cancellationToken)
    {
        SetStage("Signing in to Xbox");

        HookAccountNotifications();

        // Silent first, so a returning player never sees an account picker.
        var user = await TryAddUserAsync(
            UserAddOptions.AddDefaultUserSilently,
            cancellationToken).ConfigureAwait(false);

        if (user is null && options.AllowUserInterface)
        {
            SetStage("Waiting for the account picker");
            user = await TryAddUserAsync(
                UserAddOptions.AddDefaultUserAllowingUI,
                cancellationToken).ConfigureAwait(false);
        }

        if (user is null)
        {
            SetStage(string.Empty);
            return PlatformResult<PlatformUser>.Fail(
                PlatformStatus.NotSignedIn,
                "No Xbox account is signed in.");
        }

        SetStage("Reading your profile");

        var result = Adopt(user, notify: false);
        if (result.Succeeded)
        {
            _pendingUserNotification = true;

            if (_online is not null)
            {
                await PopulatePlayFabIdentityAsync(user, cancellationToken).ConfigureAwait(false);
            }

            result = PlatformResult<PlatformUser>.Ok(CurrentUser!);
        }

        SetStage(string.Empty);
        return result;
    }

    public Task SignOutAsync()
    {
        _online?.SignOut();
        ReleaseUser();
        CurrentUser = null;
        UserChanged?.Invoke(null);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Re-reads the held <see cref="User"/> and republishes it if the profile moved
    /// (XR-048).
    /// </summary>
    /// <remarks>
    /// The GDK <c>XUserHandle</c> stays valid across a gamertag change - it identifies
    /// the account, not a snapshot of it - so this is a re-read of the same handle
    /// rather than a re-acquire, and it keeps the PlayFab half already attached to
    /// <see cref="CurrentUser"/> instead of forcing a second login. The event is raised
    /// only on a real difference, because a listener such as the lobby treats
    /// <see cref="UserChanged"/> as a reason to rebuild.
    /// </remarks>
    public Task RefreshUserAsync()
    {
        if (_user is null || CurrentUser is null)
        {
            return Task.CompletedTask;
        }

        try
        {
            var refreshed = ReadUser(_user) with { EntityId = CurrentUser.EntityId };
            if (refreshed == CurrentUser)
            {
                return Task.CompletedTask;
            }

            CurrentUser = refreshed;
            UserChanged?.Invoke(refreshed);
        }
        catch (GameRuntimeException ex)
        {
            // A refresh is an opportunistic re-read; the previously good profile is a
            // better answer than none, so the stale value is left in place.
            CrashLog.MarkOnce("identity-refresh-failed", $"identity: profile refresh failed - {ex.Message}");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Lends the signed-in user to another service in this provider. Callers must not
    /// dispose it.
    /// </summary>
    internal bool TryGetUser(out User user)
    {
        user = _user!;
        return _user is not null;
    }

    /// <summary>
    /// Adopts a user obtained by another service - specifically
    /// <see cref="GameCorePlatformUiService.ShowAccountPickerAsync"/>, which must issue
    /// its own add with the UI-allowed option: silently re-running
    /// <see cref="SignInAsync"/> never shows the picker for an already-signed-in user,
    /// since its own silent attempt succeeds first and short-circuits the UI branch.
    /// Reusing that method here would either duplicate the read-user/replace logic or
    /// need a second public entry point on <see cref="IIdentityService"/> for something
    /// that is really an implementation detail of one provider, so this stays
    /// <c>internal</c> instead.
    /// </summary>
    /// <param name="notify">
    /// Whether to raise <see cref="UserChanged"/> here. <see cref="SignInAsync"/> passes
    /// false and raises it itself once the PlayFab half has been added, so no listener
    /// ever sees a user that is signed in to Xbox but not yet able to play online.
    /// </param>
    internal PlatformResult<PlatformUser> Adopt(User user, bool notify = true)
    {
        try
        {
            var platformUser = ReadUser(user);

            ReleaseUser();
            _user = user;
            CurrentUser = platformUser;

            if (notify)
            {
                UserChanged?.Invoke(platformUser);

                // An account adopted outside the sign-in chain - the picker - still needs
                // the PlayFab half, or online play would refuse for the newly picked
                // account until the title was restarted.
                BeginPlayFabRefresh(user);
            }

            return PlatformResult<PlatformUser>.Ok(platformUser);
        }
        catch (GameRuntimeException ex)
        {
            user.Dispose();
            return PlatformResult<PlatformUser>.Fail(
                PlatformStatus.Failed,
                "Your Xbox profile could not be read.",
                ex.Message);
        }
    }

    /// <summary>
    /// Adds a user, returning <see langword="null"/> rather than throwing when the user
    /// simply is not there - the silent attempt failing is the expected path, not an
    /// error.
    /// </summary>
    private async Task<User?> TryAddUserAsync(
        UserAddOptions options,
        CancellationToken cancellationToken)
    {
        var users = _runtime.Runtime!.Users;

        try
        {
            return await _runtime.Dispatcher
                .Marshal(users.AddAsync(options, cancellationToken))
                .ConfigureAwait(false);
        }
        catch (GameRuntimeException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Subscribes to the platform's own account notifications, once.
    /// </summary>
    /// <remarks>
    /// Ported from <c>Services._connect_user_changed()</c>, including its lazy shape: the
    /// GDK must be running before <c>Users</c> can be touched, and sign-in is the first
    /// moment that is guaranteed. Until this runs, the only account changes this title can
    /// see are the ones it causes itself - which is to say a console sign-out went
    /// completely unnoticed.
    /// </remarks>
    private void HookAccountNotifications()
    {
        if (_accountNotificationsHooked || _runtime.Runtime is null)
        {
            return;
        }

        _runtime.Runtime.Users.UserChanged += OnPlatformUserChanged;
        _runtime.Runtime.Users.DeviceAssociationChanged += OnDeviceAssociationChanged;
        _accountNotificationsHooked = true;
    }

    /// <summary>
    /// The platform moved a controller between accounts. Ported from
    /// <c>DeviceService._on_device_association_changed()</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the <i>association</i> half of XR-115 and nothing more. It answers one
    /// question - does the signed-in player still have a pad - and deliberately does not
    /// try to answer "which pad is theirs". The platform speaks device ids that have no
    /// documented correspondence to any index the engine uses, and knowing that a pad
    /// vanished never required knowing which one it was.
    /// </para>
    /// <para>
    /// Both ends of the move are checked, not just the new one: an association moving
    /// <i>to</i> another account is a loss for this one, and only looking at
    /// <see cref="UserDeviceAssociationChangedEventArgs.NewUser"/> would miss it.
    /// </para>
    /// </remarks>
    private void OnDeviceAssociationChanged(object? sender, UserDeviceAssociationChangedEventArgs args)
    {
        if (_user is null)
        {
            return;
        }

        var mine = _user.LocalId;
        bool changed;

        if (args.NewUser.Equals(mine))
        {
            // The first association naming this account is also the proof that the
            // platform answers this question on this machine at all.
            TracksControllerAssociations = true;
            changed = _associatedDevices.Add(args.DeviceId);
        }
        else if (args.OldUser.Equals(mine))
        {
            changed = _associatedDevices.Remove(args.DeviceId);
        }
        else
        {
            // Another account's pad. Not this player's problem.
            return;
        }

        if (changed)
        {
            ControllerAssociationChanged?.Invoke();
        }
    }

    /// <summary>
    /// The platform changed something about an account. Ported from
    /// <c>Services._on_user_changed()</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not marshalled onto the pump</b>, unlike every other callback in this provider.
    /// <see cref="UserChangeEvent.SigningOut"/> is the title's last guaranteed moment -
    /// the platform terminates a title whose user has signed out - so anything posted to
    /// run on a later frame runs on a frame that may never arrive. This is the same
    /// reasoning that keeps <c>Suspending</c> off the pump in <c>GameCoreRuntime</c>.
    /// </para>
    /// <para>
    /// Changes to accounts other than the signed-in one are dropped. A console can have
    /// several users signed in at once and this title is only ever signed in as one of
    /// them; reacting to a different account's sign-out would commit and clear state
    /// belonging to a player who is still playing.
    /// </para>
    /// </remarks>
    private void OnPlatformUserChanged(object? sender, UserChangedEventArgs args)
    {
        if (!IsSignedInUser(args))
        {
            return;
        }

        switch (args.Change)
        {
            case UserChangeEvent.SigningOut:
                AccountChanged?.Invoke(PlatformAccountChange.SigningOut);
                break;

            case UserChangeEvent.SignedOut:
                AccountChanged?.Invoke(PlatformAccountChange.SignedOut);

                // The title did not ask for this, but the outcome is the same one
                // SignOutAsync produces, so it takes the same path - otherwise every
                // reader of CurrentUser would be holding a departed account.
                _online?.SignOut();
                ReleaseUser();
                CurrentUser = null;
                UserChanged?.Invoke(null);
                break;

            case UserChangeEvent.SignedInAgain:
                AccountChanged?.Invoke(PlatformAccountChange.SignedInAgain);
                break;

            case UserChangeEvent.Privileges:
                AccountChanged?.Invoke(PlatformAccountChange.PrivilegesChanged);
                break;
        }
    }

    /// <summary>
    /// Whether a notification is about the account this title signed in as.
    /// </summary>
    /// <remarks>
    /// Compared by <see cref="UserLocalId"/> rather than by object identity: the
    /// notification carries the platform's own <see cref="User"/> wrapper, which is not
    /// required to be the instance sign-in kept - and on a sign-out it may carry no user
    /// at all, which is precisely when getting this right matters most. The local id is
    /// on the event args either way.
    /// </remarks>
    private bool IsSignedInUser(UserChangedEventArgs args)
        => _user is not null && args.LocalId.Equals(_user.LocalId);

    private static PlatformUser ReadUser(User user)
    {
        // UniqueModern rather than Modern: two players can share a modern gamertag and
        // are told apart only by the suffix, which is exactly the case a lobby has to
        // render unambiguously.
        var gamertag = user.GetGamertag(GamertagComponent.UniqueModern);
        var xuid = user.Id.ToString();

        return new PlatformUser
        {
            LocalId = xuid,
            DisplayName = string.IsNullOrEmpty(gamertag) ? "Player" : gamertag,
            XboxUserId = xuid,
            AgeGroup = ReadAgeGroup(user),
        };
    }

    /// <summary>
    /// Reads <c>XUserGetAgeGroup</c> through GDK.Net's <see cref="User.AgeGroup"/>
    /// (XR-013).
    /// </summary>
    /// <remarks>
    /// Required before the title offers account creation or linking, which for this
    /// title means before the first creation-capable PlayFab login. A failure to read it
    /// resolves to <see cref="PlatformAgeGroup.Unknown"/>, which is the restrictive
    /// answer: see <see cref="PlatformAgeGroupExtensions.AllowsSilentAccountCreation"/>.
    /// </remarks>
    private static PlatformAgeGroup ReadAgeGroup(User user)
    {
        try
        {
            return user.AgeGroup switch
            {
                UserAgeGroup.Adult => PlatformAgeGroup.Adult,
                UserAgeGroup.Teen => PlatformAgeGroup.Teen,
                UserAgeGroup.Child => PlatformAgeGroup.Child,
                _ => PlatformAgeGroup.Unknown,
            };
        }
        catch (GameRuntimeException ex)
        {
            CrashLog.MarkOnce(
                "age-group",
                $"identity: XUserGetAgeGroup failed, treating as Unknown: {ex.Message}");

            return PlatformAgeGroup.Unknown;
        }
    }

    /// <summary>
    /// Adds the PlayFab title-player identity after Xbox sign-in has already succeeded.
    /// Failure is deliberately non-fatal: the player is still signed in to Xbox and can
    /// use every non-online feature, while Party and Lobby entry points later refuse with
    /// the specific online-play reason held by <see cref="GameCoreOnlineSession"/>.
    /// </summary>
    /// <remarks>
    /// Raises no event of its own. Callers own the notification, because the point of
    /// this step is that the user is not finished until it has run - see
    /// <see cref="SignInAsync"/> and <see cref="BeginPlayFabRefresh"/>.
    /// </remarks>
    private async Task PopulatePlayFabIdentityAsync(User user, CancellationToken cancellationToken)
    {
        if (_online is null || !_online.CanAttemptLogin || CurrentUser is null)
        {
            return;
        }

        // XR-013: the age group is validated before the first creation-capable login,
        // which is what this call becomes. A child or unknown-age account signs in to a
        // publisher account it already has, but never has one provisioned for it here -
        // that needs parental consent through a compliant flow, which cannot be inferred
        // from the fact that the account is on the Xbox network.
        var allowAccountCreation = CurrentUser.AgeGroup.AllowsSilentAccountCreation();

        if (!allowAccountCreation)
        {
            CrashLog.MarkOnce(
                "age-group-gate",
                $"identity: age group {CurrentUser.AgeGroup} - signing in without account creation (XR-013)");
        }

        SetStage("Signing in to PlayFab");
        var online = await _online.SignInAsync(user, allowAccountCreation, cancellationToken)
            .ConfigureAwait(false);

        // The account can have been replaced while the round trip was in flight, and
        // stamping this entity id onto a different player's user would be worse than
        // having no entity id at all.
        if (online.Succeeded && online.Value is not null && CurrentUser is not null
            && ReferenceEquals(_user, user))
        {
            CurrentUser = CurrentUser with { EntityId = online.Value.EntityId };
        }
    }

    /// <summary>
    /// Re-runs the PlayFab half of sign-in for the account already signed in to Xbox
    /// (XR-074).
    /// </summary>
    /// <remarks>
    /// <para>
    /// PlayFab sign-in runs once, as the tail of Xbox sign-in, and its failure is
    /// deliberately non-fatal. That is right for every failure that is a standing refusal,
    /// and wrong for the one that is not: a login that timed out or could not reach
    /// <c>playfabapi.com</c> - what a VPN or a filtering proxy produces - left the entity
    /// id empty for the whole session, so online play stayed refused long after the
    /// network was fine again and only a restart could clear it.
    /// </para>
    /// <para>
    /// A no-op once an entity exists, so callers may ask on every online action without
    /// spending a round trip on a session that is already signed in.
    /// </para>
    /// </remarks>
    internal async Task RetryPlayFabIdentityAsync(CancellationToken cancellationToken = default)
    {
        if (_online is null
            || !_online.CanAttemptLogin
            || _online.Entity is not null
            || _user is null
            || CurrentUser is null)
        {
            return;
        }

        var user = _user;

        CrashLog.MarkOnce(
            "playfab-retry",
            $"identity: retrying PlayFab sign-in - {_online.UnavailableReason}");

        _signInInProgress = true;
        try
        {
            await PopulatePlayFabIdentityAsync(user, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _signInInProgress = false;
            SetStage(string.Empty);
        }

        // Only on a real change: listeners treat UserChanged as a reason to rebuild, and
        // a failed retry leaves the user exactly as it was.
        if (ReferenceEquals(_user, user) && _online.Entity is not null)
        {
            UserChanged?.Invoke(CurrentUser);
        }
    }

    /// <summary>
    /// Runs the PlayFab step for a user adopted outside <see cref="SignInAsync"/>, then
    /// raises <see cref="UserChanged"/> a second time with the completed user.
    /// </summary>
    /// <remarks>
    /// Not awaited: the picker's caller is a UI action that has already done its job once
    /// the Xbox account changed, and blocking it on a network round trip would leave the
    /// picker up. <see cref="IsSignInInProgress"/> stays true for the duration so that an
    /// invite arriving in the middle waits for the entity id rather than racing it.
    /// </remarks>
    private void BeginPlayFabRefresh(User user)
    {
        if (_online is null || !_online.CanAttemptLogin)
        {
            return;
        }

        _signInInProgress = true;
        _ = RefreshAsync();

        async Task RefreshAsync()
        {
            try
            {
                await PopulatePlayFabIdentityAsync(user, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _signInInProgress = false;
                SetStage(string.Empty);

                if (ReferenceEquals(_user, user))
                {
                    UserChanged?.Invoke(CurrentUser);
                }
            }
        }
    }

    private void ReleaseUser()
    {
        _user?.Dispose();
        _user = null;

        // The associations belonged to that account, and a device list left behind would
        // describe the previous player's hardware. Ported from DeviceService.clear().
        _associatedDevices.Clear();
        TracksControllerAssociations = false;
    }

    private void SetStage(string stage)
    {
        CurrentStage = stage;
        SignInStageChanged?.Invoke(stage);
    }
}
