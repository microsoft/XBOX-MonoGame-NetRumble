using System.Buffers.Binary;
using System.Text;

namespace NetRumble.Platform.Lan;

/// <summary>
/// Datagram kinds carried by the LAN transport.
/// </summary>
/// <remarks>
/// Deliberately separate from <c>NetRumble.Core.Net.Wire.MessageType</c>. That enum is
/// the <i>match</i> protocol and belongs to the layer above; this one is the transport
/// envelope that carries it. Keeping them apart is what lets the transport fragment,
/// retransmit and de-duplicate without ever parsing a match message - and lets the match
/// protocol change without a transport revision.
/// </remarks>
internal enum LanPacketKind : byte
{
    /// <summary>Client to host: request to join. Retried until accepted or timed out.</summary>
    Connect = 1,

    /// <summary>Host to client: accepted, with the peer id the host assigned.</summary>
    ConnectAccept = 2,

    /// <summary>Host to client: refused, with a reason to show the player.</summary>
    ConnectReject = 3,

    /// <summary>Either direction: leaving cleanly, so the peer need not wait for a timeout.</summary>
    Disconnect = 4,

    /// <summary>One fragment of one application message.</summary>
    Payload = 5,

    /// <summary>Acknowledgement of one reliable fragment.</summary>
    Ack = 6,

    /// <summary>Keeps a link alive and drives timeout detection.</summary>
    Heartbeat = 7,

    /// <summary>Broadcast: "who is hosting under this join code?"</summary>
    DiscoverQuery = 8,

    /// <summary>Unicast answer to a <see cref="DiscoverQuery"/>.</summary>
    DiscoverReply = 9,

    /// <summary>A text chat line. Carried out of band so it is never a match message.</summary>
    ChatText = 10,
}

/// <summary>
/// Framing constants and the read/write helpers for the LAN transport envelope.
/// </summary>
/// <remarks>
/// <para>
/// Every datagram starts with the same six bytes - a magic number and a protocol version
/// - so a stray packet from anything else sharing the discovery port is discarded before
/// it can be parsed as a length or a count. The version is checked too: this transport is
/// only ever spoken between two builds of this game, and a mismatched build should fail
/// to connect rather than half-work.
/// </para>
/// <para>
/// Little-endian throughout, through <see cref="BinaryPrimitives"/>, for the same reason
/// <c>MessageWriter</c> is: the two peers are not guaranteed to share a byte order, and
/// getting it wrong produces plausible garbage rather than an error.
/// </para>
/// </remarks>
internal static class LanProtocol
{
    /// <summary>"NRLN" - NetRumble LAN.</summary>
    internal const uint Magic = 0x4E4C524EU;

    internal const byte Version = 2;

    /// <summary>
    /// Bytes of per-link session token carried by every link-scoped datagram.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before this existed, a LAN peer was identified <b>solely</b> by the source address
    /// and port of the datagram. UDP is trivially spoofable and this transport is
    /// fire-and-forget, so an attacker needed no return traffic: forging the host's
    /// endpoint injected fully authoritative match messages into a client, and forging a
    /// client's endpoint let them send input, ready-up or disconnect as that player. The
    /// only thing the attacker needed to know was two public constants.
    /// </para>
    /// <para>
    /// The token is issued by the host in <see cref="LanPacketKind.ConnectAccept"/>, is
    /// unique per link, and must appear in every subsequent link-scoped datagram. It is
    /// not encryption and does not stop an on-path observer who can read it; it raises the
    /// bar from "know two constants" to "read this link's traffic", which is the
    /// difference between blind off-path injection and a real man in the middle.
    /// </para>
    /// <para>
    /// This is still the dev-tier transport documented in docs/known-gaps.md as not
    /// internet-safe, and this does not make it so.
    /// </para>
    /// </remarks>
    internal const int SessionTokenSize = 8;

    /// <summary>
    /// Largest datagram this transport will put on the wire.
    /// </summary>
    /// <remarks>
    /// Chosen to sit under the 1500-byte Ethernet MTU with room for the IPv4 (20) and UDP
    /// (8) headers plus any tunnelling overhead a VPN or a Hyper-V switch adds. Anything
    /// larger risks IP fragmentation, where a single lost fragment silently destroys the
    /// whole datagram - which is precisely the failure this transport's own fragmentation
    /// exists to make visible and recoverable.
    /// </remarks>
    internal const int MaxDatagram = 1200;

    /// <summary>magic (4) + version (1) + kind (1).</summary>
    internal const int PrefixSize = 6;

    /// <summary>
    /// The prefix of a link-scoped datagram: the shared prefix plus the session token.
    /// </summary>
    internal const int LinkPrefixSize = PrefixSize + SessionTokenSize;

    /// <summary>
    /// Whether this kind belongs to an established link and therefore carries a session
    /// token. The handshake and discovery kinds cannot, because no link exists yet.
    /// </summary>
    internal static bool IsLinkScoped(LanPacketKind kind) => kind switch
    {
        LanPacketKind.Disconnect
            or LanPacketKind.Payload
            or LanPacketKind.Ack
            or LanPacketKind.Heartbeat
            or LanPacketKind.ChatText => true,
        _ => false,
    };

    /// <summary>Whether this kind is part of host discovery.</summary>
    internal static bool IsDiscovery(LanPacketKind kind)
        => kind is LanPacketKind.DiscoverQuery or LanPacketKind.DiscoverReply;

    /// <summary>A fresh, cryptographically random session token.</summary>
    internal static ulong NewSessionToken()
        => BinaryPrimitives.ReadUInt64LittleEndian(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));

    /// <summary>channel (1) + sequence (4) + fragment index (2) + fragment count (2) + fragment length (2).</summary>
    internal const int PayloadHeaderSize = 11;

    /// <summary>Application bytes that fit in one datagram.</summary>
    internal const int MaxFragmentPayload = MaxDatagram - LinkPrefixSize - PayloadHeaderSize;

    /// <summary>
    /// Hard ceiling on a reassembled message, and therefore on the fragment count.
    /// </summary>
    /// <remarks>
    /// A hostile or corrupt packet can claim any fragment count that fits in 16 bits.
    /// Without this the receiver would allocate for 65535 fragments - about 77 MB - on
    /// the strength of two bytes from the network. The largest real message is a
    /// <c>MatchCreated</c> for a full lobby, comfortably under 8 KB.
    /// </remarks>
    internal const int MaxMessageSize = 64 * 1024;

    /// <summary>UDP port hosts listen on for <see cref="LanPacketKind.DiscoverQuery"/>.</summary>
    internal const int DiscoveryPort = 27500;

    /// <summary>Characters a join code is drawn from.</summary>
    /// <remarks>
    /// No I, O, 0, 1, S or 5: a join code gets read aloud or typed from a screenshot, and
    /// those are the pairs people get wrong. The alphabet is 30 characters, so a
    /// five-character code is about 24.3 million combinations - ample for a LAN.
    /// </remarks>
    internal const string JoinCodeAlphabet = "ABCDEFGHJKLMNPQRTUVWXYZ2346789";

    internal const int JoinCodeLength = 5;

    // --- Writing ------------------------------------------------------------

    /// <summary>Writes the shared prefix and returns the number of bytes written.</summary>
    internal static int WritePrefix(Span<byte> destination, LanPacketKind kind)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, Magic);
        destination[4] = Version;
        destination[5] = (byte)kind;
        return PrefixSize;
    }

    /// <summary>
    /// Writes the shared prefix plus a session token, for a link-scoped datagram.
    /// </summary>
    internal static int WriteLinkPrefix(Span<byte> destination, LanPacketKind kind, ulong sessionToken)
    {
        var offset = WritePrefix(destination, kind);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[offset..], sessionToken);
        return offset + SessionTokenSize;
    }

    /// <summary>Writes a UTF-8 string with a 16-bit length prefix.</summary>
    internal static int WriteString(Span<byte> destination, string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        BinaryPrimitives.WriteUInt16LittleEndian(destination, (ushort)byteCount);
        Encoding.UTF8.GetBytes(value, destination[2..]);
        return 2 + byteCount;
    }

    // --- Reading ------------------------------------------------------------

    /// <summary>
    /// Validates the magic and version and returns the kind and the remaining body.
    /// </summary>
    internal static bool TryReadPrefix(
        ReadOnlySpan<byte> datagram,
        out LanPacketKind kind,
        out ReadOnlySpan<byte> body)
    {
        kind = default;
        body = default;

        if (datagram.Length < PrefixSize)
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(datagram) != Magic || datagram[4] != Version)
        {
            return false;
        }

        kind = (LanPacketKind)datagram[5];
        body = datagram[PrefixSize..];
        return true;
    }

    /// <summary>
    /// Reads and strips the session token from the front of a link-scoped body.
    /// </summary>
    internal static bool TryReadSessionToken(ref ReadOnlySpan<byte> body, out ulong sessionToken)
    {
        sessionToken = 0;

        if (body.Length < SessionTokenSize)
        {
            return false;
        }

        sessionToken = BinaryPrimitives.ReadUInt64LittleEndian(body);
        body = body[SessionTokenSize..];
        return true;
    }

    /// <summary>Reads a 16-bit-prefixed UTF-8 string and advances <paramref name="body"/> past it.</summary>
    internal static bool TryReadString(ref ReadOnlySpan<byte> body, int maxLength, out string value)
    {
        value = string.Empty;

        if (body.Length < 2)
        {
            return false;
        }

        var byteCount = BinaryPrimitives.ReadUInt16LittleEndian(body);

        if (byteCount > maxLength || body.Length < 2 + byteCount)
        {
            return false;
        }

        value = Encoding.UTF8.GetString(body.Slice(2, byteCount));
        body = body[(2 + byteCount)..];
        return true;
    }

    /// <summary>True when the text is a well-formed join code, ignoring case.</summary>
    internal static bool IsJoinCode(string? code)
    {
        if (code is null || code.Length != JoinCodeLength)
        {
            return false;
        }

        foreach (var c in code)
        {
            if (!JoinCodeAlphabet.Contains(char.ToUpperInvariant(c)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Generates a random join code from <see cref="JoinCodeAlphabet"/>.</summary>
    internal static string NewJoinCode()
    {
        return string.Create(JoinCodeLength, 0, static (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = JoinCodeAlphabet[Random.Shared.Next(JoinCodeAlphabet.Length)];
            }
        });
    }
}
