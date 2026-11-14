// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System.Text;

namespace Ycs;

/// <summary>
/// Optimized string encoder (lib0 <c>StringEncoder</c>).
/// <br/>
/// Encoding many small strings in a simple Encoder is not very efficient. The function call to decode a string takes some time and creates references that must be eventually deleted.
/// In practice, when decoding several million small strings, the GC will kick in more and more often to collect orphaned string objects (or maybe there is another reason?).
/// <br/>
/// This string encoder solves the above problem. All strings are concatenated and written as a single string using a single encoding call.
/// <br/>
/// The lengths are encoded using a <see cref="UintOptRleEncoder"/>, in UTF-16 code units as in JavaScript.
/// </summary>
internal sealed class StringEncoder : IEncoder<string>
{
    private readonly StringBuilder _sb = new();
    private readonly UintOptRleEncoder _lengthEncoder = new();
    private Lib0Writer _output;

    public void Write(string value)
    {
        _sb.Append(value);
        _lengthEncoder.Write((uint)value.Length);
    }

    public void Write(ReadOnlySpan<char> value)
    {
        _sb.Append(value);
        _lengthEncoder.Write((uint)value.Length);
    }

    public ReadOnlySpan<byte> GetWrittenSpan()
    {
        if (_output == null)
        {
            _output = new Lib0Writer(_sb.Length + 16);
            _output.WriteVarString(_sb.ToString());
            _output.WriteBytes(_lengthEncoder.GetWrittenSpan());
        }

        return _output.WrittenSpan;
    }
}
