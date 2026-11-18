using System.Text;

namespace Ycs;

/// <summary>
/// Decoder for <see cref="StringEncoder"/> (lib0 <c>StringDecoder</c>). The lengths count UTF-16 code units. An
/// all-ASCII column stays bytes and a value is widened from them; any other column is decoded to a string once.
/// </summary>
internal sealed class StringDecoder : IDecoder<string>
{
    private readonly UintOptRleDecoder _lengthDecoder;
    private int _unitsLeft;
    private int _pos;

    // The column as a string unless it is all ASCII (then its bytes are Input.PendingString, one unit each).
    private string? _value;

    // The value BeginRead returned: where it starts in the column and its length.
    private int _segmentStart;
    private int _segmentLength;

    public StringDecoder(Lib0Reader input)
    {
        ArgumentNullException.ThrowIfNull(input);
        _lengthDecoder = new UintOptRleDecoder(input);
        Reset();
    }

    /// <summary>The column this decoder reads.</summary>
    public Lib0Reader Input => _lengthDecoder.Input;

    /// <summary>Starts over at the current position of <see cref="Input"/>: reads the string.</summary>
    public void Reset()
    {
        _value = null;
        _pos = 0;
        _segmentLength = 0;
        _lengthDecoder.Reset();
        _unitsLeft = Input.BeginVarString();
        if (!Ascii.IsValid(Input.PendingString))
        {
            _value = Lib0Reader.DecodeUtf8(Input.PendingString);
            _unitsLeft = _value.Length;
        }
    }

    /// <summary>Drops the decoded string and the reference to the input.</summary>
    public void Clear()
    {
        _value = null;
        Input.Clear();
    }

    public string Read()
    {
        var length = BeginRead();
        return length == 0 ? string.Empty : string.Create(length, this, static (span, self) => self.EndRead(span));
    }

    /// <summary>
    /// Consumes the next value and returns its length in UTF-16 units; <see cref="EndRead"/> must follow before
    /// the next read. Throws when the value is longer than what is left of the column.
    /// </summary>
    public int BeginRead()
    {
        var length = _lengthDecoder.Read();
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

    /// <summary>Writes the value <see cref="BeginRead"/> consumed into <paramref name="destination"/>, which holds at least its length, and returns the length.</summary>
    public int EndRead(Span<char> destination)
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
            Widen(Input.PendingString.Slice(_segmentStart, _segmentLength), destination);
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
