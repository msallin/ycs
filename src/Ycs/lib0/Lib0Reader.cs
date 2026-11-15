// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System.Buffers.Binary;
using System.Text;

namespace Ycs;

/// <summary>
/// Reads the primitive encodings of <c>lib0/decoding.js</c> from a byte array (the counterpart
/// of lib0's <c>Decoder</c>). Sub-ranges are handed out as readers over the same array, so the
/// column streams of the v2 format are decoded without copying them first. A reader can be
/// pointed at other input with <see cref="Reset"/>, so decoders can be reused.
/// </summary>
internal sealed class Lib0Reader
{
    private byte[] _buffer = Array.Empty<byte>();
    private int _end;
    private int _pos;

    /// <summary>How deeply the values this reader reads may be nested (<see cref="UpdateLimits.MaxDepth"/>).</summary>
    public int MaxDepth { get; set; } = UpdateLimits.DefaultMaxDepth;

    /// <summary>A reader over no input; see <see cref="Reset"/>.</summary>
    public Lib0Reader()
    {
    }

    public Lib0Reader(byte[] buffer)
        : this(buffer, 0, buffer?.Length ?? 0)
    {
    }

    public Lib0Reader(byte[] buffer, int offset, int count)
    {
        Reset(buffer, offset, count);
    }

    /// <summary>Reads <paramref name="count"/> bytes of <paramref name="buffer"/> from <paramref name="offset"/> from now on.</summary>
    public void Reset(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, buffer.Length - offset);

        _buffer = buffer;
        _pos = offset;
        _end = offset + count;
    }

    /// <summary>Drops the reference to the input.</summary>
    public void Clear() => Reset(Array.Empty<byte>(), 0, 0);

    public bool HasContent => _pos < _end;

    public int Remaining => _end - _pos;

    public byte ReadByte()
    {
        if (_pos >= _end)
        {
            throw EndOfInput();
        }

        return _buffer[_pos++];
    }

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        if ((uint)count > (uint)(_end - _pos))
        {
            throw EndOfInput();
        }

        var span = _buffer.AsSpan(_pos, count);
        _pos += count;
        return span;
    }

    public ushort ReadUint16() => BinaryPrimitives.ReadUInt16LittleEndian(ReadBytes(2));

    public uint ReadUint32() => BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(4));

    public float ReadFloat32() => BinaryPrimitives.ReadSingleBigEndian(ReadBytes(4));

    public double ReadFloat64() => BinaryPrimitives.ReadDoubleBigEndian(ReadBytes(8));

    public long ReadBigInt64() => BinaryPrimitives.ReadInt64BigEndian(ReadBytes(8));

    /// <summary>
    /// Unsigned variable-length integer (lib0 <c>readVarUint</c>). Values that do not fit
    /// into 32 bits are rejected instead of being silently truncated: Ycs keeps clocks, lengths
    /// and counts in 32 bits; client ids are read with <see cref="ReadVarUint53"/>.
    /// </summary>
    public uint ReadVarUint()
    {
        ulong num = 0;
        var shift = 0;

        while (true)
        {
            var r = ReadByte();
            num |= (ulong)(r & 0x7F) << shift;
            shift += 7;

            if (r < 0x80)
            {
                if (num > uint.MaxValue)
                {
                    throw new YUpdateException("A variable-length integer is out of range.");
                }

                return (uint)num;
            }

            if (shift > 35)
            {
                throw new YUpdateException("A variable-length integer is out of range.");
            }
        }
    }

    /// <summary>
    /// Unsigned variable-length integer up to 2^53 - 1, the range of lib0's <c>readVarUint</c> (a
    /// JavaScript number): client ids, which applications and other implementations set freely.
    /// </summary>
    public long ReadVarUint53()
    {
        ulong num = 0;
        var shift = 0;

        while (true)
        {
            var r = ReadByte();
            num |= (ulong)(r & 0x7F) << shift;
            shift += 7;

            if (num > Limits.MaxClient)
            {
                throw new YUpdateException("A variable-length integer is out of range.");
            }

            if (r < 0x80)
            {
                return (long)num;
            }

            if (shift > 49)
            {
                throw new YUpdateException("A variable-length integer is out of range.");
            }
        }
    }

    /// <summary>
    /// Signed variable-length integer (lib0 <c>readVarInt</c>), up to 53 bits of magnitude like
    /// a JavaScript number. <c>Sign</c> is -1 when the sign bit is set, which distinguishes
    /// a negative zero.
    /// </summary>
    public (long Value, int Sign) ReadVarInt()
    {
        var r = ReadByte();
        ulong num = (ulong)(r & 0x3F);
        var shift = 6;
        var sign = (r & 0x40) > 0 ? -1 : 1;

        while ((r & 0x80) != 0)
        {
            r = ReadByte();
            num |= (ulong)(r & 0x7F) << shift;
            shift += 7;

            if (shift > 53 + 7 || num > (ulong)JsValue.MaxSafeInteger)
            {
                throw new YUpdateException("A variable-length integer is out of range.");
            }
        }

        return (sign * (long)num, sign);
    }

    private static YUpdateException EndOfInput() => new("The input ended early.");

    // lib0 decodes with a fatal TextDecoder (ignoreBOM: a byte order mark is kept as U+FEFF).
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>UTF-8 string prefixed by its byte length (lib0 <c>readVarString</c>); invalid UTF-8 is rejected, as lib0 does.</summary>
    public string ReadVarString()
    {
        var length = (int)ReadVarUint();
        if (length == 0)
        {
            return string.Empty;
        }

        var bytes = ReadBytes(length);
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new YUpdateException("A string is not valid UTF-8.");
        }
    }

    /// <summary>Length-prefixed byte array (lib0 <c>readVarUint8Array</c>), as a span over the input.</summary>
    public ReadOnlySpan<byte> ReadVarUint8ArraySpan() => ReadBytes((int)ReadVarUint());

    /// <summary>Length-prefixed byte array (lib0 <c>readVarUint8Array</c>), copied.</summary>
    public byte[] ReadVarUint8Array() => ReadVarUint8ArraySpan().ToArray();

    /// <summary>A reader over the next length-prefixed byte array, sharing this reader's buffer.</summary>
    public Lib0Reader ReadVarUint8ArrayAsReader()
    {
        var reader = new Lib0Reader();
        ReadVarUint8ArrayInto(reader);
        return reader;
    }

    /// <summary>Points <paramref name="target"/> at the next length-prefixed byte array of this reader's buffer.</summary>
    public void ReadVarUint8ArrayInto(Lib0Reader target)
    {
        var length = ReadVarUint();
        if (length > (uint)(_end - _pos))
        {
            throw EndOfInput();
        }

        target.Reset(_buffer, _pos, (int)length);
        _pos += (int)length;
    }
}
