namespace NetRumble.Platform.Lan;

/// <summary>
/// Decorates another provider, replacing its party service with a real UDP one.
/// </summary>
/// <remarks>
/// <para>
/// A decorator rather than a twelfth full provider on purpose. Nothing about identity,
/// saves, achievements or platform UI changes when the transport does, so re-implementing
/// them would be a copy of <c>OfflinePlatformProvider</c> that drifts from it. This swaps
/// the one service that differs and forwards the rest.
/// </para>
/// <para>
/// The runtime is wrapped too, because <see cref="LanPartyService.Pump"/> has to run on the
/// same per-frame tick as the inner runtime's. That is what keeps
/// <see cref="IPlatformRuntime"/>'s promise that every event reaches game code on the
/// thread that pumps, with no lock anywhere in the netcode.
/// </para>
/// </remarks>
public sealed class LanPlatformProvider : IPlatformProvider
{
    private readonly IPlatformProvider _inner;
    private readonly LanPartyService _party;

    public LanPlatformProvider(IPlatformProvider inner, LanPartyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(inner);

        _inner = inner;
        _party = new LanPartyService(options);
        Runtime = new LanRuntime(inner.Runtime, _party);
    }

    public string Name => $"{_inner.Name}+LAN";

    /// <summary>
    /// The inner provider's capabilities plus <see cref="PlatformCapabilities.Party"/> and
    /// <see cref="PlatformCapabilities.Matchmaking"/>, which this one genuinely has:
    /// hosting, joining by five-character code and joining by connection string all work
    /// end to end over the socket.
    /// </summary>
    /// <remarks>
    /// <see cref="PlatformCapabilities.VoiceChat"/> and
    /// <see cref="PlatformCapabilities.Invites"/> are deliberately not added. This
    /// transport carries text chat but no audio, and a LAN session has no platform invite
    /// surface to send a connection string through - the string is real and joinable, but
    /// nothing here can put it in front of another player.
    /// </remarks>
    public PlatformCapabilities Capabilities
        => _inner.Capabilities | PlatformCapabilities.Party | PlatformCapabilities.Matchmaking;

    public IPlatformRuntime Runtime { get; }

    public IPartyService Party => _party;

    public IIdentityService Identity => _inner.Identity;

    public IActivityService Activity => _inner.Activity;

    public IPrivilegeService Privileges => _inner.Privileges;

    public IPrivacyService Privacy => _inner.Privacy;

    public IModerationService Moderation => _inner.Moderation;

    public ISocialService Social => _inner.Social;

    public IAchievementService Achievements => _inner.Achievements;

    public IGameSaveService GameSaves => _inner.GameSaves;

    public IGameInputService GameInput => _inner.GameInput;

    public IPlatformUiService PlatformUi => _inner.PlatformUi;

    public async ValueTask DisposeAsync()
    {
        await _party.LeaveAsync().ConfigureAwait(false);
        _party.Dispose();
        await _inner.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Forwards to the inner runtime and adds the transport's own per-frame work.</summary>
    private sealed class LanRuntime(IPlatformRuntime inner, LanPartyService party) : IPlatformRuntime
    {
        public bool IsInitialized => inner.IsInitialized;

        /// <summary>
        /// True whenever the inner runtime is, but also true on its own account: a socket
        /// is a runtime that is genuinely present, which is what the corner-of-screen
        /// "Offline" indicator is really asking about.
        /// </summary>
        public bool IsRuntimeAvailable => true;

        /// <summary>The inner runtime's answer: the LAN transport does not change what
        /// machine this is.</summary>
        public PlatformDeviceKind DeviceKind => inner.DeviceKind;

        /// <summary>
        /// Deliberately not delegated: a LAN match needs a local network, not the
        /// internet, and the console hint answers the wrong question for it. Reporting
        /// "not known" keeps the online gate quiet here rather than refusing a LAN game
        /// because the console cannot reach the internet.
        /// </summary>
        public bool IsConnectivityKnown => false;

        public bool IsOnline => true;

        public string OfflineReason => string.Empty;

#pragma warning disable CS0067 // See IsConnectivityKnown: the inner hint is not relayed.
        public event Action<bool>? ConnectivityChanged;
#pragma warning restore CS0067

        public event Action<PlatformResult>? RuntimeError
        {
            add => inner.RuntimeError += value;
            remove => inner.RuntimeError -= value;
        }

        public event Action<PlatformLifecycleEvent>? LifecycleChanged
        {
            add => inner.LifecycleChanged += value;
            remove => inner.LifecycleChanged -= value;
        }

        public Task<PlatformResult> InitializeAsync(CancellationToken cancellationToken = default)
            => inner.InitializeAsync(cancellationToken);

        public void Pump()
        {
            inner.Pump();
            party.Pump();
        }

        public async Task ShutdownAsync()
        {
            await party.LeaveAsync().ConfigureAwait(false);
            await inner.ShutdownAsync().ConfigureAwait(false);
        }
    }
}
