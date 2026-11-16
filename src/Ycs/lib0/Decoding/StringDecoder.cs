// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

namespace Ycs;

/// <summary>
/// Decoder for <see cref="StringEncoder"/> (lib0 <c>StringDecoder</c>). The lengths count UTF-16
/// code units, so the whole string is decoded first and each value is a substring of it.
/// </summary>
internal sealed class StringDecoder : IDecoder<string>
{
    private readonly UintOptRleDecoder _lengthDecoder;
    private string? _value;
    private int _pos;

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
        _lengthDecoder.Reset();
        _value = Input.ReadVarString();
    }

    /// <summary>Drops the decoded string and the reference to the input.</summary>
    public void Clear()
    {
        _value = null;
        Input.Clear();
    }

    public string Read()
    {
        var chars = ReadSpan();
        return chars.Length == 0 ? string.Empty : chars.ToString();
    }

    /// <summary>The next value as a slice of the column's string, without allocating; same exceptions as <see cref="Read"/>.</summary>
    public ReadOnlySpan<char> ReadSpan()
    {
        var length = _lengthDecoder.Read();
        if (length == 0)
        {
            return default;
        }

        if (_value == null || length > (uint)(_value.Length - _pos))
        {
            throw new YUpdateException("A string in a v2 update is longer than the string column.");
        }

        var result = _value.AsSpan(_pos, (int)length);
        _pos += (int)length;

        // No need to keep the string in memory anymore.
        // This also covers the case when nothing but empty strings are left.
        if (_pos >= _value.Length)
        {
            _value = null;
        }

        return result;
    }
}
