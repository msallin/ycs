// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Ycs;

/// <summary>
/// A growable byte buffer with the primitive encodings of <c>lib0/encoding.js</c>:
/// variable-length integers, length-prefixed strings and byte arrays, and the 'any' encoding.
/// It is the counterpart of lib0's <c>Encoder</c> and replaces the <see cref="MemoryStream"/>
/// extension methods the encoders used before: values are written straight into the buffer,
/// strings are UTF-8 encoded in place and nothing is allocated per value.
/// </summary>
internal sealed class Lib0Writer
{
    private byte[] _buffer;
    private int _length;

    public Lib0Writer(int initialCapacity = 64)
    {
        _buffer = new byte[Math.Max(initialCapacity, 16)];
    }

    /// <summary>Number of bytes written so far.</summary>
    public int Length => _length;

    /// <summary>The bytes written so far. Invalidated by the next write.</summary>
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _length);

    public byte[] ToArray() => WrittenSpan.ToArray();

    public void Clear() => _length = 0;

    public void WriteByte(byte value)
    {
        if (_length == _buffer.Length)
        {
            Grow(1);
        }

        _buffer[_length++] = value;
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(GetSpan(bytes.Length));
        _length += bytes.Length;
    }

    public void WriteUint16(ushort num)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(GetSpan(2), num);
        _length += 2;
    }

    public void WriteUint32(uint num)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(GetSpan(4), num);
        _length += 4;
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
        _length += WriteVarUint(GetSpan(10), num);
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

    /// <summary>Byte array prefixed by its length (lib0 <c>writeVarUint8Array</c>).</summary>
    public void WriteVarUint8Array(ReadOnlySpan<byte> data)
    {
        WriteVarUint((uint)data.Length);
        WriteBytes(data);
    }

    /// <summary>Copies the written bytes to <paramref name="destination"/>.</summary>
    public void CopyTo(IBufferWriter<byte> destination) => destination.Write(WrittenSpan);

    /// <summary>Writes a <see cref="WriteVarUint(ulong)"/> into any buffer writer.</summary>
    public static void WriteVarUint(IBufferWriter<byte> destination, ulong num)
    {
        destination.Advance(WriteVarUint(destination.GetSpan(10), num));
    }

    private static int WriteVarUint(Span<byte> span, ulong num)
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
        if (_buffer.Length - _length < sizeHint)
        {
            Grow(sizeHint);
        }

        return _buffer.AsSpan(_length);
    }

    private void Grow(int sizeHint)
    {
        var newSize = Math.Max(checked(_buffer.Length * 2), checked(_length + sizeHint));
        Array.Resize(ref _buffer, newSize);
    }
}
