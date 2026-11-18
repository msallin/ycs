using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Ycs;

/// <summary>
/// A byte buffer with the primitive encodings of <c>lib0/encoding.js</c>:
/// variable-length integers, length-prefixed strings and byte arrays, and the 'any' encoding.
/// It is the counterpart of lib0's <c>Encoder</c>: values are written straight into the buffer,
/// strings are UTF-8 encoded in place and nothing is allocated per value.
/// <br/>
/// The bytes go to one of three places. An owned array (the default), which grows by doubling and
/// which <see cref="Reset()"/> keeps for the next use. An array rented from <see cref="BufferPool{T}"/>
/// (<c>pooled</c>), which <see cref="Reset()"/> returns: whoever creates a pooled writer must reset it
/// on every path. Or the caller's <see cref="IBufferWriter{T}"/> (<see cref="StartWritingTo"/>), with
/// no buffer of its own: <see cref="Flush"/> commits what was written.
/// </summary>
internal sealed class Lib0Writer
{
    /// <summary>An owned buffer up to this size is kept by <see cref="Reset()"/>; a larger one is dropped.</summary>
    private const int MaxRetainedCapacity = 64 * 1024;

    private const int MinPooledCapacity = 256;

    // Up to this many chars the worst case is reserved without counting; the extra pass costs more than the slack.
    private const int MaxCountedWorstCase = 1024;
    private const int MinDestinationChunk = 256;

    private readonly bool _pooled;
    private readonly int _initialCapacity;
    private byte[] _buffer = Array.Empty<byte>();
    private int _length;

    // The most bytes any write since the last reset reached: Clear lowers _length, the bytes above it stay in the buffer.
    private int _dirty;

    // How much the pooled writer held when it was last reset, up to MaxRememberedLength: its first buffer is rented
    // that large, so a writer that encodes documents of about the same size again and again (a pooled encoder) does
    // not grow and copy from the initial capacity each time.
    private int _lastLength;

    // Above it the doubling's copies cost less than writing the bytes did; it bounds what a small encode after a
    // large one takes from the pool.
    private const int MaxRememberedLength = 1 << 20;

    private IBufferWriter<byte>? _destination;
    private Memory<byte> _chunk;
    private int _committed;

    /// <param name="initialCapacity">The first buffer's size; nothing is allocated before the first write.</param>
    /// <param name="pooled">Rent the buffer from the pool; <see cref="Reset()"/> must then be called on every path.</param>
    public Lib0Writer(int initialCapacity = 64, bool pooled = false)
    {
        _initialCapacity = Math.Max(initialCapacity, pooled ? MinPooledCapacity : 16);
        _pooled = pooled;
    }

    /// <summary>Number of bytes written since the last reset.</summary>
    public int Length => _committed + _length;

    /// <summary>The bytes written so far. Invalidated by the next write. Not available while writing to a destination.</summary>
    public ReadOnlySpan<byte> WrittenSpan
    {
        get
        {
            ThrowIfWritingToDestination();
            return _buffer.AsSpan(0, _length);
        }
    }

    public byte[] ToArray()
    {
        var written = WrittenSpan;
        if (written.IsEmpty)
        {
            return Array.Empty<byte>();
        }

        // Every byte is overwritten at once: no need to zero it first.
        var array = System.GC.AllocateUninitializedArray<byte>(written.Length);
        written.CopyTo(array);
        return array;
    }

    /// <summary>Forgets what was written and keeps the buffer.</summary>
    public void Clear()
    {
        ThrowIfWritingToDestination();
        _dirty = Math.Max(_dirty, _length);
        _length = 0;
    }

    /// <summary>
    /// Forgets what was written and stops writing to a destination (without committing what was not
    /// flushed). A pooled buffer is returned to the pool; an owned one is kept unless it is large.
    /// </summary>
    public void Reset()
    {
        if (_pooled && _buffer.Length > 0)
        {
            _lastLength = Math.Min(_length, MaxRememberedLength);
            BufferPool<byte>.Return(_buffer, Math.Max(_dirty, _length));
            _buffer = Array.Empty<byte>();
        }
        else if (_buffer.Length > MaxRetainedCapacity)
        {
            _buffer = Array.Empty<byte>();
        }

        _length = 0;
        _dirty = 0;
        _destination = null;
        _chunk = default;
        _committed = 0;
    }

    /// <summary>
    /// From now on, writes go straight into <paramref name="destination"/>; <see cref="Flush"/> commits
    /// them. Requires an empty writer; <see cref="Reset()"/> ends it.
    /// </summary>
    public void StartWritingTo(IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (Length != 0 || _destination != null)
        {
            throw new InvalidOperationException("The writer already holds data; reset it first.");
        }

        Reset();
        _destination = destination;
    }

    /// <summary>Commits what was written to the destination (see <see cref="StartWritingTo"/>).</summary>
    public void Flush()
    {
        if (_destination == null)
        {
            throw new InvalidOperationException("The writer is not writing to a destination.");
        }

        Commit();
        _chunk = default;
    }

    public void WriteByte(byte value)
    {
        GetSpan(1)[0] = value;
        _length++;
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(GetSpan(bytes.Length));
        _length += bytes.Length;
    }

    public void WriteFloat32(float num)
    {
        BinaryPrimitives.WriteSingleBigEndian(GetSpan(4), num);
        _length += 4;
    }

    public void WriteFloat64(double num)
    {
        BinaryPrimitives.WriteDoubleBigEndian(GetSpan(8), num);
        _length += 8;
    }

    public void WriteBigInt64(long num)
    {
        BinaryPrimitives.WriteInt64BigEndian(GetSpan(8), num);
        _length += 8;
    }

    /// <summary>
    /// Unsigned variable-length integer: 7 bits per byte, least significant group first,
    /// the high bit set on every byte but the last (lib0 <c>writeVarUint</c>).
    /// </summary>
    public void WriteVarUint(ulong num)
    {
        // Not '_length += ...': GetSpan can commit a chunk and reset _length, which '+=' would have read before.
        var written = WriteVarUint(GetSpan(10), num);
        _length += written;
    }

    /// <summary>
    /// Signed variable-length integer (lib0 <c>writeVarInt</c>): the first byte holds a
    /// continuation bit, a sign bit and 6 bits of the magnitude; the following bytes are as
    /// in <see cref="WriteVarUint(ulong)"/>. <paramref name="negativeZero"/> writes zero with
    /// the sign bit set, which the run-length encoders use as a marker.
    /// </summary>
    public void WriteVarInt(long num, bool negativeZero = false)
    {
        var span = GetSpan(11);
        var isNegative = num == 0 ? negativeZero : num < 0;
        var magnitude = num < 0 ? (ulong)(-num) : (ulong)num;

        //                  |  whether to continue reading  |      is negative      | value.
        span[0] = (byte)((magnitude > 0x3F ? 0x80 : 0) | (isNegative ? 0x40 : 0) | (int)(magnitude & 0x3F));
        magnitude >>= 6;
        var i = 1;

        // We don't need to consider the case of num == 0 so we can use a different pattern here than above.
        while (magnitude > 0)
        {
            span[i++] = (byte)((magnitude > 0x7F ? 0x80 : 0) | (int)(magnitude & 0x7F));
            magnitude >>= 7;
        }

        _length += i;
    }

    /// <summary>UTF-8 string prefixed by its byte length (lib0 <c>writeVarString</c>).</summary>
    public void WriteVarString(string str) => WriteVarString(str.AsSpan());

    public void WriteVarString(ReadOnlySpan<char> str)
    {
        var byteCount = Encoding.UTF8.GetByteCount(str);
        WriteVarUint((uint)byteCount);
        Encoding.UTF8.GetBytes(str, GetSpan(byteCount));
        _length += byteCount;
    }

    /// <summary>
    /// Appends the UTF-8 encoding of <paramref name="chars"/> through a stateful encoder: a surrogate
    /// pair split across two calls is encoded as if the strings had been concatenated first.
    /// </summary>
    public void WriteUtf8(Encoder encoder, ReadOnlySpan<char> chars, bool flush)
    {
        // The worst case is three bytes per char: for a large string that would make the buffer jump to three times
        // what it needs, so its exact size (the encoder's pending surrogate included) is counted instead.
        var span = GetSpan(chars.Length > MaxCountedWorstCase ? encoder.GetByteCount(chars, flush) : Encoding.UTF8.GetMaxByteCount(chars.Length));
        encoder.Convert(chars, span, flush, out _, out var bytesUsed, out _);
        _length += bytesUsed;
    }

    /// <summary>Byte array prefixed by its length (lib0 <c>writeVarUint8Array</c>).</summary>
    public void WriteVarUint8Array(ReadOnlySpan<byte> data)
    {
        WriteVarUint((uint)data.Length);
        WriteBytes(data);
    }

    /// <summary>Copies the written bytes to <paramref name="destination"/>.</summary>
    public void CopyTo(IBufferWriter<byte> destination) => destination.Write(WrittenSpan);

    /// <summary>The size of <paramref name="num"/> as a var-uint.</summary>
    public static int VarUintSize(ulong num)
    {
        var size = 1;
        while (num > 0x7F)
        {
            num >>= 7;
            size++;
        }

        return size;
    }

    /// <summary>Writes a <see cref="WriteVarUint(ulong)"/> into any buffer writer.</summary>
    public static void WriteVarUint(IBufferWriter<byte> destination, ulong num)
    {
        destination.Advance(WriteVarUint(destination.GetSpan(10), num));
    }

    public static int WriteVarUint(Span<byte> span, ulong num)
    {
        var i = 0;
        while (num > 0x7F)
        {
            span[i++] = (byte)(0x80 | (num & 0x7F));
            num >>= 7;
        }

        span[i++] = (byte)num;
        return i;
    }

    private Span<byte> GetSpan(int sizeHint)
    {
        if (_destination != null)
        {
            return GetDestinationSpan(sizeHint);
        }

        if (_buffer.Length - _length < sizeHint)
        {
            Grow(sizeHint);
        }

        return _buffer.AsSpan(_length);
    }

    private Span<byte> GetDestinationSpan(int sizeHint)
    {
        if (_chunk.Length - _length < sizeHint)
        {
            Commit();
            _chunk = _destination!.GetMemory(Math.Max(sizeHint, MinDestinationChunk));
        }

        return _chunk.Span.Slice(_length);
    }

    private void Commit()
    {
        if (_length > 0)
        {
            _destination!.Advance(_length);
            _committed = checked(_committed + _length);
            _length = 0;
        }
    }

    private void Grow(int sizeHint)
    {
        var newSize = Math.Max(Math.Max(checked(_buffer.Length * 2), checked(_length + sizeHint)), _initialCapacity);
        if (_pooled)
        {
            if (_buffer.Length == 0)
            {
                newSize = Math.Max(newSize, _lastLength);
            }

            var newBuffer = BufferPool<byte>.Rent(newSize);
            _buffer.AsSpan(0, _length).CopyTo(newBuffer);
            if (_buffer.Length > 0)
            {
                BufferPool<byte>.Return(_buffer, Math.Max(_dirty, _length));
            }

            _dirty = 0;
            _buffer = newBuffer;
        }
        else
        {
            Array.Resize(ref _buffer, newSize);
        }
    }

    private void ThrowIfWritingToDestination()
    {
        if (_destination != null)
        {
            throw new InvalidOperationException("The writer writes to a destination; its bytes are not available here.");
        }
    }
}
