namespace Ycs;

/// <summary>
/// Decoder for <see cref="StringEncoder"/> (lib0 <c>StringDecoder</c>). The lengths count UTF-16 code units; the
/// column stays UTF-8 and a value is decoded from the bytes that hold its units. A length that ends between the two
/// halves of a surrogate pair decodes the rest of the column to a string once and slices that.
/// </summary>
internal sealed class StringDecoder : IDecoder<string>
{
    private readonly UintOptRleDecoder _lengthDecoder;
    private int _unitsLeft;
    private int _bytePos;
    private bool _ascii;

    // Set once a length splits a surrogate pair: the rest of the column as a string, read from _stringPos.
    private string? _value;
    private int _stringPos;

    // The value BeginRead returned: bytes of the column, or characters of _value.
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
        _bytePos = 0;
        _segmentLength = 0;
        _lengthDecoder.Reset();
        _unitsLeft = Input.BeginVarString();
        _ascii = _unitsLeft == Input.PendingString.Length;
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

        var units = (int)length;
        if (_value == null)
        {
            var bytes = BytesOf(units);
            if (bytes < 0)
            {
                _value = Input.PendingStringFrom(_bytePos);
                _stringPos = 0;
            }
            else
            {
                _segmentStart = _bytePos;
                _bytePos += bytes;
                _segmentLength = bytes;
            }
        }

        if (_value != null)
        {
            _segmentStart = _stringPos;
            _segmentLength = units;
            _stringPos += units;
        }

        _unitsLeft -= units;
        return units;
    }

    /// <summary>Writes the value <see cref="BeginRead"/> consumed into <paramref name="destination"/>, which holds exactly its length.</summary>
    public void EndRead(Span<char> destination)
    {
        if (_segmentLength == 0)
        {
            return;
        }

        if (_value != null)
        {
            _value.AsSpan(_segmentStart, _segmentLength).CopyTo(destination);
        }
        else
        {
            Input.DecodePending(_segmentStart, _segmentLength, destination);
        }
    }

    // The bytes that hold the next `units` units of the validated column, or -1 when the last one is half a pair.
    private int BytesOf(int units)
    {
        if (_ascii)
        {
            return units;
        }

        var column = Input.PendingString;
        var i = _bytePos;
        for (var u = 0; u < units;)
        {
            var lead = column[i];
            if (lead < 0x80)
            {
                i++;
                u++;
            }
            else if (lead < 0xE0)
            {
                i += 2;
                u++;
            }
            else if (lead < 0xF0)
            {
                i += 3;
                u++;
            }
            else if (units - u < 2)
            {
                return -1;
            }
            else
            {
                i += 4;
                u += 2;
            }
        }

        return i - _bytePos;
    }
}
