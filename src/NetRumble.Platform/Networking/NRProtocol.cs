namespace NetRumble.Platform.Networking;

/// <summary>
/// The version of the multiplayer wire contract this build speaks.
/// </summary>
/// <remarks>
/// <para>
/// Two builds that disagree here cannot play together, and the failure is silent unless
/// something checks. What a player sees is a match that joins successfully and then does
/// nothing: no roster, no ships, no error. This class exists to turn that into a sentence.
/// </para>
/// <para>
/// <b>Why it lives here and not beside the codec it describes.</b> The number describes
/// <c>MatchMessageCodec</c> in NetRumble.Core, but the refusal has to happen in the
/// transports - which sit <i>below</i> Core and cannot see it. Duplicating the constant so
/// each layer owns a copy is how two builds end up disagreeing about what version they
/// are, so the single copy lives at the bottom where everything can reach it, and the
/// callers above pass it down.
/// </para>
/// <para>
/// <b>One number, not two.</b> The Godot original split this into a wire version and an
/// RPC-set version, because Godot identifies an <c>@rpc</c> method on the wire by its
/// <i>index</i> into the declaring node's RPC list - so adding or renaming any one of them
/// shifts every later index and breaks compatibility without touching a single payload.
/// That failure mode does not exist here: <c>MessageType</c> assigns every message an
/// explicit id, and adding a new one cannot move an existing one. Only the payload schemas
/// can break compatibility, so only they are versioned.
/// </para>
/// <para>
/// <b>Bump <see cref="WireVersion"/> whenever a payload's encoding changes</b> - a field
/// added, removed, reordered, widened, or given a new meaning in
/// <c>MatchMessageCodec</c>. Adding a whole new message type that no older build ever
/// sends or expects does not need a bump; changing one that does, always does.
/// </para>
/// </remarks>
public static class NRProtocol
{
    /// <summary>
    /// The wire contract revision.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>1 - the original contract.</description></item>
    /// <item><description>
    /// 2 - the twenty-weapon arsenal. <c>ProjectileSpawned</c> gained a per-shot spec and
    /// <c>PowerUpCollected</c> changed from carrying a granted weapon to carrying the
    /// pickup itself. A version 1 peer decoding either reads a short buffer or a
    /// meaningless enum, so the two cannot interoperate at all.
    /// </description></item>
    /// <item><description>
    /// <b>3</b> - pickups became pooled generic bodies, so <c>PowerUpSpawned</c> gained
    /// the pickup type between the id and the position. A version 2 peer reads that type
    /// byte as the first half of the position and lands every pickup in the wrong place.
    /// </description></item>
    /// <item><description>
    /// <b>4</b> - the game modes were removed, leaving only Deathmatch, and the
    /// <c>GameMode</c> message (id 8) that published the host's choice went with them.
    /// This revision also adds an authoritative <c>RosterSnapshot</c> so every lobby
    /// reconciles to the host's complete player list instead of depending entirely on
    /// incremental rows. A version 3 peer expects a mode message and sizes its lobby for
    /// whichever mode it thinks is being played, so a mismatched pair would disagree
    /// about the roster.
    /// </description></item>
    /// <item><description>
    /// <b>5</b> - the PlayFab entity id was removed from <c>SubmitIdentity</c>, from every
    /// roster row and from <c>ShipSpawn</c> (XR-014). It is a durable account identifier in
    /// the publisher's service and nothing in the match ever read it: the transport keys
    /// players by peer id, and Party authenticates entities itself, so broadcasting it to
    /// every peer exposed account-scoped data the session had no use for. A version 4 peer
    /// reads the following field's bytes as the missing string's length.
    /// </description></item>
    /// </list>
    /// </remarks>
    public const int WireVersion = 5;

    /// <summary>
    /// The lobby search property the host advertises <see cref="VersionString"/> under.
    /// </summary>
    /// <remarks>
    /// PlayFab only indexes its reserved search keys, so this has to be one of them.
    /// <c>string_key1</c> is already taken by the join code.
    /// </remarks>
    public const string LobbyKey = "string_key3";

    /// <summary>
    /// The longest version token any transport will read off the wire.
    /// </summary>
    /// <remarks>
    /// Generous next to the two characters the current format uses, so a later token - a
    /// codec hash, say - needs no framing change, but bounded so a hostile peer cannot
    /// make a handshake allocate on demand.
    /// </remarks>
    public const int MaxLength = 32;

    /// <summary>
    /// The full version token, as published and compared.
    /// </summary>
    /// <remarks>
    /// Rendered as a dotted string rather than a bare number so a second component can be
    /// added later - a hash over the codec, say - without changing the shape of anything
    /// that stores or compares it.
    /// </remarks>
    public static string VersionString() => WireVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Whether a version token from somewhere else can play with this build.
    /// </summary>
    /// <remarks>
    /// Exact equality, and an empty token is never compatible. Empty is the important
    /// case: it is what a build from before this check existed looks like, because it
    /// publishes no version at all. Treating "absent" as "fine" would let through the
    /// exact mismatch this was written for, so absence is a mismatch.
    /// </remarks>
    public static bool IsCompatible(string? other)
        => !string.IsNullOrEmpty(other) && string.Equals(other, VersionString(), StringComparison.Ordinal);

    /// <summary>
    /// The player-facing explanation of a refused join.
    /// </summary>
    /// <remarks>
    /// Phrased from the joining player's side, so the same sentence works whether the
    /// client noticed the mismatch on the lobby or the host noticed it during the
    /// handshake. Three shapes rather than one, because the first thing anyone asks is
    /// which of the two builds is the stale one - and an absent version answers that
    /// precisely, since only a build older than this check publishes nothing.
    /// </remarks>
    public static string MismatchMessage(string? matchVersion, string? peerVersion)
    {
        const string Advice = "Both players need to be on the same build.";

        if (string.IsNullOrEmpty(matchVersion))
        {
            return $"That match is running an older version of the game (yours is {Describe(peerVersion)}). {Advice}";
        }

        if (string.IsNullOrEmpty(peerVersion))
        {
            return $"Your copy of the game is older than that match (the match is on {matchVersion}). {Advice}";
        }

        return $"That match is running a different version of the game (match {matchVersion}, you {peerVersion}). {Advice}";
    }

    private static string Describe(string? version)
        => string.IsNullOrEmpty(version) ? "an older build" : version;
}
