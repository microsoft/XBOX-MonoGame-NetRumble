using NetRumble.Platform.Networking;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using GDK.Net;
using GDK.Net.PlayFab.Party;
using NetRumble.Platform.GameCore.Interop;
using NetRumble.Platform.Diagnostics;
using NetRumble.Platform.PlayFab;

namespace NetRumble.Platform.GameCore.Services;

/// <summary>PlayFab Party networking and chat over GDK.Net's managed Party projection.</summary>
/// <remarks>
/// <para>
/// Party has endpoints rather than the small integer peer ids the game inherited from
/// Godot. The provider therefore owns a translation table and a tiny reliable control
/// handshake: a client sends an internal hello after creating its endpoint, the host
/// assigns the next peer id, and a welcome frame completes the client's join task. User
/// messages never see those frames, preserving <see cref="IPartyService"/>'s contract.
/// </para>
/// <para>
/// Party operations are asynchronous but not <see cref="Task"/>-based. A start call
/// returns a <see cref="PartyOperationId"/> and a completion record later appears from
/// <see cref="PartyManager.ProcessStateChanges"/>. The task bridge below is keyed by
/// operation id and completed directly from <see cref="Pump"/>; it deliberately does not
/// use <see cref="PumpDispatcher"/>, which is only for GDK.Net APIs whose own tasks
/// complete on the runtime thread pool.
/// </para>
/// <para>
/// This cannot be exercised end-to-end in this checkout because there is no installed
/// GDK, Xbox account or real PlayFab title id. The implementation follows the verified
/// <c>GDK.Net.MultiplayerHarness</c> sequence and records every remaining uncertainty in
/// <c>docs/design-notes.md</c> rather than claiming live service validation.
/// </para>
/// </remarks>
internal sealed class GameCorePartyService : IPartyService, IDisposable
{
    private const int MaxPartyPlayers = 8;
    private const int MaxChatChars = 512;
    private const string LanguageCode = "en-US";
    private const string DescriptorLobbyDataKey = "netrumble.party.descriptor";
    private const string InvitationLobbyDataKey = "netrumble.party.invitation";

    /// <summary>
    /// The opaque per-session host token (XR-014). Named separately from the
    /// <c>hostEntityId</c> key it replaced so that a lobby written by a build that
    /// published raw entity ids is not mistaken for one carrying a token.
    /// </summary>
    private const string HostTokenLobbyDataKey = "netrumble.party.hostToken";
    private static readonly byte[] ControlMagic = Encoding.ASCII.GetBytes("NRP1");

    /// <summary>Client to host: "I have an endpoint, assign me a peer id".</summary>
    private const byte ControlKindHello = 1;

    /// <summary>Host to one client: "your peer id is N".</summary>
    private const byte ControlKindWelcome = 2;

    /// <summary>
    /// Host to everyone: "I am leaving and this session is over" (#25).
    /// </summary>
    /// <remarks>
    /// Party raises <c>PartyNetworkDestroyed</c> when the network really goes, but a
    /// host that leaves a lobby cleanly tears its own endpoint down first, and members
    /// were left sitting in a lobby that no longer had a host - visibly fine, joinable
    /// by nobody - until some later transport failure happened to notice. Saying so
    /// explicitly turns that into an immediate, explained return to the main menu.
    /// </remarks>
    private const byte ControlKindHostClosing = 3;

    private readonly GameCoreRuntime? _runtime;
    private readonly GameCoreIdentityService _identity;
    private readonly GameCoreOnlineSession? _online;
    private readonly PlayFabConfiguration? _configuration;

    /// <summary>
    /// String verification for inbound player-authored text (XR-018). Null in the
    /// fallback (no-GDK) construction, where no Party network exists to receive text
    /// from in the first place.
    /// </summary>
    private readonly GameCoreModerationService? _moderation;
    private readonly Dictionary<PartyOperationId, PendingOperation> _pendingOperations = [];
    private readonly List<PendingRegions> _pendingRegions = [];
    private readonly Dictionary<ushort, int> _endpointToPeer = [];
    private readonly Dictionary<int, PartyEndpoint> _peerToEndpoint = [];
    private readonly Dictionary<string, int> _entityToPeer = new(StringComparer.Ordinal);
    private readonly HashSet<int> _mutedPeers = [];
    private readonly Dictionary<int, ChatRestriction> _restrictions = [];

    private PartyManager? _party;
    private PartyLocalUser? _partyUser;
    private PartyNetwork? _network;
    private PartyEndpoint? _localEndpoint;
    private PartyChatControl? _localChat;
    private string _lobbyId = string.Empty;

    /// <summary>
    /// The host's entity id once it is known: on the host, its own; on a client, whichever
    /// authenticated endpoint entity the host token verified against, cached after the
    /// first match. Empty on a client until then, which is why every authority check goes
    /// through <see cref="IsHostEntity"/> rather than comparing this directly.
    /// </summary>
    private string _hostEntityId = string.Empty;

    /// <summary>
    /// The published stand-in for <see cref="_hostEntityId"/> (XR-014). See
    /// <see cref="PartyHostToken"/>.
    /// </summary>
    private string _hostToken = string.Empty;
    private int _nextPeerId = IPartyService.HostPeerId + 1;
    private TaskCompletionSource<int>? _peerAssignment;
    private CancellationToken _peerAssignmentCancellation;
    /// <summary>
    /// Whether the communications privilege has been confirmed for the local user
    /// (XR-015, XR-045).
    /// </summary>
    /// <remarks>
    /// Starts <see langword="false"/>. It used to start <see langword="true"/>, which
    /// meant <see cref="ApplyLocalMute"/> computed
    /// <c>AudioInputMuted = IsSelfMuted || !ChatAllowed</c> as <see langword="false"/> and
    /// left the microphone live from the moment the chat control existed until an
    /// asynchronous privilege round trip landed. Silence until the platform says otherwise
    /// is the only defensible default: a player whose account may not be permitted to talk
    /// must not be transmitting while the title finds out.
    /// </remarks>
    private bool _chatAllowed;
    private bool _isSelfMuted;
    private bool _leaving;
    private bool _disposed;

    internal GameCorePartyService(GameCoreIdentityService identity)
        : this(null, identity, null, null, null, fallbackOnly: true)
    {
    }

    internal GameCorePartyService(
        GameCoreRuntime runtime,
        GameCoreIdentityService identity,
        GameCoreOnlineSession online,
        PlayFabConfiguration configuration,
        GameCoreModerationService moderation)
        : this(runtime, identity, online, configuration, moderation, fallbackOnly: false)
    {
    }

    private GameCorePartyService(
        GameCoreRuntime? runtime,
        GameCoreIdentityService identity,
        GameCoreOnlineSession? online,
        PlayFabConfiguration? configuration,
        GameCoreModerationService? moderation,
        bool fallbackOnly)
    {
        _runtime = runtime;
        _identity = identity;
        _online = online;
        _configuration = configuration;
        _moderation = moderation;
    }

    public bool HasNetwork { get; private set; }
    public bool IsHost { get; private set; }
    public int LocalPeerId { get; private set; }
    public string ConnectionString { get; private set; } = string.Empty;
    public string JoinCode { get; private set; } = string.Empty;

    public bool ChatAllowed
    {
        get => _chatAllowed;
        set
        {
            if (_chatAllowed == value)
            {
                return;
            }

            _chatAllowed = value;
            ApplyChatPolicy();
            ChatChanged?.Invoke();
        }
    }

    public bool IsSelfMuted
    {
        get => _isSelfMuted;
        set
        {
            if (_isSelfMuted == value)
            {
                return;
            }

            _isSelfMuted = value;
            ApplyLocalMute();
            ChatChanged?.Invoke();
        }
    }

    public event Action<int>? PeerJoined;
    public event Action<int>? PeerLeft;
    public event Action<PlatformResult>? NetworkDestroyed;
    public event PartyMessageHandler? MessageReceived;
    public event Action? ChatChanged;
    public event Action<int, string, ChatTextKind>? ChatTextReceived;

    public async Task<PlatformResult<string>> HostAsync(
        int maxPlayers,
        string gameMode,
        string protocolVersion,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return PlatformResult<string>.Canceled();
        }

        var available = await EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        if (available.Failed)
        {
            return PlatformResult<string>.Fail(available.Status, available.Message ?? UnavailableReason());
        }

        if (HasNetwork)
        {
            return PlatformResult<string>.Fail(PlatformStatus.Failed, "A session is already running.",
                "GameCorePartyService.HostAsync called with a live network.");
        }

        var stage = "starting the Party manager";
        try
        {
            EnsureParty();

            stage = "creating the Party local user";
            EnsurePartyUser();

            stage = "finding a Party region";
            var regions = await AwaitRegionsAsync(cancellationToken).ConfigureAwait(false);
            if (regions.Count == 0)
            {
                return PlatformResult<string>.Fail(PlatformStatus.NetworkFailure,
                    "Online play could not find a Party region.",
                    "Party.GetRegions remained empty before hosting.");
            }

            stage = "creating the Party network";
            var create = _party!.CreateNewNetwork(_partyUser!, NetworkConfiguration(maxPlayers), regions);
            var created = await AwaitOperationAsync<PartyCreateNewNetworkCompleted>(
                create.Operation, cancellationToken).ConfigureAwait(false);
            if (!created.Succeeded)
            {
                await LeaveAsync().ConfigureAwait(false);
                return PlatformResult<string>.Fail(MapPartyStatus(created.Result),
                    WithDiagnostics(
                        PlayerMessage("The online match could not be created.", created.Result),
                        Describe(created)),
                    Describe(created));
            }

            var descriptor = created.Descriptor.Serialize();
            _hostEntityId = _online!.EntityId;
            _hostToken = PartyHostToken.Create(_hostEntityId);
            IsHost = true;
            MarkEntity(_hostEntityId, IPartyService.HostPeerId);

            stage = "connecting the host endpoint and chat";
            var connected = await ConnectAuthenticateEndpointAndChatAsync(
                descriptor, created.AppliedInitialInvitationIdentifier, cancellationToken)
                .ConfigureAwait(false);
            if (connected.Failed)
            {
                await LeaveAsync().ConfigureAwait(false);
                return PlatformResult<string>.Fail(
                    connected.Status,
                    WithDiagnostics(connected.Message ?? "The online match could not be joined.", connected.Diagnostics),
                    connected.Diagnostics);
            }

            MarkLocalEndpoint(IPartyService.HostPeerId);
            ConnectionString = EncodeInvite(
                descriptor,
                created.AppliedInitialInvitationIdentifier,
                _hostToken,
                protocolVersion);

            stage = "creating the PlayFab lobby";
            var lobby = await _online.Lobbies.CreateLobbyWithJoinCodeAsync(
                maxPlayers,

                // Public, not private. The join code is a value in the lobby's indexed
                // search data, so resolving it is a FindLobbies query - and FindLobbies
                // never returns a Private lobby. Hosting privately published a code that
                // no client could ever match, which surfaced as "No match is using that
                // code." even with the host sitting in a live lobby. The code itself is
                // the gate; the lobby carries no secrets beyond the Party descriptor,
                // which is useless without it.
                isPrivate: false,
                lobbyData: new Dictionary<string, string>
                {
                    [DescriptorLobbyDataKey] = descriptor,
                    [InvitationLobbyDataKey] = created.AppliedInitialInvitationIdentifier ?? string.Empty,
                    [HostTokenLobbyDataKey] = _hostToken,
                },
                cancellationToken).ConfigureAwait(false);

            if (lobby.Failed || lobby.Value is null)
            {
                await LeaveAsync().ConfigureAwait(false);
                return PlatformResult<string>.Fail(lobby.Status,
                    WithDiagnostics(lobby.Message ?? "The lobby could not be created.", lobby.Result.Diagnostics),
                    lobby.Result.Diagnostics);
            }

            _lobbyId = lobby.Value.LobbyId;
            JoinCode = lobby.Value.JoinCode;
            HasNetwork = true;
            return PlatformResult<string>.Ok(JoinCode);
        }
        catch (OperationCanceledException)
        {
            await LeaveAsync().ConfigureAwait(false);
            return PlatformResult<string>.Canceled();
        }
        catch (Exception ex) when (IsPlatformException(ex))
        {
            await LeaveAsync().ConfigureAwait(false);
            return PlatformResult<string>.Fail(
                PlatformStatus.Failed,
                WithDiagnostics($"The online match could not be created while {stage}.", ex.Message),
                ex.Message);
        }
    }

    public async Task<PlatformResult> JoinAsync(
        string joinCode,
        string protocolVersion,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return PlatformResult.Canceled();
        }

        var available = await EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        if (available.Failed)
        {
            return available;
        }

        var found = await _online!.Lobbies.FindLobbyByJoinCodeAsync(joinCode, cancellationToken)
            .ConfigureAwait(false);
        if (found.Failed)
        {
            return PlatformResult.Fail(found.Status,
                found.Message ?? "Could not look up that match.", found.Result.Diagnostics);
        }

        if (found.Value is null)
        {
            return PlatformResult.Fail(PlatformStatus.Failed, "No match is using that code.",
                $"No PlayFab Lobby row matched join code '{joinCode}'.");
        }

        // Before JoinLobbyAsync, not after. This is the last point at which the two builds
        // still agree on how to talk, and refusing here means no lobby membership to
        // unwind and no chance of the roster briefly showing a peer that cannot play.
        if (!string.Equals(found.Value.ProtocolVersion, protocolVersion, StringComparison.Ordinal))
        {
            return PlatformResult.Fail(
                PlatformStatus.Failed,
                NRProtocol.MismatchMessage(found.Value.ProtocolVersion, protocolVersion),
                $"Lobby protocol '{found.Value.ProtocolVersion}' does not match local '{protocolVersion}'.");
        }

        var joined = await _online.Lobbies.JoinLobbyAsync(found.Value.ConnectionString, cancellationToken)
            .ConfigureAwait(false);
        if (joined.Failed || string.IsNullOrWhiteSpace(joined.Value))
        {
            return PlatformResult.Fail(joined.Status,
                joined.Message ?? "Could not join that match.", joined.Result.Diagnostics);
        }

        _lobbyId = joined.Value;
        JoinCode = found.Value.JoinCode;

        var lobby = found.Value;
        if (!TryReadNetworkDescriptor(lobby, out var descriptor, out var invitation, out var hostToken, out _))
        {
            var read = await _online.Lobbies.GetLobbyAsync(_lobbyId, cancellationToken).ConfigureAwait(false);
            if (read.Succeeded && read.Value is not null)
            {
                lobby = read.Value;
            }
        }

        if (!TryReadNetworkDescriptor(lobby, out descriptor, out invitation, out hostToken, out var error))
        {
            await _online.Lobbies.LeaveLobbyAsync(_lobbyId, cancellationToken).ConfigureAwait(false);
            _lobbyId = string.Empty;
            return PlatformResult.Fail(PlatformStatus.Failed, "That match cannot be joined.", error);
        }

        return await JoinPartyNetworkAsync(
                descriptor,
                invitation,
                hostToken,
                protocolVersion,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <remarks>
    /// The version is carried inside the invite envelope rather than looked up in a lobby:
    /// an invite contains a serialized Party descriptor and may arrive without any lobby
    /// row. Invites created before this field existed decode with an empty version and are
    /// refused as older builds, never allowed through to Party.
    /// </remarks>
    public async Task<PlatformResult> JoinByConnectionStringAsync(
        string connectionString,
        string protocolVersion,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return PlatformResult.Canceled();
        }

        var available = await EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        if (available.Failed)
        {
            return available;
        }

        if (!TryDecodeInvite(
                connectionString,
                out var descriptor,
                out var invitation,
                out var hostToken,
                out var inviteProtocolVersion))
        {
            return PlatformResult.Fail(
                PlatformStatus.Failed,
                "That invite could not be read.",
                "The connection string was neither a Net Rumble Party invite nor a serialized Party descriptor.");
        }

        if (string.IsNullOrEmpty(inviteProtocolVersion)
            || !string.Equals(inviteProtocolVersion, protocolVersion, StringComparison.Ordinal))
        {
            return PlatformResult.Fail(
                PlatformStatus.Failed,
                NRProtocol.MismatchMessage(inviteProtocolVersion, protocolVersion),
                $"Invite protocol '{inviteProtocolVersion}' does not match local '{protocolVersion}'.");
        }

        return await JoinPartyNetworkAsync(
                descriptor,
                invitation,
                hostToken,
                protocolVersion,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task LeaveAsync()
    {
        if (!HasNetwork && _network is null && _party is null)
        {
            ResetSessionState();
            return;
        }

        // Captured before teardown clears them: the finally block below needs to know
        // whether this peer owned the lobby it is about to release.
        var wasHost = IsHost;

        // #25: the host leaving must take the session down rather than leave a lobby
        // that is still advertised but no longer joinable. Party's own destruction
        // notice does not reliably reach a peer sitting in the lobby, so the host says
        // so explicitly first, while its endpoint is still up. Best-effort by nature -
        // a guaranteed send that never lands is exactly the case the members' own
        // transport timeout still covers.
        if (wasHost && HasNetwork && _localEndpoint is not null)
        {
            try
            {
                _localEndpoint.SendMessage(
                    Array.Empty<PartyEndpoint>(),
                    ControlMessage(ControlKindHostClosing),
                    PartySendMessageOptions.GuaranteedDelivery);
            }
            catch (Exception ex) when (IsPlatformException(ex))
            {
                CrashLog.MarkOnce("host-closing", $"party: host-closing notice failed: {ex.Message}");
            }
        }

        _leaving = true;
        try
        {
            if (_network is not null)
            {
                if (_localChat is not null)
                {
                    await TryAwait(_network.DisconnectChatControl(_localChat)).ConfigureAwait(false);
                }

                if (_localEndpoint is not null)
                {
                    await TryAwait(_network.DestroyEndpoint(_localEndpoint)).ConfigureAwait(false);
                }

                if (_partyUser is not null)
                {
                    await TryAwait(_network.RemoveLocalUser(_partyUser)).ConfigureAwait(false);
                }

                await TryAwait(_network.Leave()).ConfigureAwait(false);
            }

            if (_party is not null && _localChat is not null)
            {
                await TryAwait(_party.LocalDevice.DestroyChatControl(_localChat)).ConfigureAwait(false);
            }

            if (_party is not null && _partyUser is not null)
            {
                await TryAwait(_party.DestroyLocalUser(_partyUser)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (IsPlatformException(ex))
        {
            CrashLog.MarkOnce("party-leave", $"party: leave teardown failed: {ex.Message}");
        }
        finally
        {
            if (_online is not null && _lobbyId.Length > 0)
            {
                // #25: a host that merely leaves its own lobby leaves the row behind,
                // still carrying the join code in its indexed search data and still
                // answering FindLobbies - so a third client resolves the code, joins
                // the lobby and then blocks forever on a Party handshake with a host
                // that is gone. The lobby has no owner migration (see
                // PlayFabLobbyClient.CreateLobbyWithJoinCodeAsync), so when the owner
                // goes the lobby has nothing left to be: delete it outright.
                if (wasHost)
                {
                    await _online.Lobbies.DeleteLobbyAsync(_lobbyId).ConfigureAwait(false);
                }
                else
                {
                    await _online.Lobbies.LeaveLobbyAsync(_lobbyId).ConfigureAwait(false);
                }
            }

            DisposeParty();
            ResetSessionState();
        }
    }

    public void Send(int peerId, ReadOnlySpan<byte> payload, MessageDelivery delivery)
    {
        if (!HasNetwork || payload.IsEmpty || _localEndpoint is null)
        {
            return;
        }

        try
        {
            _localEndpoint.SendMessage(TargetsFor(peerId), payload.ToArray(), OptionsFor(delivery), QueueFor(delivery));
        }
        catch (Exception ex) when (IsPlatformException(ex))
        {
            // Swallowed as before - a dropped message must not take the match down - but
            // no longer silently. Keyed on the message so a send that fails thirty times
            // a second leaves one line rather than flooding the log.
            CrashLog.MarkOnce("send:" + ex.Message, $"party: send failed ({delivery}): {ex.Message}");
        }
    }

    /// <summary>Drains Party changes from the runtime's single per-frame pump.</summary>
    internal void Pump()
    {
        CompleteCanceledRegions();
        CompleteCanceledOperations();

        if (_party is null)
        {
            return;
        }

        foreach (PartyStateChange change in _party.ProcessStateChanges())
        {
            switch (change)
            {
                case PartyRegionsChanged regions:
                    CompleteRegions(regions.Regions);
                    CompleteOperation(regions);
                    break;
                case PartyOperationCompleted operation:
                    Observe(operation);
                    CompleteOperation(operation);
                    break;
                case PartyEndpointCreated created:
                    ObserveEndpointCreated(created.Endpoint);
                    break;
                case PartyEndpointDestroyed destroyed:
                    ObserveEndpointDestroyed(destroyed.Endpoint);
                    break;
                case PartyEndpointMessageReceived received:
                    OnMessageReceived(received.SenderEndpoint, received.Message);
                    break;
                case PartyChatControlCreated created:
                    ObserveChatControl(created.ChatControl);
                    break;
                case PartyChatControlDestroyed destroyed:
                    ForgetChatControl(destroyed.ChatControl);
                    ChatChanged?.Invoke();
                    break;
                case PartyChatTextReceived text:
                    OnChatTextReceived(text);
                    break;
                case PartyVoiceChatTranscriptionReceived transcription:
                    OnTranscriptionReceived(transcription);
                    break;
                case PartyChatControlPropertiesChanged:
                case PartyLocalChatAudioInputChanged:
                case PartyLocalChatAudioOutputChanged:
                    ChatChanged?.Invoke();
                    break;
                case PartyNetworkDestroyed destroyed:
                    OnNetworkDestroyed(destroyed);
                    break;
            }
        }
    }

    public void SetPeerRestrictions(int peerId, bool allowVoice, bool allowText)
    {
        _restrictions[peerId] = new ChatRestriction(allowVoice, allowText);
        ApplyChatPolicy(peerId);
        ChatChanged?.Invoke();
    }

    public void SetPeerMuted(int peerId, bool muted)
    {
        var changed = muted ? _mutedPeers.Add(peerId) : _mutedPeers.Remove(peerId);
        if (changed)
        {
            ApplyChatPolicy(peerId);
            ChatChanged?.Invoke();
        }
    }

    public bool IsPeerMuted(int peerId) => _mutedPeers.Contains(peerId);

    public ChatIndicator GetChatIndicator(int peerId)
    {
        if (!HasNetwork || !ChatAllowed)
        {
            return ChatIndicator.None;
        }

        if (peerId == LocalPeerId)
        {
            return IsSelfMuted ? ChatIndicator.Muted : LocalIndicator();
        }

        if (IsPeerMuted(peerId)
            || (_restrictions.TryGetValue(peerId, out var restriction) && !restriction.AllowVoice))
        {
            return ChatIndicator.Muted;
        }

        if (_localChat is null || !TryGetChat(peerId, out var remote))
        {
            return ChatIndicator.None;
        }

        try
        {
            return _localChat.GetChatIndicator(remote) switch
            {
                PartyChatControlChatIndicator.Talking => ChatIndicator.Talking,
                PartyChatControlChatIndicator.IncomingVoiceDisabled => ChatIndicator.Muted,
                PartyChatControlChatIndicator.IncomingCommunicationsMuted => ChatIndicator.Muted,
                PartyChatControlChatIndicator.RemoteAudioInputMuted => ChatIndicator.Muted,
                PartyChatControlChatIndicator.NoRemoteInput => ChatIndicator.None,
                _ => ChatIndicator.Available,
            };
        }
        catch (Exception ex) when (IsPlatformException(ex))
        {
            return ChatIndicator.None;
        }
    }

    public void ClearChatRestrictions()
    {
        _mutedPeers.Clear();
        _restrictions.Clear();
        ApplyChatPolicy();
        ChatChanged?.Invoke();
    }

    public PlatformResult SendChatText(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return PlatformResult.Fail(PlatformStatus.Failed, "There is nothing to send.");
        }

        if (!ChatAllowed)
        {
            return PlatformResult.Fail(PlatformStatus.NoPrivilege, "Your account settings do not allow chat.");
        }

        if (!HasNetwork || _localChat is null)
        {
            return PlatformResult.Fail(PlatformStatus.Failed, "You are not in a match.");
        }

        if (IsSelfMuted)
        {
            return PlatformResult.Fail(PlatformStatus.Failed, "You are muted.");
        }

        var trimmed = message.Length > MaxChatChars ? message[..MaxChatChars] : message;
        try
        {
            var data = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(data, LocalPeerId);
            _localChat.SendText(Array.Empty<PartyChatControl>(), trimmed, data);
            ChatTextReceived?.Invoke(LocalPeerId, trimmed, ChatTextKind.Typed);
            return PlatformResult.Ok();
        }
        catch (Exception ex) when (IsPlatformException(ex))
        {
            return PlatformResult.Fail(PlatformStatus.Failed, "That chat message could not be sent.", ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeParty();
        ResetSessionState();
    }

    private async Task<PlatformResult> JoinPartyNetworkAsync(
        string descriptor,
        string? invitation,
        string hostToken,
        string protocolVersion,
        CancellationToken cancellationToken)
    {
        if (HasNetwork)
        {
            return PlatformResult.Fail(PlatformStatus.Failed, "A session is already running.",
                "GameCorePartyService.JoinAsync called with a live network.");
        }

        try
        {
            // XR-014: the host arrives as a token rather than an entity id, so there is
            // nothing to seed the entity map with. The host's entity is learned from its
            // own authenticated endpoint and matched against the token; see IsHostEntity.
            _hostToken = hostToken;
            _hostEntityId = string.Empty;
            IsHost = false;
            EnsureParty();
            EnsurePartyUser();

            var connected = await ConnectAuthenticateEndpointAndChatAsync(descriptor, invitation, cancellationToken)
                .ConfigureAwait(false);
            if (connected.Failed)
            {
                await LeaveAsync().ConfigureAwait(false);
                return connected;
            }

            _peerAssignment = new TaskCompletionSource<int>();
            SendControlHello();
            var assigned = await AwaitPeerAssignmentAsync(cancellationToken).ConfigureAwait(false);
            if (assigned <= IPartyService.HostPeerId)
            {
                await LeaveAsync().ConfigureAwait(false);
                return PlatformResult.Fail(PlatformStatus.Failed, "The host assigned an invalid peer id.",
                    $"Assigned peer id {assigned}.");
            }

            HasNetwork = true;
            LocalPeerId = assigned;
            ConnectionString = EncodeInvite(descriptor, invitation, _hostToken, protocolVersion);
            if (_peerToEndpoint.ContainsKey(IPartyService.HostPeerId))
            {
                PeerJoined?.Invoke(IPartyService.HostPeerId);
            }

            return PlatformResult.Ok();
        }
        catch (OperationCanceledException)
        {
            await LeaveAsync().ConfigureAwait(false);
            return PlatformResult.Canceled();
        }
        catch (Exception ex) when (IsPlatformException(ex))
        {
            await LeaveAsync().ConfigureAwait(false);
            return PlatformResult.Fail(PlatformStatus.Failed, "That online match could not be joined.", ex.Message);
        }
    }

    private async Task<PlatformResult> ConnectAuthenticateEndpointAndChatAsync(
        string descriptor,
        string? invitation,
        CancellationToken cancellationToken)
    {
        (PartyOperationId connect, PartyNetwork network) =
            _party!.ConnectToNetwork(PartyNetworkDescriptor.Deserialize(descriptor));
        _network = network;

        PartyOperationId authenticate = network.AuthenticateLocalUser(
            _partyUser!, string.IsNullOrEmpty(invitation) ? null : invitation);

        var connected = await AwaitOperationAsync<PartyConnectToNetworkCompleted>(connect, cancellationToken)
            .ConfigureAwait(false);
        if (!connected.Succeeded || connected.Network is null)
        {
            return PlatformResult.Fail(MapPartyStatus(connected.Result),
                PlayerMessage("Could not connect to the online match.", connected.Result), Describe(connected));
        }

        _network = connected.Network;
        var authenticated = await AwaitOperationAsync<PartyAuthenticateLocalUserCompleted>(
            authenticate, cancellationToken).ConfigureAwait(false);
        if (!authenticated.Succeeded)
        {
            return PlatformResult.Fail(MapPartyStatus(authenticated.Result),
                PlayerMessage("Could not authenticate with the online match.", authenticated.Result),
                Describe(authenticated));
        }

        var endpoint = await AwaitOperationAsync<PartyCreateEndpointCompleted>(
            _network.CreateEndpoint(_partyUser), cancellationToken).ConfigureAwait(false);
        if (!endpoint.Succeeded || endpoint.Endpoint is null)
        {
            return PlatformResult.Fail(MapPartyStatus(endpoint.Result),
                PlayerMessage("Could not join voice and messaging for the match.", endpoint.Result),
                Describe(endpoint));
        }

        _localEndpoint = endpoint.Endpoint;

        var chat = await AwaitOperationAsync<PartyCreateChatControlCompleted>(
            _party.LocalDevice.CreateChatControl(_partyUser!, LanguageCode), cancellationToken)
            .ConfigureAwait(false);
        if (chat.Succeeded && chat.ChatControl is not null)
        {
            _localChat = chat.ChatControl;
            ApplyLocalMute();

            // PlatformUserDefault requires the non-null, non-empty platform user context
            // (the Xbox user's XUID) to resolve "this user's" default device; passing none
            // fails both calls with PartyError "no platform user was specified".
            var xuid = _identity.CurrentUser?.XboxUserId;
            _ = _localChat.SetAudioInput(PartyAudioDeviceSelectionType.PlatformUserDefault, xuid);
            _ = _localChat.SetAudioOutput(PartyAudioDeviceSelectionType.PlatformUserDefault, xuid);
            await TryAwait(_network.ConnectChatControl(_localChat), cancellationToken).ConfigureAwait(false);
            ApplyChatPolicy();
        }

        return PlatformResult.Ok();
    }

    private void EnsureParty()
    {
        if (_party is not null)
        {
            return;
        }

        PartyManager.SetLocalUdpSocketBindAddress(new PartyLocalUdpSocketBindAddressConfiguration
        {
            Options = PartyLocalUdpSocketBindAddressOptions.ExcludeGameCorePreferredUdpMultiplayerPort,
            Port = 0,
        });

        _party = PartyManager.Initialize(_configuration!.TitleId);
    }

    private void EnsurePartyUser() => _partyUser ??= _party!.CreateLocalUser(_online!.Entity!);

    private PlatformResult EnsureAvailable()
    {
        if (_runtime is null || _online is null || _configuration is null)
        {
            return PlatformResult.Unavailable("The GameCore Party service was constructed without online services.");
        }

        if (!_runtime.IsInitialized || _runtime.Runtime is null)
        {
            return PlatformResult.Unavailable("The Microsoft GDK is not running in this build.");
        }

        if (_identity.CurrentUser is null)
        {
            return PlatformResult.Fail(PlatformStatus.NotSignedIn, "You need to be signed in to play online.");
        }

        if (!PartyRuntimeProbe.IsAvailable)
        {
            return PlatformResult.Unavailable(PartyRuntimeProbe.UnavailableReason);
        }

        if (_configuration.IsPlaceholder)
        {
            return PlatformResult.Unavailable(_configuration.UnavailableReason);
        }

        if (_online.Entity is null || string.IsNullOrEmpty(_online.EntityId))
        {
            var reason = _online.UnavailableReason;

            // A sign-in that timed out or could not reach PlayFab is a network condition,
            // not a feature this build does not have, and saying "online play is
            // unavailable" for it told the player the wrong thing to do about it: there is
            // nothing to fix in the title, and trying again once the connection is healthy
            // works. Reported as a network failure so every refusal site words it that way.
            var transient = _online.LastSignInStatus
                is PlatformStatus.TimedOut
                or PlatformStatus.NetworkFailure;

            return PlatformResult.Fail(
                transient ? PlatformStatus.NetworkFailure : PlatformStatus.Unavailable,
                string.IsNullOrWhiteSpace(reason)
                    ? "Online play needs a PlayFab entity for the signed-in Xbox user. Sign out and sign in again."
                    : reason,
                _online.LastSignInDiagnostics);
        }

        return PlatformResult.Ok();
    }

    /// <summary>
    /// <see cref="EnsureAvailable"/>, retrying the PlayFab half of sign-in first when that
    /// is the only thing missing and its last failure was a transient one (XR-074).
    /// </summary>
    /// <remarks>
    /// The retry belongs here, on the entry points a player drives, rather than on a timer:
    /// pressing Host or Join is both the moment the entity is actually needed and a fresh
    /// statement of intent, so one round trip is warranted and the wait is one the player
    /// asked for. A standing refusal - a placeholder title, or an account that may not have
    /// a publisher account provisioned - is not retried, because nothing about it can have
    /// changed and the attempt would cost the player the sign-in timeout on every press.
    /// </remarks>
    private async Task<PlatformResult> EnsureAvailableAsync(CancellationToken cancellationToken)
    {
        var available = EnsureAvailable();

        if (available.Succeeded || !CanRetryPlayFabSignIn())
        {
            return available;
        }

        await _identity.RetryPlayFabIdentityAsync(cancellationToken).ConfigureAwait(false);

        return EnsureAvailable();
    }

    /// <summary>
    /// True when the only thing standing between this service and a usable session is a
    /// PlayFab identity that may still be obtainable.
    /// </summary>
    private bool CanRetryPlayFabSignIn()
        => _online is not null
           && _runtime is { IsInitialized: true }
           && _runtime.Runtime is not null
           && _identity.CurrentUser is not null
           && _online.CanAttemptLogin
           && _online.CanRetrySignIn
           && (_online.Entity is null || string.IsNullOrEmpty(_online.EntityId));

    private string UnavailableReason() => EnsureAvailable().Message ?? "Online play is not available in this build.";

    private Task<IReadOnlyList<PartyRegion>> AwaitRegionsAsync(CancellationToken cancellationToken)
    {
        var regions = _party!.GetRegions();
        if (regions.Count > 0)
        {
            return Task.FromResult(regions);
        }

        var pending = new PendingRegions(cancellationToken);
        _pendingRegions.Add(pending);
        return pending.Completion.Task;
    }

    private Task<T> AwaitOperationAsync<T>(PartyOperationId operation, CancellationToken cancellationToken)
        where T : PartyOperationCompleted
    {
        var pending = new PendingOperation(typeof(T), cancellationToken);
        _pendingOperations[operation] = pending;
        return pending.Completion.Task.ContinueWith(
            task => (T)task.Result,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private Task<int> AwaitPeerAssignmentAsync(CancellationToken cancellationToken)
    {
        if (LocalPeerId > IPartyService.HostPeerId)
        {
            return Task.FromResult(LocalPeerId);
        }

        var assignment = _peerAssignment ??= new TaskCompletionSource<int>();
        _peerAssignmentCancellation = cancellationToken;
        return assignment.Task;
    }

    private async Task TryAwait(PartyOperationId operation, CancellationToken cancellationToken = default)
    {
        try
        {
            await AwaitOperationAsync<PartyOperationCompleted>(operation, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsPlatformException(ex) || ex is InvalidCastException or AggregateException)
        {
        }
    }

    private void CompleteOperation(PartyOperationCompleted operation)
    {
        if (!_pendingOperations.Remove(operation.Operation, out var pending))
        {
            return;
        }

        if (pending.CancellationToken.IsCancellationRequested)
        {
            pending.Completion.TrySetCanceled(pending.CancellationToken);
        }
        else if (!pending.ExpectedType.IsInstanceOfType(operation))
        {
            pending.Completion.TrySetException(new InvalidOperationException(
                $"Operation {operation.Operation} completed as {operation.GetType().Name}, not {pending.ExpectedType.Name}."));
        }
        else
        {
            pending.Completion.TrySetResult(operation);
        }
    }

    private void CompleteCanceledOperations()
    {
        foreach (var pair in _pendingOperations.ToArray())
        {
            if (pair.Value.CancellationToken.IsCancellationRequested && _pendingOperations.Remove(pair.Key))
            {
                pair.Value.Completion.TrySetCanceled(pair.Value.CancellationToken);
            }
        }

        if (_peerAssignment is not null
            && !_peerAssignment.Task.IsCompleted
            && _peerAssignmentCancellation.IsCancellationRequested)
        {
            _peerAssignment.TrySetCanceled(_peerAssignmentCancellation);
        }
    }

    private void CompleteRegions(IReadOnlyList<PartyRegion> regions)
    {
        if (regions.Count == 0)
        {
            return;
        }

        foreach (var pending in _pendingRegions.ToArray())
        {
            _pendingRegions.Remove(pending);
            pending.Completion.TrySetResult(regions);
        }
    }

    private void CompleteCanceledRegions()
    {
        foreach (var pending in _pendingRegions.ToArray())
        {
            if (pending.CancellationToken.IsCancellationRequested)
            {
                _pendingRegions.Remove(pending);
                pending.Completion.TrySetCanceled(pending.CancellationToken);
            }
        }
    }

    private void Observe(PartyOperationCompleted operation)
    {
        switch (operation)
        {
            case PartyConnectToNetworkCompleted { Succeeded: true, Network: not null } connected:
                _network = connected.Network;
                break;
            case PartyCreateEndpointCompleted { Succeeded: true, Endpoint: not null } endpoint:
                _localEndpoint = endpoint.Endpoint;
                break;
            case PartyCreateChatControlCompleted { Succeeded: true, ChatControl: not null } chat:
                ObserveChatControl(chat.ChatControl);
                break;
        }
    }

    private void ObserveEndpointCreated(PartyEndpoint? endpoint)
    {
        if (endpoint is null)
        {
            return;
        }

        if (endpoint.IsLocal)
        {
            _localEndpoint = endpoint;
            if (IsHost)
            {
                MarkEndpoint(endpoint, IPartyService.HostPeerId);
            }

            return;
        }

        var entityId = endpoint.EntityId ?? string.Empty;
        if (entityId.Length > 0 && _entityToPeer.TryGetValue(entityId, out var knownPeer))
        {
            MarkEndpoint(endpoint, knownPeer);
        }
        else if (!IsHost && IsHostEntity(entityId))
        {
            MarkEndpoint(endpoint, IPartyService.HostPeerId);
        }
    }

    /// <summary>
    /// Whether an endpoint entity is the host's (XR-014).
    /// </summary>
    /// <remarks>
    /// The replacement for the string comparison against a published entity id. The entity
    /// id passed in is the one <b>Party authenticated</b> behind the endpoint, so proving
    /// it against the session's host token is exactly the same statement as the comparison
    /// it replaces - the difference is only that the host's id never had to be published
    /// for a client to make it. The first entity to verify is cached, so the HMAC runs once
    /// per session rather than per message.
    /// </remarks>
    private bool IsHostEntity(string? entityId)
    {
        if (string.IsNullOrEmpty(entityId))
        {
            return false;
        }

        if (_hostEntityId.Length > 0)
        {
            return string.Equals(entityId, _hostEntityId, StringComparison.Ordinal);
        }

        if (!PartyHostToken.Matches(_hostToken, entityId))
        {
            return false;
        }

        _hostEntityId = entityId;
        return true;
    }

    private void ObserveEndpointDestroyed(PartyEndpoint? endpoint)
    {
        if (endpoint is null || !_endpointToPeer.Remove(endpoint.UniqueIdentifier, out var peerId))
        {
            return;
        }

        _peerToEndpoint.Remove(peerId);
        _mutedPeers.Remove(peerId);
        _restrictions.Remove(peerId);

        // Not while leaving, for the same reason OnNetworkDestroyed stays quiet: tearing
        // the local endpoint down also destroys this peer's view of everyone else's, and
        // reporting those as departures told the leaving player's own match director that
        // the other players had gone. A host that quit then ended the match on its own
        // screen - "everyone else left" shown to the one person who did the leaving -
        // while the player actually left behind was told nothing. The local player
        // leaving is not a peer departing.
        if (!_leaving && peerId != LocalPeerId)
        {
            PeerLeft?.Invoke(peerId);
        }
    }

    private void MarkLocalEndpoint(int peerId)
    {
        LocalPeerId = peerId;
        if (_localEndpoint is not null)
        {
            MarkEndpoint(_localEndpoint, peerId);
        }
    }

    private void MarkEndpoint(PartyEndpoint endpoint, int peerId)
    {
        _endpointToPeer[endpoint.UniqueIdentifier] = peerId;
        _peerToEndpoint[peerId] = endpoint;
        MarkEntity(endpoint.EntityId, peerId);
    }

    private void MarkEntity(string? entityId, int peerId)
    {
        if (!string.IsNullOrEmpty(entityId))
        {
            _entityToPeer[entityId] = peerId;
        }
    }

    private void OnMessageReceived(PartyEndpoint? sender, byte[] message)
    {
        if (sender is null || message.Length == 0 || TryReadControl(sender, message))
        {
            return;
        }

        if (!_endpointToPeer.TryGetValue(sender.UniqueIdentifier, out var peerId))
        {
            if (!IsHost)
            {
                return;
            }

            peerId = AssignPeer(sender);
        }

        MessageReceived?.Invoke(peerId, message);
    }

    private bool TryReadControl(PartyEndpoint sender, byte[] message)
    {
        if (message.Length < ControlMagic.Length + 1 || !message.AsSpan(0, ControlMagic.Length).SequenceEqual(ControlMagic))
        {
            return false;
        }

        var kind = message[ControlMagic.Length];
        if (kind == ControlKindHello && IsHost)
        {
            var peerId = AssignPeer(sender);
            SendControlWelcome(sender, peerId);
        }
        else if (kind == ControlKindWelcome && !IsHost && message.Length >= ControlMagic.Length + 5)
        {
            AcceptControlWelcome(sender, BinaryPrimitives.ReadInt32LittleEndian(message.AsSpan(ControlMagic.Length + 1)));
        }
        else if (kind == ControlKindHostClosing && !IsHost)
        {
            AcceptHostClosing(sender);
        }

        return true;
    }

    /// <summary>
    /// Ends the local session because the host said it was leaving (#25).
    /// </summary>
    /// <remarks>
    /// Only the endpoint already mapped to <see cref="IPartyService.HostPeerId"/> is
    /// believed, for exactly the reason <see cref="AcceptControlWelcome"/> documents:
    /// <see cref="ControlMagic"/> authenticates nothing, so any peer could otherwise
    /// evict every other player from a match by sending five bytes.
    /// </remarks>
    private void AcceptHostClosing(PartyEndpoint sender)
    {
        if (!HasNetwork
            || !_endpointToPeer.TryGetValue(sender.UniqueIdentifier, out var peerId)
            || peerId != IPartyService.HostPeerId)
        {
            return;
        }

        foreach (var id in _peerToEndpoint.Keys.Where(id => id != LocalPeerId).ToArray())
        {
            PeerLeft?.Invoke(id);
        }

        ResetSessionState();
        NetworkDestroyed?.Invoke(PlatformResult.Fail(
            PlatformStatus.NetworkFailure,
            "The host left the match.",
            "Received a host-closing control frame."));
    }

    /// <summary>
    /// Accepts the host's answer to <see cref="SendControlHello"/>, which is what tells a
    /// joining client its own peer id and which endpoint is the authority.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every one of these checks is load-bearing, because this message decides who the
    /// host is and the entire authority model rests on that answer.</b>
    /// <c>PartyMatchNetwork</c> derives <c>fromHost</c> purely from the peer id this
    /// mapping produces, and it is what permits a message to move ships, rewrite scores
    /// and end the match. <see cref="ControlMagic"/> is a fixed constant in the binary and
    /// authenticates nothing, so it cannot be treated as a secret.
    /// </para>
    /// <para>
    /// Without these checks, any player already in the Party network could send five bytes
    /// to another client and be promoted to authority over it - the original mapping was
    /// not even replaced, so both endpoints would answer to
    /// <see cref="IPartyService.HostPeerId"/> and nothing about the match would look wrong
    /// until the attacker used it.
    /// </para>
    /// <para>
    /// So: the sender must be the entity the lobby named as host, a join must actually be
    /// outstanding, the endpoint must not already be mapped to some other peer, and the id
    /// must be one the host could legitimately have assigned. Party itself authenticates
    /// the entity id behind <c>PartyEndpoint</c>, which is what makes the first check worth
    /// anything.
    /// </para>
    /// </remarks>
    private void AcceptControlWelcome(PartyEndpoint sender, int peerId)
    {
        // Only the host the lobby named. Anything else is another player in the same
        // network claiming to be the authority. Proved against the session's host token
        // (XR-014) rather than a published entity id - the same check, without the id.
        if (!IsHostEntity(sender.EntityId))
        {
            return;
        }

        // Only while joining. A welcome arriving mid-match answers a question nobody
        // asked, and is the shape a replayed or injected one would take.
        if (_peerAssignment is null || _peerAssignment.Task.IsCompleted)
        {
            return;
        }

        // Never a re-map. An endpoint that already has a peer id keeps it.
        if (_endpointToPeer.TryGetValue(sender.UniqueIdentifier, out var existing)
            && existing != IPartyService.HostPeerId)
        {
            return;
        }

        // The host cannot assign its own id to a client, and a negative id would index
        // nothing the roster has. Checked here rather than only on the awaited result,
        // which ran after the damage was already done.
        if (peerId <= IPartyService.HostPeerId)
        {
            return;
        }

        MarkLocalEndpoint(peerId);
        MarkEndpoint(sender, IPartyService.HostPeerId);
        _peerAssignment.TrySetResult(peerId);
    }

    private int AssignPeer(PartyEndpoint endpoint)
    {
        if (_endpointToPeer.TryGetValue(endpoint.UniqueIdentifier, out var existing))
        {
            return existing;
        }

        var peerId = _nextPeerId++;
        MarkEndpoint(endpoint, peerId);
        PeerJoined?.Invoke(peerId);
        return peerId;
    }

    private void SendControlHello() =>
        _localEndpoint?.SendMessage(Array.Empty<PartyEndpoint>(), ControlMessage(ControlKindHello), PartySendMessageOptions.GuaranteedDelivery);

    private void SendControlWelcome(PartyEndpoint target, int peerId) =>
        _localEndpoint?.SendMessage([target], ControlMessage(ControlKindWelcome, peerId), PartySendMessageOptions.GuaranteedDelivery);

    private static byte[] ControlMessage(byte kind, int peerId = 0)
    {
        var message = new byte[ControlMagic.Length + 1 + (kind == ControlKindWelcome ? 4 : 0)];
        ControlMagic.CopyTo(message, 0);
        message[ControlMagic.Length] = kind;
        if (kind == ControlKindWelcome)
        {
            BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(ControlMagic.Length + 1), peerId);
        }

        return message;
    }

    private IReadOnlyList<PartyEndpoint> TargetsFor(int peerId)
        => peerId == IPartyService.PartyBroadcast
            ? Array.Empty<PartyEndpoint>()
            : _peerToEndpoint.TryGetValue(peerId, out var endpoint) ? [endpoint] : Array.Empty<PartyEndpoint>();

    private static PartySendMessageOptions OptionsFor(MessageDelivery delivery) => delivery switch
    {
        MessageDelivery.Reliable => PartySendMessageOptions.GuaranteedDelivery,
        MessageDelivery.UnreliableSequenced => PartySendMessageOptions.SequentialDelivery,
        _ => PartySendMessageOptions.BestEffortDelivery | PartySendMessageOptions.NonsequentialDelivery,
    };

    /// <summary>
    /// Party has a sequential flag but not Godot's exact unreliable-ordered channel.
    /// Sequenced traffic asks for sequential delivery with a short queue timeout; plain
    /// unreliable keeps Party's non-sequential default and a slightly longer timeout.
    /// </summary>
    private static PartySendMessageQueuingConfiguration? QueueFor(MessageDelivery delivery) => delivery switch
    {
        MessageDelivery.UnreliableSequenced => new PartySendMessageQueuingConfiguration
        {
            Timeout = TimeSpan.FromMilliseconds(100),
        },
        MessageDelivery.Unreliable => new PartySendMessageQueuingConfiguration
        {
            Priority = -1,
            Timeout = TimeSpan.FromMilliseconds(250),
        },
        _ => null,
    };

    private void ObserveChatControl(PartyChatControl? control)
    {
        if (control is null)
        {
            return;
        }

        if (control.IsLocal)
        {
            _localChat = control;
            ApplyLocalMute();
        }
        else
        {
            ApplyChatPolicy(control);
        }

        ChatChanged?.Invoke();
    }

    private void ForgetChatControl(PartyChatControl? control)
    {
        if (control is not null && ReferenceEquals(control, _localChat))
        {
            _localChat = null;
        }
    }

    private void ApplyLocalMute()
    {
        try
        {
            if (_localChat is not null)
            {
                _localChat.AudioInputMuted = IsSelfMuted || !ChatAllowed;
            }
        }
        catch (Exception ex) when (IsPlatformException(ex))
        {
        }
    }

    private void ApplyChatPolicy(int peerId)
    {
        if (_localChat is not null && TryGetChat(peerId, out var chat))
        {
            ApplyChatPolicy(chat);
        }
    }

    private void ApplyChatPolicy()
    {
        ApplyLocalMute();
        if (_party is null)
        {
            return;
        }

        foreach (var chat in _party.ChatControls.Where(chat => !chat.IsLocal))
        {
            ApplyChatPolicy(chat);
        }
    }

    private void ApplyChatPolicy(PartyChatControl remote)
    {
        if (_localChat is null || remote.IsLocal)
        {
            return;
        }

        var peerId = PeerIdForChat(remote);

        // An unmapped peer, or a peer with no evaluated restriction, is denied both
        // channels (XR-015). Both of these used to default to (true, true), so a peer the
        // privacy evaluation had not reached yet was cleared for voice and text twice
        // over. The restriction is applied the moment EvaluateAsync answers for them.
        var restriction = peerId is null
            ? ChatRestriction.Denied
            : _restrictions.GetValueOrDefault(peerId.Value, ChatRestriction.Denied);
        var muted = peerId is not null && IsPeerMuted(peerId.Value);
        var allowVoice = ChatAllowed && !muted && restriction.AllowVoice;
        var allowText = ChatAllowed && !muted && restriction.AllowText;

        try
        {
            _localChat.SetIncomingAudioMuted(remote, !allowVoice);
            _localChat.SetIncomingTextMuted(remote, !allowText);
            var permissions = PartyChatPermissionOptions.None;
            if (ChatAllowed && !IsSelfMuted)
            {
                permissions |= PartyChatPermissionOptions.SendAudio;
            }

            if (allowVoice)
            {
                permissions |= PartyChatPermissionOptions.ReceiveAudio;
            }

            if (allowText)
            {
                permissions |= PartyChatPermissionOptions.ReceiveText;
            }

            _localChat.SetPermissions(remote, permissions);
        }
        catch (Exception ex) when (IsPlatformException(ex))
        {
        }
    }

    private bool TryGetChat(int peerId, out PartyChatControl chat)
    {
        chat = null!;
        if (_party is null)
        {
            return false;
        }

        foreach (var control in _party.ChatControls)
        {
            if (!control.IsLocal && PeerIdForChat(control) == peerId)
            {
                chat = control;
                return true;
            }
        }

        return false;
    }

    private int? PeerIdForChat(PartyChatControl control)
        => !string.IsNullOrEmpty(control.EntityId) && _entityToPeer.TryGetValue(control.EntityId, out var peerId)
            ? peerId
            : null;

    private ChatIndicator LocalIndicator()
    {
        if (_localChat is null)
        {
            return ChatIndicator.None;
        }

        try
        {
            return _localChat.LocalChatIndicator switch
            {
                PartyLocalChatControlChatIndicator.Talking => ChatIndicator.Talking,
                PartyLocalChatControlChatIndicator.AudioInputMuted => ChatIndicator.Muted,
                PartyLocalChatControlChatIndicator.NoAudioInput => ChatIndicator.None,
                _ => ChatIndicator.Available,
            };
        }
        catch (Exception ex) when (IsPlatformException(ex))
        {
            return ChatIndicator.None;
        }
    }

    private void OnChatTextReceived(PartyChatTextReceived text)
    {
        if (!ChatAllowed || text.SenderChatControl is null || string.IsNullOrEmpty(text.ChatText))
        {
            return;
        }

        var peerId = PeerIdForChat(text.SenderChatControl) ?? ReadPeerId(text.Data);
        if (peerId is null || IsPeerMuted(peerId.Value)
            || (_restrictions.TryGetValue(peerId.Value, out var restriction) && !restriction.AllowText))
        {
            return;
        }

        RaiseVerifiedChatText(peerId.Value, text.ChatText, ChatTextKind.Typed);
    }

    private void OnTranscriptionReceived(PartyVoiceChatTranscriptionReceived transcription)
    {
        if (!ChatAllowed || transcription.SenderChatControl is null || string.IsNullOrEmpty(transcription.Transcription))
        {
            return;
        }

        var peerId = PeerIdForChat(transcription.SenderChatControl);
        if (peerId is null || IsPeerMuted(peerId.Value)
            || (_restrictions.TryGetValue(peerId.Value, out var restriction) && !restriction.AllowText))
        {
            return;
        }

        RaiseVerifiedChatText(peerId.Value, transcription.Transcription, ChatTextKind.VoiceTranscription);
    }

    /// <summary>
    /// Raises <see cref="ChatTextReceived"/> only for text that string verification has
    /// accepted (XR-018).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The requirement covers text the title <i>displays</i>, not only text it sends.
    /// Party's own text moderation is a service-side option that is not guaranteed to be
    /// configured for this title, so inbound text was reaching the roster unchecked even
    /// though every outbound message went through
    /// <see cref="GameCoreModerationService.VerifyTextAsync"/>.
    /// </para>
    /// <para>
    /// Verification is a round trip and this is called from the Party state-change pump,
    /// which must not block. The message is therefore released asynchronously, and
    /// <b>dropped</b> - not shown - if verification fails or cannot be completed. Chat
    /// text has no ordering contract to preserve and no acknowledgement to send, so a
    /// dropped message is simply a message the player never sees, which is the correct
    /// outcome for text that could not be shown to be acceptable.
    /// </para>
    /// <para>
    /// The event is raised from the verification continuation. That is the same thread
    /// affinity every other Party event on this class has, because
    /// <see cref="GameCoreModerationService.VerifyTextAsync"/> marshals its own
    /// completion through <see cref="PumpDispatcher"/>.
    /// </para>
    /// </remarks>
    private void RaiseVerifiedChatText(int peerId, string chatText, ChatTextKind kind)
    {
        if (_moderation is null)
        {
            return;
        }

        _ = VerifyAndRaiseAsync(peerId, chatText, kind);
    }

    private async Task VerifyAndRaiseAsync(int peerId, string chatText, ChatTextKind kind)
    {
        try
        {
            var verified = await _moderation!.VerifyTextAsync(chatText).ConfigureAwait(false);

            // Re-checked after the round trip: the peer can have been muted, restricted
            // or disconnected, or chat revoked entirely, while it was in flight.
            if (verified.Failed || verified.Value is null || _disposed || !ChatAllowed
                || IsPeerMuted(peerId)
                || (_restrictions.TryGetValue(peerId, out var restriction) && !restriction.AllowText))
            {
                return;
            }

            ChatTextReceived?.Invoke(peerId, verified.Value, kind);
        }
        catch (Exception ex) when (IsPlatformException(ex) || ex is OperationCanceledException)
        {
            CrashLog.MarkOnce(
                "chat-verify",
                $"party: inbound chat verification failed, message dropped: {ex.Message}");
        }
    }

    private static int? ReadPeerId(byte[] data)
        => data.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(data) : null;

    private void OnNetworkDestroyed(PartyNetworkDestroyed destroyed)
    {
        if (_leaving)
        {
            return;
        }

        foreach (var peerId in _peerToEndpoint.Keys.Where(id => id != LocalPeerId).ToArray())
        {
            PeerLeft?.Invoke(peerId);
        }

        ResetSessionState();
        NetworkDestroyed?.Invoke(PlatformResult.Fail(MapDestroyedStatus(destroyed.Reason),
            "The online match ended.",
            $"PartyNetworkDestroyed: {destroyed.Reason} (detail {destroyed.ErrorDetail})."));
    }

    private void ResetSessionState()
    {
        HasNetwork = false;
        IsHost = false;
        LocalPeerId = 0;
        ConnectionString = string.Empty;
        JoinCode = string.Empty;
        _lobbyId = string.Empty;
        _hostEntityId = string.Empty;
        _hostToken = string.Empty;
        _nextPeerId = IPartyService.HostPeerId + 1;
        _network = null;
        _localEndpoint = null;
        _localChat = null;
        _partyUser = null;
        _peerAssignment = null;
        _peerAssignmentCancellation = default;
        _leaving = false;
        _pendingOperations.Clear();
        _pendingRegions.Clear();
        _endpointToPeer.Clear();
        _peerToEndpoint.Clear();
        _entityToPeer.Clear();
        _mutedPeers.Clear();
        _restrictions.Clear();
    }

    private void DisposeParty()
    {
        try
        {
            _party?.Dispose();
        }
        catch (Exception ex) when (IsPlatformException(ex))
        {
        }

        _party = null;
    }

    private static PartyNetworkConfiguration NetworkConfiguration(int maxPlayers) => new()
    {
        MaxUserCount = (uint)Math.Clamp(maxPlayers, 2, MaxPartyPlayers),
        MaxDeviceCount = (uint)Math.Clamp(maxPlayers, 2, MaxPartyPlayers),
        MaxUsersPerDeviceCount = 1,
        MaxDevicesPerUserCount = 1,
        MaxEndpointsPerDeviceCount = 1,
        DirectPeerConnectivityOptions = PartyDirectPeerConnectivityOptions.None,
    };

    private static PlatformStatus MapPartyStatus(PartyStateChangeResult result) => result switch
    {
        PartyStateChangeResult.CanceledByTitle => PlatformStatus.Canceled,
        PartyStateChangeResult.InternetConnectivityError => PlatformStatus.NetworkFailure,
        PartyStateChangeResult.NoServersAvailable => PlatformStatus.NetworkFailure,
        PartyStateChangeResult.NetworkNoLongerExists => PlatformStatus.NetworkFailure,
        PartyStateChangeResult.NetworkNotJoinable => PlatformStatus.NetworkFailure,
        PartyStateChangeResult.FailedToBindToLocalUdpSocket => PlatformStatus.NetworkFailure,
        _ => PlatformStatus.Failed,
    };

    /// <summary>
    /// Builds a player-facing message for a failed Party operation, distinguishing the
    /// collapsed <see cref="PlatformStatus.NetworkFailure"/> reasons <see cref="MapPartyStatus"/>
    /// shares by status code (XR-074) - "no internet", "the match is gone" and "the match
    /// is full/closed" call for different player reactions, and a single shared sentence
    /// for all of them left the player guessing which one to try.
    /// </summary>
    /// <param name="action">The player-facing clause describing what failed, e.g. "Could not connect to the online match."</param>
    private static string PlayerMessage(string action, PartyStateChangeResult result) => result switch
    {
        PartyStateChangeResult.CanceledByTitle => action,
        PartyStateChangeResult.InternetConnectivityError =>
            $"{action} Check your internet connection and try again.",
        PartyStateChangeResult.NoServersAvailable =>
            $"{action} The online service is temporarily unavailable. Please try again later.",
        PartyStateChangeResult.NetworkNoLongerExists =>
            $"{action} This match has ended.",
        PartyStateChangeResult.NetworkNotJoinable =>
            $"{action} This match is full or is no longer accepting players.",
        PartyStateChangeResult.FailedToBindToLocalUdpSocket =>
            $"{action} A network connection could not be opened. Check your network or firewall settings.",
        _ => action,
    };

    private static PlatformStatus MapDestroyedStatus(PartyDestroyedReason reason) => reason switch
    {
        PartyDestroyedReason.Requested => PlatformStatus.Ok,
        PartyDestroyedReason.Disconnected => PlatformStatus.NetworkFailure,
        PartyDestroyedReason.DeviceLostAuthentication => PlatformStatus.NotSignedIn,
        _ => PlatformStatus.Failed,
    };

    private static string Describe(PartyOperationCompleted operation)
        => $"{operation.GetType().Name} completed with {operation.Result} (detail {operation.ErrorDetail}).";

    /// <summary>
    /// Appends provider diagnostics to a player-facing message in Debug builds only.
    /// This lets a devkit run show the real Party/PlayFab failure detail (state-change
    /// result, HRESULT, HTTP status) without leaking that detail into a Release UI.
    /// </summary>
    private static string WithDiagnostics(string message, string? diagnostics)
    {
#if DEBUG
        if (!string.IsNullOrWhiteSpace(diagnostics))
        {
            return $"{message} [{diagnostics}]";
        }
#endif
        return message;
    }

    private static bool IsPlatformException(Exception ex)
        => ex is GameRuntimeException
           or PartyException
           or DllNotFoundException
           or EntryPointNotFoundException
           or PlatformNotSupportedException
           or ObjectDisposedException
           or InvalidOperationException
           or ArgumentException;

    private static bool TryReadNetworkDescriptor(
        PlayFabLobby lobby,
        out string descriptor,
        out string invitation,
        out string hostToken,
        out string diagnostics)
    {
        descriptor = string.Empty;
        invitation = string.Empty;
        diagnostics = string.Empty;

        // XR-014: nothing this title publishes carries the host's entity id any more. The
        // lobby owner entity is PlayFab's own field on the lobby record and is the only
        // host identity available when the lobby was written by a build that predates host
        // tokens; it is minted into a token here and never re-published.
        hostToken = string.Empty;

        if (lobby.LobbyData.TryGetValue(DescriptorLobbyDataKey, out descriptor!) && descriptor.Length > 0)
        {
            lobby.LobbyData.TryGetValue(InvitationLobbyDataKey, out invitation!);
            lobby.LobbyData.TryGetValue(HostTokenLobbyDataKey, out hostToken!);

            if (string.IsNullOrEmpty(hostToken) && lobby.OwnerEntityId.Length > 0)
            {
                hostToken = PartyHostToken.Create(lobby.OwnerEntityId);
            }

            return true;
        }

        diagnostics = $"Lobby {lobby.LobbyId} did not contain '{DescriptorLobbyDataKey}'.";
        return false;
    }

    internal static string EncodeInvite(
        string descriptor,
        string? invitation,
        string hostToken,
        string protocolVersion)
    {
        var payload = JsonSerializer.Serialize(
            new PartyInviteEnvelope(
                descriptor,
                invitation ?? string.Empty,
                hostToken,
                protocolVersion),
            PartyInviteJsonContext.Default.PartyInviteEnvelope);
        return "gdk-party:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    internal static bool TryDecodeInvite(
        string connectionString,
        out string descriptor,
        out string invitation,
        out string hostToken,
        out string protocolVersion)
    {
        descriptor = string.Empty;
        invitation = string.Empty;
        hostToken = string.Empty;
        protocolVersion = string.Empty;

        var text = connectionString.Trim();
        if (text.StartsWith("gdk-party:", StringComparison.OrdinalIgnoreCase))
        {
            var encoded = text["gdk-party:".Length..].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
            try
            {
                var payload = JsonSerializer.Deserialize(
                    Encoding.UTF8.GetString(Convert.FromBase64String(encoded)),
                    PartyInviteJsonContext.Default.PartyInviteEnvelope);
                descriptor = payload?.Descriptor ?? string.Empty;
                invitation = payload?.Invitation ?? string.Empty;
                hostToken = payload?.HostToken ?? string.Empty;
                protocolVersion = payload?.ProtocolVersion ?? string.Empty;
                return descriptor.Length > 0;
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            {
                return false;
            }
        }

        descriptor = text;
        return descriptor.Length > 0;
    }

    private sealed class PendingOperation(Type expectedType, CancellationToken cancellationToken)
    {
        public Type ExpectedType { get; } = expectedType;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public TaskCompletionSource<PartyOperationCompleted> Completion { get; } = new();
    }

    private sealed class PendingRegions(CancellationToken cancellationToken)
    {
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public TaskCompletionSource<IReadOnlyList<PartyRegion>> Completion { get; } = new();
    }

    /// <summary>
    /// A peer's evaluated voice and text permissions.
    /// </summary>
    /// <remarks>
    /// <see cref="Denied"/> is the default for a peer that has not been evaluated, so a
    /// roster entry the privacy service has not answered for yet cannot talk or be heard
    /// (XR-015).
    /// </remarks>
    private readonly record struct ChatRestriction(bool AllowVoice, bool AllowText)
    {
        public static ChatRestriction Denied => new(false, false);
    }
}
