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
/// column streams of the v2 format are decoded without copying them first.
/// </summary>
internal sealed class Lib0Reader
{
    private readonly byte[] _buffer;
    private readonly int _end;
    private int _pos;

    public Lib0Reader(byte[] buffer)
        : this(buffer, 0, buffer.Length)
    {
    }

    public Lib0Reader(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, buffer.Length - offset);

        _buffer = buffer;
        _pos = offset;
        _end = offset + count;
    }

    public bool HasContent => _pos < _end;

    public int Remaining => _end - _pos;

    public byte ReadByte()
    {
        if (_pos >= _end)
        {
            throw new EndOfStreamException();
        }

        return _buffer[_pos++];
    }

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        if ((uint)count > (uint)(_end - _pos))
        {
            throw new EndOfStreamException();
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
    /// into 32 bits are rejected instead of being silently truncated: Ycs keeps client ids,
    /// clocks and lengths in 32 bits, as Yjs does in practice.
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
                    throw new InvalidDataException("Integer out of range.");
                }

                return (uint)num;
            }

            if (shift > 35)
            {
                throw new InvalidDataException("Integer out of range.");
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

            if (shift > 53 + 7 || num > (1UL << 53) - 1)
            {
                throw new InvalidDataException("Integer out of range.");
            }
        }

        return (sign * (long)num, sign);
    }

    /// <summary>UTF-8 string prefixed by its byte length (lib0 <c>readVarString</c>).</summary>
    public string ReadVarString()
    {
        var length = (int)ReadVarUint();
        return length == 0 ? string.Empty : Encoding.UTF8.GetString(ReadBytes(length));
    }

    /// <summary>Length-prefixed byte array (lib0 <c>readVarUint8Array</c>), as a span over the input.</summary>
    public ReadOnlySpan<byte> ReadVarUint8ArraySpan() => ReadBytes((int)ReadVarUint());

    /// <summary>Length-prefixed byte array (lib0 <c>readVarUint8Array</c>), copied.</summary>
    public byte[] ReadVarUint8Array() => ReadVarUint8ArraySpan().ToArray();

    /// <summary>A reader over the next length-prefixed byte array, sharing this reader's buffer.</summary>
    public Lib0Reader ReadVarUint8ArrayAsReader()
    {
        var length = (int)ReadVarUint();
        if ((uint)length > (uint)(_end - _pos))
        {
            throw new EndOfStreamException();
        }

        var reader = new Lib0Reader(_buffer, _pos, length);
        _pos += length;
        return reader;
    }
}
