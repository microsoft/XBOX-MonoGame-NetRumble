namespace NetRumble.Platform;

/// <summary>
/// Networking transport, matchmaking and voice/text chat.
/// </summary>
/// <remarks>
/// <para>
/// Ported from <c>scripts/services/party_service.gd</c>. The host creates a PlayFab
/// Party network and advertises its descriptor on a PlayFab Lobby keyed by a
/// five-character join code; clients resolve that code back to the network.
/// </para>
/// <para>
/// <b>This is the interface that changed most in the port.</b> Godot got Party's
/// <c>MultiplayerPeerExtension</c> for free and ran its high-level
/// <c>MultiplayerAPI</c> (30 <c>@rpc</c> functions) on top. MonoGame has no
/// networking at all, so this interface exposes raw reliable/unreliable message
/// send-and-receive, and <c>NetRumble.Core</c> layers its own message dispatch over
/// it. Reliability tiers are preserved exactly: roster/score/lobby traffic is
/// <see cref="MessageDelivery.Reliable"/>, per-frame input and world snapshots are
/// <see cref="MessageDelivery.UnreliableSequenced"/>.
/// </para>
/// <para>
/// A provider is free to implement this over something other than Party (a LAN/UDP
/// provider for local testing is an obvious second implementation) — nothing in this
/// interface names Party.
/// </para>
/// </remarks>
public interface IPartyService
{
    /// <summary>Peer id of the host. Matches the Godot port's <c>HOST_PEER_ID</c>.</summary>
    const int HostPeerId = 1;

    /// <summary>True when a network exists, whether hosting or joined.</summary>
    bool HasNetwork { get; }

    /// <summary>True when this instance is the authority.</summary>
    bool IsHost { get; }

    /// <summary>This instance's peer id, or 0 when not connected.</summary>
    int LocalPeerId { get; }

    /// <summary>
    /// Lobby connection string for the current network, used to build invites.
    /// Empty when not hosting or not connected.
    /// </summary>
    string ConnectionString { get; }

    /// <summary>Five-character join code for the current lobby. Empty when not in one.</summary>
    string JoinCode { get; }

    /// <summary>
    /// Raised when a remote peer becomes reachable, and once for each peer.
    /// </summary>
    /// <remarks>
    /// <b>Symmetric.</b> A host raises it for each client that connects, and a client
    /// raises it for the host once its own connect completes. The temptation is to treat
    /// it as "a client joined me", which leaves a client never learning it has a peer at
    /// all - the event then means two different things depending on which end reads it,
    /// and anything waiting for a peer on the client side waits forever.
    /// </remarks>
    event Action<int>? PeerJoined;

    /// <summary>Raised when a peer becomes unreachable, and once for each peer.</summary>
    /// <remarks>
    /// <b>Symmetric</b>, in the same way and for the same reason as <see cref="PeerJoined"/>.
    /// A host raises it for each client that disconnects or times out, and a client raises
    /// it for the host when the host goes away - even though the client is also about to
    /// get <see cref="NetworkDestroyed"/>, which is a larger event and not a substitute:
    /// one departure must not raise the event on only one of the two ends that saw it.
    /// A player choosing to leave raises neither event on the leaver.
    /// </remarks>
    event Action<int>? PeerLeft;

    /// <summary>Raised when the network goes away underneath the title.</summary>
    event Action<PlatformResult>? NetworkDestroyed;

    /// <summary>Raised for every message received. Payload is only valid for the duration of the call.</summary>
    event PartyMessageHandler? MessageReceived;

    /// <summary>Raised when a chat indicator or mute state changes.</summary>
    event Action? ChatChanged;

    /// <summary>Speech-to-text or text chat line from a peer.</summary>
    /// <remarks>
    /// XR-003: the kind is carried because the two are shown under different rules. A
    /// player can switch voice transcription off, and doing so must not also silence
    /// messages another player typed - those have no other surface to arrive on, so
    /// suppressing them means a message is received with nowhere to read it.
    /// </remarks>
    event Action<int, string, ChatTextKind>? ChatTextReceived;

    // --- Session lifecycle --------------------------------------------------

    /// <summary>
    /// Creates a network and advertises it on a lobby, returning the join code.
    /// </summary>
    /// <param name="protocolVersion">
    /// The wire contract this build speaks, published alongside the join code so a
    /// prospective joiner can refuse a mismatch before it becomes a silent failure. Passed
    /// in rather than read from a constant here because the wire contract belongs to the
    /// game's codec, which sits above this assembly.
    /// </param>
    Task<PlatformResult<string>> HostAsync(
        int maxPlayers,
        string gameMode,
        string protocolVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a five-character join code and connects.
    /// </summary>
    /// <param name="protocolVersion">
    /// The wire contract this build speaks. A lobby advertising anything else - including
    /// nothing at all, which is what a build older than this check looks like - is refused
    /// before any traffic is exchanged.
    /// </param>
    Task<PlatformResult> JoinAsync(
        string joinCode,
        string protocolVersion,
        CancellationToken cancellationToken = default);

    /// <summary>Connects using a connection string carried by a platform invite.</summary>
    /// <param name="protocolVersion">As for <see cref="JoinAsync"/>.</param>
    Task<PlatformResult> JoinByConnectionStringAsync(
        string connectionString,
        string protocolVersion,
        CancellationToken cancellationToken = default);

    /// <summary>Tears the session down. Safe when there is no network.</summary>
    Task LeaveAsync();

    // --- Messaging ----------------------------------------------------------

    /// <summary>
    /// Sends to one peer, or to everyone when <paramref name="peerId"/> is
    /// <see cref="PartyBroadcast"/>.
    /// </summary>
    void Send(int peerId, ReadOnlySpan<byte> payload, MessageDelivery delivery);

    /// <summary>Target value meaning "every peer except this one".</summary>
    const int PartyBroadcast = 0;

    // --- Chat ---------------------------------------------------------------

    /// <summary>
    /// Master switch from the communications privilege check (XR-045). When false the
    /// provider must suppress all voice and text.
    /// </summary>
    bool ChatAllowed { get; set; }

    bool IsSelfMuted { get; set; }

    /// <summary>Per-peer restrictions derived from privacy verdicts (XR-015).</summary>
    void SetPeerRestrictions(int peerId, bool allowVoice, bool allowText);

    void SetPeerMuted(int peerId, bool muted);
    bool IsPeerMuted(int peerId);

    /// <summary>Microphone state to draw next to a roster row.</summary>
    ChatIndicator GetChatIndicator(int peerId);

    void ClearChatRestrictions();

    /// <summary>Sends a text chat line. Returns why it was refused, for XR-018.</summary>
    PlatformResult SendChatText(string message);
}

/// <summary>Handler for <see cref="IPartyService.MessageReceived"/>.</summary>
public delegate void PartyMessageHandler(int senderPeerId, ReadOnlySpan<byte> payload);

/// <summary>
/// Delivery guarantee, mapping the Godot port's per-<c>@rpc</c> reliability onto
/// Party's send-queue configuration.
/// </summary>
public enum MessageDelivery
{
    /// <summary>Roster, score, lobby and match-flow traffic. Must arrive.</summary>
    Reliable,

    /// <summary>
    /// Per-frame ship input and world snapshots. Drops are fine; out-of-order is not,
    /// so stale packets are discarded rather than applied.
    /// </summary>
    UnreliableSequenced,

    /// <summary>
    /// One-shot cosmetic effects. Matches the Godot port's plain <c>"unreliable"</c> tier.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="UnreliableSequenced"/> on purpose. Each effect is
    /// self-contained and fires at a position carried in its own packet, so an old one
    /// arriving late is still correct - dropping it to preserve ordering would silently
    /// lose explosions for no benefit.
    /// </remarks>
    Unreliable,
}

/// <summary>
/// Microphone state for one player, from <c>PartyService.ChatIndicator</c>.
/// </summary>
public enum ChatIndicator
{
    /// <summary>No chat control for this player, or chat is off entirely.</summary>
    None = 0,

    /// <summary>Chat available and unmuted.</summary>
    Available,

    /// <summary>Incoming audio muted locally, or the local mic is muted.</summary>
    Muted,

    /// <summary>
    /// Currently speaking. Unreachable through the Godot addon, which had no
    /// per-control audio-level query; kept so a richer provider can drive it.
    /// </summary>
    Talking,
}

/// <summary>
/// Where a chat line came from, which decides the rules under which it is shown
/// (XR-003).
/// </summary>
public enum ChatTextKind
{
    /// <summary>A message another player typed, or a notice the title itself raised.</summary>
    Typed = 0,

    /// <summary>
    /// A speech-to-text transcription of a player's voice. Suppressed when the player
    /// has turned voice transcription off; typed messages are not.
    /// </summary>
    VoiceTranscription,
}
