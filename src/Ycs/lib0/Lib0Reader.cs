using System.Buffers;
using System.Diagnostics;
using System.Buffers.Binary;
using System.Text;
using System.Text.Unicode;

namespace Ycs;

/// <summary>
/// Reads the primitive encodings of <c>lib0/decoding.js</c> (the counterpart of lib0's <c>Decoder</c>) from a range of
/// an input the caller passes to every read: the reader holds offsets only, never the input. Sub-ranges are handed out
/// as readers over the same input, so the column streams of the v2 format are decoded without copying them first.
/// A mutable struct: keep it in a local or a non-readonly field, and always read one range of one input with it.
/// </summary>
internal struct Lib0Reader
{
    private int _pos;
    private int _end;
    private int _stringStart;
    private int _stringLength;

    /// <summary>How deeply the values this reader reads may be nested (<see cref="UpdateLimits.MaxDepth"/>).</summary>
    public readonly int MaxDepth;

    /// <summary>A reader of the bytes from <paramref name="start"/> up to <paramref name="end"/> of the input every read is given.</summary>
    public Lib0Reader(int start, int end, int maxDepth = UpdateLimits.DefaultMaxDepth)
    {
        _pos = start;
        _end = end;
        _stringStart = 0;
        _stringLength = 0;
        MaxDepth = maxDepth;
    }

    public readonly bool HasContent => _pos < _end;

    public readonly int Remaining => _end - _pos;

    public byte ReadByte(ReadOnlySpan<byte> input)
    {
        Debug.Assert(_end <= input.Length, "The reader was handed another input than the one it was made for.");
        if (_pos >= _end)
        {
            throw EndOfInput();
        }

        return input[_pos++];
    }

    public ReadOnlySpan<byte> ReadBytes(ReadOnlySpan<byte> input, int count)
    {
        Debug.Assert(_end <= input.Length, "The reader was handed another input than the one it was made for.");
        if ((uint)count > (uint)(_end - _pos))
        {
            throw EndOfInput();
        }

        var span = input.Slice(_pos, count);
        _pos += count;
        return span;
    }

    public float ReadFloat32(ReadOnlySpan<byte> input) => BinaryPrimitives.ReadSingleBigEndian(ReadBytes(input, 4));

    public double ReadFloat64(ReadOnlySpan<byte> input) => BinaryPrimitives.ReadDoubleBigEndian(ReadBytes(input, 8));

    public long ReadBigInt64(ReadOnlySpan<byte> input) => BinaryPrimitives.ReadInt64BigEndian(ReadBytes(input, 8));

    /// <summary>
    /// Unsigned variable-length integer (lib0 <c>readVarUint</c>). Values that do not fit
    /// into 32 bits are rejected instead of being silently truncated: Ycs keeps clocks, lengths
    /// and counts in 32 bits; client ids are read with <see cref="ReadVarUint53"/>.
    /// </summary>
    public uint ReadVarUint(ReadOnlySpan<byte> input)
    {
        ulong num = 0;
        var shift = 0;

        while (true)
        {
            var r = ReadByte(input);
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
    public long ReadVarUint53(ReadOnlySpan<byte> input)
    {
        ulong num = 0;
        var shift = 0;

        while (true)
        {
            var r = ReadByte(input);
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
    public (long Value, int Sign) ReadVarInt(ReadOnlySpan<byte> input)
    {
        var r = ReadByte(input);
        ulong num = (ulong)(r & 0x3F);
        var shift = 6;
        var sign = (r & 0x40) > 0 ? -1 : 1;

        while ((r & 0x80) != 0)
        {
            r = ReadByte(input);
            num |= (ulong)(r & 0x7F) << shift;
            shift += 7;

            if (shift > 53 + 7 || num > (ulong)JsValue.MaxSafeInteger)
            {
                throw new YUpdateException("A variable-length integer is out of range.");
            }
        }

        return (sign * (long)num, sign);
    }

    // A declared length beyond int.MaxValue can never fit in the input: the same error as a length beyond its end.
    private int ReadVarLength(ReadOnlySpan<byte> input)
    {
        var length = ReadVarUint(input);
        return length > int.MaxValue ? throw EndOfInput() : (int)length;
    }

    private static YUpdateException EndOfInput() => new("The input ended early.");

    // lib0 decodes with a fatal TextDecoder (ignoreBOM: a byte order mark is kept as U+FEFF).
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>UTF-8 string prefixed by its byte length (lib0 <c>readVarString</c>); invalid UTF-8 is rejected, as lib0 does.</summary>
    public string ReadVarString(ReadOnlySpan<byte> input)
    {
        var length = ReadVarLength(input);
        if (length == 0)
        {
            return string.Empty;
        }

        var bytes = ReadBytes(input, length);
        return DecodeUtf8(bytes);
    }

    /// <summary>The bytes of the string <see cref="BeginVarString"/> read last; valid until the next <see cref="BeginVarString"/>.</summary>
    public readonly ReadOnlySpan<byte> PendingString(ReadOnlySpan<byte> input) => input.Slice(_stringStart, _stringLength);

    /// <summary>
    /// First half of <see cref="ReadVarString"/>: consumes the string's bytes (the same end-of-input exceptions) and
    /// returns their count, which is at least the string's length in UTF-16 units. <see cref="EndVarString"/> then
    /// validates and decodes them.
    /// </summary>
    public int BeginVarString(ReadOnlySpan<byte> input)
    {
        var length = ReadVarLength(input);
        _stringStart = _pos;
        _stringLength = 0;
        if (length == 0)
        {
            return 0;
        }

        ReadBytes(input, length);
        _stringStart = _pos - length;
        _stringLength = length;
        return length;
    }

    /// <summary>The characters of the string <see cref="BeginVarString"/> consumed; invalid UTF-8 is rejected as in <see cref="ReadVarString"/>.</summary>
    public readonly int PendingStringLength(ReadOnlySpan<byte> input)
    {
        var bytes = PendingString(input);
        if (Ascii.IsValid(bytes))
        {
            return bytes.Length;
        }

        try
        {
            return StrictUtf8.GetCharCount(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw InvalidUtf8();
        }
    }

    /// <summary>
    /// Decodes the string <see cref="BeginVarString"/> consumed into <paramref name="destination"/>, which holds at
    /// least that many characters, and returns the characters written. Invalid UTF-8 is rejected as in <see cref="ReadVarString"/>.
    /// </summary>
    public readonly int EndVarString(ReadOnlySpan<byte> input, Span<char> destination)
    {
        if (Utf8.ToUtf16(PendingString(input), destination, out _, out var written, replaceInvalidSequences: false) != OperationStatus.Done)
        {
            throw InvalidUtf8();
        }

        return written;
    }

    /// <summary>The string of <paramref name="utf8"/>; invalid UTF-8 is rejected as in <see cref="ReadVarString"/>.</summary>
    public static string DecodeUtf8(ReadOnlySpan<byte> utf8)
    {
        try
        {
            return StrictUtf8.GetString(utf8);
        }
        catch (DecoderFallbackException)
        {
            throw InvalidUtf8();
        }
    }

    private static YUpdateException InvalidUtf8() => new("A string is not valid UTF-8.");

    /// <summary>Length-prefixed byte array (lib0 <c>readVarUint8Array</c>), as a span over the input.</summary>
    public ReadOnlySpan<byte> ReadVarUint8ArraySpan(ReadOnlySpan<byte> input) => ReadBytes(input, ReadVarLength(input));

    /// <summary>Length-prefixed byte array (lib0 <c>readVarUint8Array</c>), copied.</summary>
    public byte[] ReadVarUint8Array(ReadOnlySpan<byte> input) => ReadVarUint8ArraySpan(input).ToArray();

    /// <summary>A reader over the next length-prefixed byte array, of the same input and nesting limit.</summary>
    public Lib0Reader ReadVarUint8ArrayAsReader(ReadOnlySpan<byte> input)
    {
        var length = ReadVarUint(input);
        if (length > (uint)(_end - _pos))
        {
            throw EndOfInput();
        }

        var reader = new Lib0Reader(_pos, _pos + (int)length, MaxDepth);
        _pos += (int)length;
        return reader;
    }
}
