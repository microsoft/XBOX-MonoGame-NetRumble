using System.Buffers.Binary;
using System.Net;

namespace NetRumble.Platform.Lan;

/// <summary>
/// One peer-to-peer link, and everything the transport guarantees over it: fragmentation,
/// retransmission of reliable traffic, ordered reassembly, and the sequencing rule that
/// discards a stale snapshot instead of applying it.
/// </summary>
/// <remarks>
/// <para>
/// This is the part Godot's <c>MultiplayerAPI</c>, and PlayFab Party underneath it, gave
/// the original for free. <see cref="IPartyService"/> promises three delivery tiers, so a
/// transport that only sent datagrams would be quietly breaking the contract every other
/// implementation is written against: <c>PartyMatchNetwork</c> sends the roster, the score
/// and the match state as <see cref="MessageDelivery.Reliable"/> and would lose a player's
/// name to a single dropped packet.
/// </para>
/// <para>
/// The three channels are independent. A stalled reliable retransmit does not hold up
/// snapshots, which is the whole point of running input and world state on
/// <see cref="MessageDelivery.UnreliableSequenced"/>: gameplay keeps flowing while a
/// reliable message is still in flight.
/// </para>
/// <para>
/// Not a general-purpose reliability layer. There is no congestion control and no RTT
/// estimation - retransmission is a fixed interval - because this transport's target is a
/// LAN, where loss is rare and bandwidth is not the constraint. Over the open internet it
/// would need both.
/// </para>
/// </remarks>
internal sealed class LanLink(int peerId, IPEndPoint endPoint)
{
    /// <summary>Channel index per <see cref="MessageDelivery"/> tier.</summary>
    private const int ChannelCount = 3;

    /// <summary>
    /// The secret both ends of this link stamp on every link-scoped datagram, issued by
    /// the host at accept time. See <see cref="LanProtocol.SessionTokenSize"/> for what it
    /// does and does not buy.
    /// </summary>
    public ulong SessionToken { get; set; }

    /// <summary>How long an unacknowledged reliable fragment waits before being resent.</summary>
    private const long RetransmitIntervalMs = 120;

    /// <summary>
    /// How far ahead of the next expected sequence a reliable message may arrive and
    /// still be buffered. Caps the memory a peer can make the other end hold.
    /// </summary>
    private const uint MaxReliableReorderWindow = 256;

    /// <summary>
    /// How long a reliable message may go unacknowledged before the link is declared dead.
    /// </summary>
    private const long ReliableGiveUpMs = 10_000;

    /// <summary>How long a partial reassembly is kept before its fragments are abandoned.</summary>
    private const long ReassemblyTimeoutMs = 2_000;

    private readonly Dictionary<uint, PendingMessage> _pendingReliable = [];
    private readonly Dictionary<uint, Reassembly>[] _inbound =
        [[], [], []];

    private readonly Dictionary<uint, byte[]> _readyReliable = [];
    private readonly uint[] _nextSequence = [1, 1, 1];

    private uint _nextExpectedReliable = 1;
    private uint _lastSequencedDelivered;

    public int PeerId { get; set; } = peerId;

    public IPEndPoint EndPoint { get; } = endPoint;

    /// <summary>Timestamp of the last datagram of any kind received from this peer.</summary>
    public long LastHeardMs { get; set; }

    /// <summary>Timestamp of the last heartbeat sent to this peer.</summary>
    public long LastHeartbeatMs { get; set; }

    /// <summary>Set once the link has been told to stop, so a late datagram is ignored.</summary>
    public bool Closed { get; set; }

    /// <summary>Reliable messages still waiting for an acknowledgement.</summary>
    public int PendingReliableCount => _pendingReliable.Count;

    /// <summary>Maps a delivery tier onto its channel index.</summary>
    public static int ChannelOf(MessageDelivery delivery) => delivery switch
    {
        MessageDelivery.Reliable => 0,
        MessageDelivery.UnreliableSequenced => 1,
        _ => 2,
    };

    /// <summary>
    /// Splits an application message into datagrams and hands each to <paramref name="transmit"/>.
    /// Reliable fragments are retained for retransmission until acknowledged.
    /// </summary>
    public void Send(
        ReadOnlySpan<byte> payload,
        MessageDelivery delivery,
        long nowMs,
        Action<IPEndPoint, byte[], int> transmit)
    {
        if (Closed || payload.Length > LanProtocol.MaxMessageSize)
        {
            return;
        }

        var channel = ChannelOf(delivery);
        var sequence = _nextSequence[channel]++;

        var fragmentCount = Math.Max(1, (payload.Length + LanProtocol.MaxFragmentPayload - 1)
            / LanProtocol.MaxFragmentPayload);

        var fragments = new byte[fragmentCount][];

        for (var i = 0; i < fragmentCount; i++)
        {
            var offset = i * LanProtocol.MaxFragmentPayload;
            var length = Math.Min(LanProtocol.MaxFragmentPayload, payload.Length - offset);
            fragments[i] = BuildFragment(channel, sequence, i, fragmentCount, payload.Slice(offset, length), SessionToken);
            transmit(EndPoint, fragments[i], fragments[i].Length);
        }

        if (delivery is not MessageDelivery.Reliable)
        {
            return;
        }

        _pendingReliable[sequence] = new PendingMessage(fragments, nowMs);
    }

    /// <summary>Marks one reliable fragment acknowledged, dropping the message once all are.</summary>
    public void OnAck(uint sequence, int fragmentIndex)
    {
        if (!_pendingReliable.TryGetValue(sequence, out var pending))
        {
            return;
        }

        if (pending.Acknowledge(fragmentIndex))
        {
            _pendingReliable.Remove(sequence);
        }
    }

    /// <summary>
    /// Resends reliable fragments that have gone unacknowledged for too long.
    /// </summary>
    /// <returns>False when a message has been outstanding past the give-up window.</returns>
    public bool Retransmit(long nowMs, Action<IPEndPoint, byte[], int> transmit)
    {
        var alive = true;

        foreach (var pending in _pendingReliable.Values)
        {
            if (nowMs - pending.FirstSentMs > ReliableGiveUpMs)
            {
                alive = false;
                continue;
            }

            if (nowMs - pending.LastSentMs < RetransmitIntervalMs)
            {
                continue;
            }

            pending.LastSentMs = nowMs;

            for (var i = 0; i < pending.Fragments.Length; i++)
            {
                if (!pending.IsAcknowledged(i))
                {
                    transmit(EndPoint, pending.Fragments[i], pending.Fragments[i].Length);
                }
            }
        }

        return alive;
    }

    /// <summary>
    /// Accepts one inbound payload fragment. Completed messages are appended to
    /// <paramref name="delivered"/> in the order the application should see them.
    /// </summary>
    /// <param name="ackFragment">
    /// Set to the fragment index that must be acknowledged, or -1 when the channel is not
    /// reliable. The caller sends the acknowledgement; the link does not own a socket.
    /// </param>
    public void OnPayload(
        ReadOnlySpan<byte> body,
        long nowMs,
        List<byte[]> delivered,
        out uint ackSequence,
        out int ackFragment)
    {
        ackSequence = 0;
        ackFragment = -1;

        if (Closed || body.Length < LanProtocol.PayloadHeaderSize)
        {
            return;
        }

        int channel = body[0];
        var sequence = BinaryPrimitives.ReadUInt32LittleEndian(body[1..]);
        int fragmentIndex = BinaryPrimitives.ReadUInt16LittleEndian(body[5..]);
        int fragmentCount = BinaryPrimitives.ReadUInt16LittleEndian(body[7..]);
        int fragmentLength = BinaryPrimitives.ReadUInt16LittleEndian(body[9..]);

        if (channel >= ChannelCount
            || sequence == 0
            || fragmentCount == 0
            || fragmentIndex >= fragmentCount
            || fragmentLength > LanProtocol.MaxFragmentPayload
            || body.Length < LanProtocol.PayloadHeaderSize + fragmentLength
            || (long)fragmentCount * LanProtocol.MaxFragmentPayload > LanProtocol.MaxMessageSize)
        {
            return;
        }

        var fragment = body.Slice(LanProtocol.PayloadHeaderSize, fragmentLength);

        if (channel == 0)
        {
            // Acknowledge before the duplicate check: a retransmission means the original
            // acknowledgement was itself lost, and staying silent would loop forever.
            ackSequence = sequence;
            ackFragment = fragmentIndex;

            if (sequence < _nextExpectedReliable || _readyReliable.ContainsKey(sequence))
            {
                return;
            }

            // Bounded, because the sequence number comes off the wire. A peer that keeps
            // sending complete reliable messages with ever-increasing sequences would
            // otherwise grow _readyReliable for the life of the link, since nothing is
            // removed until the gap at _nextExpectedReliable is finally filled. A window
            // this wide is far beyond anything real reordering produces.
            if (sequence - _nextExpectedReliable >= MaxReliableReorderWindow)
            {
                return;
            }
        }
        else if (channel == 1 && sequence <= _lastSequencedDelivered)
        {
            // The point of the sequenced tier: a snapshot older than one already applied
            // is worse than no snapshot, so it is dropped rather than rolled back onto
            // the simulation.
            return;
        }

        var message = Accumulate(channel, sequence, fragmentIndex, fragmentCount, fragment, nowMs);

        if (message is null)
        {
            return;
        }

        switch (channel)
        {
            case 0:
                _readyReliable[sequence] = message;

                while (_readyReliable.Remove(_nextExpectedReliable, out var ordered))
                {
                    delivered.Add(ordered);
                    _nextExpectedReliable++;
                }

                break;

            case 1:
                _lastSequencedDelivered = sequence;
                delivered.Add(message);
                break;

            default:
                delivered.Add(message);
                break;
        }
    }

    /// <summary>Abandons partial reassemblies that will never complete.</summary>
    public void ExpireReassemblies(long nowMs)
    {
        foreach (var channel in _inbound)
        {
            if (channel.Count == 0)
            {
                continue;
            }

            foreach (var (sequence, reassembly) in channel)
            {
                if (nowMs - reassembly.FirstSeenMs > ReassemblyTimeoutMs)
                {
                    channel.Remove(sequence);
                }
            }
        }
    }

    private byte[]? Accumulate(
        int channel,
        uint sequence,
        int fragmentIndex,
        int fragmentCount,
        ReadOnlySpan<byte> fragment,
        long nowMs)
    {
        if (fragmentCount == 1)
        {
            return fragment.ToArray();
        }

        var pending = _inbound[channel];

        if (!pending.TryGetValue(sequence, out var reassembly))
        {
            reassembly = new Reassembly(fragmentCount, nowMs);
            pending[sequence] = reassembly;
        }
        else if (reassembly.Fragments.Length != fragmentCount)
        {
            // Two datagrams claiming the same sequence with different shapes. One of them
            // is corrupt or forged; neither can be trusted, so the whole message goes.
            pending.Remove(sequence);
            return null;
        }

        if (!reassembly.Add(fragmentIndex, fragment))
        {
            return null;
        }

        pending.Remove(sequence);
        return reassembly.Assemble();
    }

    private static byte[] BuildFragment(
        int channel,
        uint sequence,
        int fragmentIndex,
        int fragmentCount,
        ReadOnlySpan<byte> fragment,
        ulong sessionToken)
    {
        var datagram = new byte[LanProtocol.LinkPrefixSize + LanProtocol.PayloadHeaderSize + fragment.Length];
        var span = datagram.AsSpan();
        var offset = LanProtocol.WriteLinkPrefix(span, LanPacketKind.Payload, sessionToken);

        span[offset] = (byte)channel;
        BinaryPrimitives.WriteUInt32LittleEndian(span[(offset + 1)..], sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(offset + 5)..], (ushort)fragmentIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(offset + 7)..], (ushort)fragmentCount);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(offset + 9)..], (ushort)fragment.Length);
        fragment.CopyTo(span[(offset + LanProtocol.PayloadHeaderSize)..]);

        return datagram;
    }

    /// <summary>A reliable message held until every one of its fragments is acknowledged.</summary>
    private sealed class PendingMessage(byte[][] fragments, long sentMs)
    {
        private readonly bool[] _acknowledged = new bool[fragments.Length];
        private int _outstanding = fragments.Length;

        public byte[][] Fragments { get; } = fragments;

        public long FirstSentMs { get; } = sentMs;

        public long LastSentMs { get; set; } = sentMs;

        public bool IsAcknowledged(int index) => _acknowledged[index];

        /// <summary>Records an acknowledgement. Returns true once the whole message is acknowledged.</summary>
        public bool Acknowledge(int index)
        {
            if (index < 0 || index >= _acknowledged.Length || _acknowledged[index])
            {
                return _outstanding == 0;
            }

            _acknowledged[index] = true;
            _outstanding--;
            return _outstanding == 0;
        }
    }

    /// <summary>Fragments of one inbound message, held until they are all present.</summary>
    private sealed class Reassembly(int fragmentCount, long firstSeenMs)
    {
        private int _outstanding = fragmentCount;

        public byte[]?[] Fragments { get; } = new byte[]?[fragmentCount];

        public long FirstSeenMs { get; } = firstSeenMs;

        /// <summary>Stores a fragment. Returns true once the message is complete.</summary>
        public bool Add(int index, ReadOnlySpan<byte> fragment)
        {
            if (Fragments[index] is not null)
            {
                return false;
            }

            Fragments[index] = fragment.ToArray();
            _outstanding--;
            return _outstanding == 0;
        }

        public byte[] Assemble()
        {
            var total = 0;

            foreach (var fragment in Fragments)
            {
                total += fragment!.Length;
            }

            var message = new byte[total];
            var offset = 0;

            foreach (var fragment in Fragments)
            {
                fragment!.CopyTo(message, offset);
                offset += fragment.Length;
            }

            return message;
        }
    }
}
