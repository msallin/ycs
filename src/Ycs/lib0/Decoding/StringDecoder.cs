// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

namespace Ycs;

/// <summary>
/// Decoder for <see cref="StringEncoder"/> (lib0 <c>StringDecoder</c>).
/// </summary>
internal sealed class StringDecoder : IDecoder<string>
{
    private readonly UintOptRleDecoder _lengthDecoder;
    private string? _value;
    private int _pos;

    public StringDecoder(Lib0Reader input)
    {
        ArgumentNullException.ThrowIfNull(input);
        _value = input.ReadVarString();
        _lengthDecoder = new UintOptRleDecoder(input);
    }

    public string Read()
    {
        var length = (int)_lengthDecoder.Read();
        if (length == 0)
        {
            return string.Empty;
        }

        if (_value == null || (uint)length > (uint)(_value.Length - _pos))
        {
            throw new InvalidDataException("String length out of range.");
        }

        var result = _value.Substring(_pos, length);
        _pos += length;

        // No need to keep the string in memory anymore.
        // This also covers the case when nothing but empty strings are left.
        if (_pos >= _value.Length)
        {
            _value = null;
        }

        return result;
    }
}
