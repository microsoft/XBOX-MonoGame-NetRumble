using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace NetRumble.Core.Net.Wire;

/// <summary>
/// Builds an outgoing packet into a pooled buffer.
/// </summary>
/// <remarks>
/// <para>
/// Little-endian throughout, written through <see cref="BinaryPrimitives"/> rather than
/// relying on the host's byte order. Party carries bytes between machines that a future
/// console or ARM build may not share an endianness with, and getting this wrong produces
/// plausible-looking garbage rather than an error.
/// </para>
/// <para>
/// Rented from <see cref="ArrayPool{T}"/> because the snapshot path builds one of these
/// 30 times a second for the whole match; allocating a fresh array each time would hand
/// the GC a steady stream of large short-lived buffers.
/// </para>
/// <para>
/// Disposal returns the buffer to the pool. Use it with <c>using</c>; a leak is not
/// fatal, it just degrades to ordinary allocation.
/// </para>
/// </remarks>
public struct MessageWriter : IDisposable
{
    private byte[] _buffer;
    private int _length;

    public MessageWriter(MessageType type, int initialCapacity = 256)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(initialCapacity, 16));
        _length = 0;
        WriteByte((byte)type);
    }

    /// <summary>The bytes written so far.</summary>
    public readonly ReadOnlySpan<byte> Written => _buffer.AsSpan(0, _length);

    public void WriteByte(byte value)
    {
        Ensure(1);
        _buffer[_length++] = value;
    }

    public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    public void WriteInt(int value)
    {
        Ensure(4);
        BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_length), value);
        _length += 4;
    }

    public void WriteFloat(float value)
    {
        Ensure(4);
        BinaryPrimitives.WriteSingleLittleEndian(_buffer.AsSpan(_length), value);
        _length += 4;
    }

    public void WriteVector2(Vector2 value)
    {
        WriteFloat(value.X);
        WriteFloat(value.Y);
    }

    /// <summary>
    /// Writes a UTF-8 string with a 16-bit length prefix.
    /// </summary>
    /// <remarks>
    /// Only display names and PlayFab entity ids travel as strings, and both are bounded
    /// well under 64 KB, so a 2-byte prefix is enough and keeps the roster packet small.
    /// </remarks>
    public void WriteString(string? value)
    {
        var text = value ?? string.Empty;
        var byteCount = Encoding.UTF8.GetByteCount(text);

        if (byteCount > ushort.MaxValue)
        {
            throw new ArgumentException($"String is {byteCount} bytes, over the 65535 limit.", nameof(value));
        }

        Ensure(2 + byteCount);
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_length), (ushort)byteCount);
        _length += 2;
        Encoding.UTF8.GetBytes(text, _buffer.AsSpan(_length));
        _length += byteCount;
    }

    /// <summary>
    /// Writes a collection count as a 16-bit value.
    /// </summary>
    /// <remarks>
    /// Deliberately capped. The largest collection on the wire is a snapshot of 16 ships
    /// plus 15 asteroids, so anything approaching 65535 is a corrupt or hostile packet and
    /// should fail here rather than make the reader try to allocate for it.
    /// </remarks>
    public void WriteCount(int count)
    {
        if (count is < 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Count does not fit in 16 bits.");
        }

        Ensure(2);
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_length), (ushort)count);
        _length += 2;
    }

    private void Ensure(int extra)
    {
        if (_length + extra <= _buffer.Length)
        {
            return;
        }

        var grown = ArrayPool<byte>.Shared.Rent(Math.Max(_buffer.Length * 2, _length + extra));
        _buffer.AsSpan(0, _length).CopyTo(grown);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = grown;
    }

    public void Dispose()
    {
        if (_buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = [];
            _length = 0;
        }
    }
}
