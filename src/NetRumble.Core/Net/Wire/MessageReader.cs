using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace NetRumble.Core.Net.Wire;

/// <summary>
/// Reads an incoming packet.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every read is bounds-checked and returns a success flag rather than throwing.</b>
/// Packets arrive from other machines, and a modified or simply out-of-date client can
/// send a truncated or nonsense payload. The host must drop such a packet, not take an
/// exception on its simulation thread, so decoding is written as a chain of
/// <c>if (!reader.TryRead...) return false;</c> rather than exception-driven parsing.
/// </para>
/// <para>
/// A <c>ref struct</c> over the caller's span: the payload handed to
/// <see cref="IPartyService.MessageReceived"/> is only valid for the duration of the
/// callback, and this makes it a compile error to stash it somewhere that outlives it.
/// </para>
/// </remarks>
public ref struct MessageReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _position = 0;

    /// <summary>Bytes not yet consumed.</summary>
    public readonly int Remaining => _data.Length - _position;

    /// <summary>
    /// True when the packet was consumed exactly.
    /// </summary>
    /// <remarks>
    /// Checked after decoding so trailing bytes are treated as a malformed packet. A
    /// payload that decodes but has leftovers usually means the two peers disagree about
    /// the message layout, which is worth rejecting loudly rather than half-applying.
    /// </remarks>
    public readonly bool IsFullyConsumed => _position == _data.Length;

    public bool TryReadByte(out byte value)
    {
        if (Remaining < 1)
        {
            value = 0;
            return false;
        }

        value = _data[_position++];
        return true;
    }

    public bool TryReadMessageType(out MessageType value)
    {
        var ok = TryReadByte(out var raw);
        value = ok ? (MessageType)raw : MessageType.None;
        return ok;
    }

    public bool TryReadBool(out bool value)
    {
        var ok = TryReadByte(out var raw);
        value = ok && raw != 0;
        return ok;
    }

    public bool TryReadInt(out int value)
    {
        if (Remaining < 4)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadInt32LittleEndian(_data[_position..]);
        _position += 4;
        return true;
    }

    public bool TryReadFloat(out float value)
    {
        if (Remaining < 4)
        {
            value = 0.0f;
            return false;
        }

        value = BinaryPrimitives.ReadSingleLittleEndian(_data[_position..]);
        _position += 4;

        // A NaN or infinity reaching the simulation poisons every position it touches and
        // never recovers: one bad velocity spreads through collisions to the whole world.
        // Rejecting the packet is far cheaper than diagnosing that later.
        return float.IsFinite(value);
    }

    public bool TryReadVector2(out Vector2 value)
    {
        if (!TryReadFloat(out var x) || !TryReadFloat(out var y))
        {
            value = Vector2.Zero;
            return false;
        }

        value = new Vector2(x, y);
        return true;
    }

    public bool TryReadString(out string value)
    {
        value = string.Empty;

        if (Remaining < 2)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(_data[_position..]);
        _position += 2;

        if (Remaining < length)
        {
            return false;
        }

        value = Encoding.UTF8.GetString(_data.Slice(_position, length));
        _position += length;
        return true;
    }

    /// <summary>
    /// Reads a collection count and rejects it if the packet is too short to possibly
    /// contain that many elements.
    /// </summary>
    /// <remarks>
    /// The <paramref name="minimumBytesPerItem"/> check is the defence against a hostile
    /// packet that declares 65535 entries in eight bytes: without it the decoder would
    /// allocate a list for all of them before discovering the payload is empty.
    /// </remarks>
    public bool TryReadCount(int minimumBytesPerItem, out int count)
    {
        count = 0;

        if (Remaining < 2)
        {
            return false;
        }

        count = BinaryPrimitives.ReadUInt16LittleEndian(_data[_position..]);
        _position += 2;

        return (long)count * minimumBytesPerItem <= Remaining;
    }
}
