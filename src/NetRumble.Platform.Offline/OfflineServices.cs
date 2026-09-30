namespace NetRumble.Platform.Offline;

/// <summary>Runtime that is always "available" because there is nothing to load.</summary>
internal sealed class OfflineRuntime : IPlatformRuntime
{
    public bool IsInitialized { get; private set; }

    /// <summary>
    /// False by design. The game shows "Offline" in the corner based on this, and it is
    /// what distinguishes a deliberate offline run from a GDK that failed to start.
    /// </summary>
    public bool IsRuntimeAvailable => false;

    /// <summary>Always desktop: the offline provider is the dev-box and fallback tier.</summary>
    public PlatformDeviceKind DeviceKind => PlatformDeviceKind.Desktop;

    /// <summary>
    /// Nothing answers the connectivity question here, so the title says nothing about
    /// it rather than claiming a connection it cannot verify.
    /// </summary>
    public bool IsConnectivityKnown => false;

    /// <summary>
    /// Optimistic, which is what keeps a dev box behaving exactly as it did before
    /// XR-074 existed. See <see cref="IPlatformRuntime.IsOnline"/> for why the only safe
    /// direction to be wrong in is this one.
    /// </summary>
    public bool IsOnline => true;

    public string OfflineReason => string.Empty;

#pragma warning disable CS0067 // Never raised offline; part of the contract.
    public event Action<bool>? ConnectivityChanged;
#pragma warning restore CS0067

#pragma warning disable CS0067 // Never raised offline; part of the contract.
    public event Action<PlatformResult>? RuntimeError;
    public event Action<PlatformLifecycleEvent>? LifecycleChanged;
#pragma warning restore CS0067

    public Task<PlatformResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        IsInitialized = true;
        return Task.FromResult(PlatformResult.Ok());
    }

    public void Pump()
    {
        // Nothing to drain: every offline task completes synchronously.
    }

    public Task ShutdownAsync()
    {
        IsInitialized = false;
        return Task.CompletedTask;
    }
}

/// <summary>
/// Produces a local guest identity so the front end has a name to show and the
/// practice match can start.
/// </summary>
internal sealed class OfflineIdentityService : IIdentityService
{
    public PlatformUser? CurrentUser { get; private set; }

    public string CurrentStage { get; private set; } = string.Empty;

    public event Action<PlatformUser?>? UserChanged;
    public event Action<string>? SignInStageChanged;

#pragma warning disable CS0067 // No platform account exists offline, so nothing can change one.
    public event Action<PlatformAccountChange>? AccountChanged;

    // And nothing pairs controllers with an account that does not exist: the defaults on
    // IIdentityService report "not tracked", which sends callers to the engine's own pad
    // list - the only source there is on a desktop machine.
    public event Action? ControllerAssociationChanged;
#pragma warning restore CS0067

    public Task<PlatformResult<PlatformUser>> SignInAsync(
        SignInOptions options = default,
        CancellationToken cancellationToken = default)
    {
        SetStage("Starting offline session");

        var user = new PlatformUser
        {
            LocalId = "offline-local-player",
            DisplayName = string.IsNullOrWhiteSpace(options.DeveloperCustomId)
                ? "Player"
                : options.DeveloperCustomId,
            IsDeveloperOverride = !string.IsNullOrWhiteSpace(options.DeveloperCustomId),
        };

        CurrentUser = user;
        UserChanged?.Invoke(user);
        SetStage(string.Empty);
        return Task.FromResult(PlatformResult<PlatformUser>.Ok(user));
    }

    public Task SignOutAsync()
    {
        CurrentUser = null;
        UserChanged?.Invoke(null);
        return Task.CompletedTask;
    }

    private void SetStage(string stage)
    {
        CurrentStage = stage;
        SignInStageChanged?.Invoke(stage);
    }
}

/// <summary>
/// Refuses every networking call with a clear reason. Multiplayer is not
/// optional-online, so this must fail loudly rather than pretend to work.
/// </summary>
internal sealed class OfflinePartyService : IPartyService
{
    private const string Reason = "Online play needs a signed-in Xbox account and the Microsoft GDK.";

    public bool HasNetwork => false;
    public bool IsHost => false;
    public int LocalPeerId => 0;
    public string ConnectionString => string.Empty;
    public string JoinCode => string.Empty;

#pragma warning disable CS0067 // Never raised offline; part of the contract.
    public event Action<int>? PeerJoined;
    public event Action<int>? PeerLeft;
    public event Action<PlatformResult>? NetworkDestroyed;
    public event PartyMessageHandler? MessageReceived;
    public event Action? ChatChanged;
    public event Action<int, string, ChatTextKind>? ChatTextReceived;
#pragma warning restore CS0067

    public Task<PlatformResult<string>> HostAsync(int maxPlayers, string gameMode, string protocolVersion, CancellationToken cancellationToken = default)
        => Task.FromResult(PlatformResult<string>.Unavailable(Reason));

    public Task<PlatformResult> JoinAsync(string joinCode, string protocolVersion, CancellationToken cancellationToken = default)
        => Task.FromResult(PlatformResult.Unavailable(Reason));

    public Task<PlatformResult> JoinByConnectionStringAsync(string connectionString, string protocolVersion, CancellationToken cancellationToken = default)
        => Task.FromResult(PlatformResult.Unavailable(Reason));

    public Task LeaveAsync() => Task.CompletedTask;

    public void Send(int peerId, ReadOnlySpan<byte> payload, MessageDelivery delivery)
    {
        // Dropped. The simulation is local-only, so nothing is listening.
    }

    public bool ChatAllowed { get; set; }
    public bool IsSelfMuted { get; set; }

    public void SetPeerRestrictions(int peerId, bool allowVoice, bool allowText) { }
    public void SetPeerMuted(int peerId, bool muted) { }
    public bool IsPeerMuted(int peerId) => false;
    public ChatIndicator GetChatIndicator(int peerId) => ChatIndicator.None;
    public void ClearChatRestrictions() { }

    public PlatformResult SendChatText(string message) => PlatformResult.Unavailable(Reason);
}

internal sealed class OfflineActivityService : IActivityService
{
#pragma warning disable CS0067 // Never raised offline; part of the contract.
    public event Action<string>? InviteAccepted;
#pragma warning restore CS0067

    public Task<PlatformResult> SetActivityAsync(string connectionString, int maxPlayers, int currentPlayers, string groupId = "", CancellationToken cancellationToken = default)
        => Task.FromResult(PlatformResult.Unavailable());

    public Task<PlatformResult> DeleteActivityAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(PlatformResult.Ok());

    public Task<PlatformResult> ShowInviteUiAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(PlatformResult.Unavailable());

    public Task<PlatformResult<IReadOnlyDictionary<string, string>>> GetJoinableActivitiesAsync(IReadOnlyList<string> xboxUserIds, CancellationToken cancellationToken = default)
        => Task.FromResult(PlatformResult<IReadOnlyDictionary<string, string>>.Ok(
            new Dictionary<string, string>()));

    public Task<PlatformResult> ReportRecentPlayersAsync(IReadOnlyList<string> xboxUserIds, CancellationToken cancellationToken = default)
        => Task.FromResult(PlatformResult.Ok());

    public Task<PlatformResult> FlushRecentPlayersAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(PlatformResult.Ok());

    public Task<PlatformResult> SetPresenceAsync(string status, CancellationToken cancellationToken = default)
        => Task.FromResult(PlatformResult.Ok());
}

/// <summary>
/// Grants everything. Offline play has no online surface to gate, and denying here
/// would block the practice match for no reason.
/// </summary>
internal sealed class OfflinePrivilegeService : IPrivilegeService
{
    public Task<PrivilegeVerdict> CheckAsync(GamePrivilege privilege, bool useCache = true, CancellationToken cancellationToken = default)
        => Task.FromResult(PrivilegeVerdict.Allow());

    public Task<PrivilegeVerdict> EnsureAsync(GamePrivilege privilege, CancellationToken cancellationToken = default)
        => Task.FromResult(PrivilegeVerdict.Allow());

    public PrivilegeVerdict? GetCached(GamePrivilege privilege) => PrivilegeVerdict.Allow();

    public void ClearCache() { }
}

internal sealed class OfflinePrivacyService : IPrivacyService
{
    /// <summary>
    /// Succeeds without doing anything. There are no mute or avoid lists to fetch
    /// offline, so there is nothing that could fail - unlike the GDK service, which
    /// fails closed here because it genuinely cannot reach the lists it is supposed to
    /// have. Both are correct for their provider; see <c>PlatformServicesChecks</c>,
    /// which asserts whichever contract the provider under test declares.
    /// </summary>
    public Task<PlatformResult> RefreshListsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(PlatformResult.Ok());

    public Task<PlatformResult<IReadOnlyDictionary<string, PrivacyVerdict>>> EvaluateAsync(IReadOnlyList<string> xboxUserIds, CancellationToken cancellationToken = default)
    {
        var verdicts = new Dictionary<string, PrivacyVerdict>(xboxUserIds.Count);
        foreach (var id in xboxUserIds)
        {
            verdicts[id] = PrivacyVerdict.AllowAll;
        }

        return Task.FromResult(
            PlatformResult<IReadOnlyDictionary<string, PrivacyVerdict>>.Ok(verdicts));
    }

    public PrivacyVerdict? GetCached(string xboxUserId) => PrivacyVerdict.AllowAll;

    public void ClearCache() { }
}

internal sealed class OfflineModerationService : IModerationService
{
    /// <summary>
    /// Passes text through unchanged. There is no one to send it to offline, so
    /// refusing would only break the chat UI in a local test.
    /// </summary>
    public Task<PlatformResult<string>> VerifyTextAsync(string text, CancellationToken cancellationToken = default)
        => Task.FromResult(PlatformResult<string>.Ok(text));

    public Task<PlatformResult> ReportPlayerAsync(string targetXboxUserId, PlayerReportType reportType, string reason = "", CancellationToken cancellationToken = default)
        => Task.FromResult(PlatformResult.Unavailable());

    public Task<PlatformResult> ShowProfileCardAsync(string targetXboxUserId, CancellationToken cancellationToken = default)
        => Task.FromResult(PlatformResult.Unavailable());
}

internal sealed class OfflineSocialService : ISocialService
{
    public Task<PlatformResult<IReadOnlyList<PlatformFriend>>> GetFriendsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(PlatformResult<IReadOnlyList<PlatformFriend>>.Ok(Array.Empty<PlatformFriend>()));

    public Task<PlatformResult<string>> ResolveDisplayNameAsync(string xboxUserId, CancellationToken cancellationToken = default)
        => Task.FromResult(PlatformResult<string>.Unavailable());

    public Task<PlatformResult<IReadOnlyDictionary<string, string>>> ResolveGamerPicturesAsync(
        IReadOnlyList<string> xboxUserIds,
        CancellationToken cancellationToken = default)
        => Task.FromResult(PlatformResult<IReadOnlyDictionary<string, string>>.Unavailable());

    public void Clear() { }
}

internal sealed class OfflineAchievementService : IAchievementService
{
    public Task<PlatformResult> UnlockAsync(string achievementId, CancellationToken cancellationToken = default)
        => Task.FromResult(PlatformResult.Unavailable());

    public Task<PlatformResult> SetProgressAsync(string achievementId, int percentComplete, CancellationToken cancellationToken = default)
        => Task.FromResult(PlatformResult.Unavailable());
}

/// <summary>
/// Reports no devices. The MonoGame layer substitutes a <c>GamePad</c>-backed
/// implementation, which is why this stays a separate, trivially replaceable class.
/// </summary>
internal sealed class NullGameInputService : IGameInputService
{
    public IReadOnlyList<GameInputDevice> Devices => Array.Empty<GameInputDevice>();

#pragma warning disable CS0067 // Never raised; part of the contract.
    public event Action<GameInputDevice>? DeviceConnected;
    public event Action<GameInputDevice>? DeviceDisconnected;
#pragma warning restore CS0067

    public GamepadReading Read(uint deviceId) => GamepadReading.Empty;
    public GamepadReading ReadPrimary() => GamepadReading.Empty;
    public void SetVibration(uint deviceId, float lowFrequency, float highFrequency, float leftTrigger, float rightTrigger) { }
    public void StopVibration(uint deviceId) { }
}

internal sealed class OfflinePlatformUiService : IPlatformUiService
{
    /// <summary>Desktop draws its own text field.</summary>
    public bool RequiresVirtualKeyboard => false;

    public Task<PlatformResult<string>> ShowTextEntryAsync(TextEntryRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(PlatformResult<string>.Unavailable(
            "No platform keyboard in this build; the game draws its own."));

    public Task<PlatformResult> ShowAccountPickerAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(PlatformResult.Unavailable());
}
