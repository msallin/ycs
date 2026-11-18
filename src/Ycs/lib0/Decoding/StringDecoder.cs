using System.Text;

namespace Ycs;

/// <summary>
/// Decoder for <see cref="StringEncoder"/> (lib0 <c>StringDecoder</c>). The lengths count UTF-16 code units. An
/// all-ASCII column stays bytes and a value is widened from them; any other column is decoded to a string once.
/// </summary>
internal struct StringDecoder
{
    private UintOptRleDecoder _lengthDecoder;
    private int _unitsLeft;
    private int _pos;

    // The reader as BeginVarString left it: the column's string is its pending string (all ASCII: one unit a byte).
    private readonly Lib0Reader _column;

    // The column as a string unless it is all ASCII.
    private readonly string? _value;

    // The value BeginRead returned: where it starts in the column and its length.
    private int _segmentStart;
    private int _segmentLength;

    /// <summary>Reads the column's string, which starts at <paramref name="column"/>'s position in <paramref name="input"/>; the lengths follow it.</summary>
    public StringDecoder(ReadOnlySpan<byte> input, Lib0Reader column)
    {
        _pos = 0;
        _segmentStart = 0;
        _segmentLength = 0;
        _value = null;
        _unitsLeft = column.BeginVarString(input);
        var pending = column.PendingString(input);
        if (!Ascii.IsValid(pending))
        {
            _value = Lib0Reader.DecodeUtf8(pending);
            _unitsLeft = _value.Length;
        }

        _column = column;
        _lengthDecoder = new UintOptRleDecoder(column);
    }

    public string Read(ReadOnlySpan<byte> input)
    {
        var length = BeginRead(input);
        if (length == 0)
        {
            return string.Empty;
        }

        return _value != null
            ? _value.Substring(_segmentStart, length)
            : Encoding.ASCII.GetString(_column.PendingString(input).Slice(_segmentStart, length));
    }

    /// <summary>
    /// Consumes the next value and returns its length in UTF-16 units; <see cref="EndRead"/> must follow before
    /// the next read. Throws when the value is longer than what is left of the column.
    /// </summary>
    public int BeginRead(ReadOnlySpan<byte> input)
    {
        var length = _lengthDecoder.Read(input);
        _segmentLength = 0;
        if (length == 0)
        {
            return 0;
        }

        if (length > (uint)_unitsLeft)
        {
            throw new YUpdateException("A string in a v2 update is longer than the string column.");
        }

        _segmentStart = _pos;
        _segmentLength = (int)length;
        _pos += _segmentLength;
        _unitsLeft -= _segmentLength;
        return _segmentLength;
    }

    /// <summary>The length <see cref="BeginRead"/> returned last.</summary>
    public readonly int SegmentLength => _segmentLength;

    /// <summary>Writes the value <see cref="BeginRead"/> consumed into <paramref name="destination"/>, which holds at least its length, and returns the length.</summary>
    public readonly int EndRead(ReadOnlySpan<byte> input, Span<char> destination)
    {
        if (_segmentLength == 0)
        {
            return 0;
        }

        if (_value != null)
        {
            _value.AsSpan(_segmentStart, _segmentLength).CopyTo(destination);
        }
        else
        {
            Widen(_column.PendingString(input).Slice(_segmentStart, _segmentLength), destination);
        }

        return _segmentLength;
    }

    // A typing session is mostly values of a few characters, where a loop beats the vectorized call's setup.
    private static void Widen(ReadOnlySpan<byte> ascii, Span<char> destination)
    {
        if (ascii.Length >= 16)
        {
            Ascii.ToUtf16(ascii, destination, out _);
            return;
        }

        destination = destination[..ascii.Length];
        for (var i = 0; i < destination.Length; i++)
        {
            destination[i] = (char)ascii[i];
        }
    }
}
