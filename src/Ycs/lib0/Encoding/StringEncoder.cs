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
/// <br/>
/// Each string is UTF-8 encoded as it is written, through one stateful <see cref="Encoder"/>: a
/// surrogate pair split between two strings comes out as the encoding of the concatenation, which
/// is what lib0 writes. The column is <c>varuint(UTF-8 length)</c>, the UTF-8 bytes, then the lengths.
/// </summary>
internal sealed class StringEncoder
{
    private readonly Lib0Writer _utf8;
    private readonly UintOptRleEncoder _lengthEncoder;
    private readonly Encoder _encoder = Encoding.UTF8.GetEncoder();
    private bool _finished;

    /// <param name="pooled">Rent the buffers from the pool; <see cref="Reset"/> returns them.</param>
    public StringEncoder(bool pooled = false)
    {
        _utf8 = new Lib0Writer(pooled: pooled);
        _lengthEncoder = new UintOptRleEncoder(pooled);
    }

    public void Write(string value) => Write(value.AsSpan());

    public void Write(ReadOnlySpan<char> value)
    {
        if (_finished)
        {
            throw new InvalidOperationException("The column was already finished; reset the encoder first.");
        }

        _utf8.WriteUtf8(_encoder, value, flush: false);
        _lengthEncoder.Write((uint)value.Length);
    }

    /// <summary>The column's size in bytes; finishes the column (writing afterwards throws until <see cref="Reset"/>).</summary>
    public int GetColumnLength()
    {
        Finish();
        return Lib0Writer.VarUintSize((uint)_utf8.Length) + _utf8.Length + _lengthEncoder.GetWrittenSpan().Length;
    }

    /// <summary>The concatenated strings as UTF-8, the column's first part after their length; finishes the column.</summary>
    public ReadOnlySpan<byte> GetUtf8()
    {
        Finish();
        return _utf8.WrittenSpan;
    }

    /// <summary>The run-length encoded string lengths, the column's last part; finishes the column.</summary>
    public ReadOnlySpan<byte> GetLengths()
    {
        Finish();
        return _lengthEncoder.GetWrittenSpan();
    }

    /// <summary>The column as one array; finishes it.</summary>
    public byte[] ToArray()
    {
        var writer = new Lib0Writer(GetColumnLength());
        writer.WriteVarUint((uint)GetUtf8().Length);
        writer.WriteBytes(GetUtf8());
        writer.WriteBytes(GetLengths());
        return writer.ToArray();
    }

    public void Reset()
    {
        _utf8.Reset();
        _lengthEncoder.Reset();
        _encoder.Reset();
        _finished = false;
    }

    private void Finish()
    {
        if (!_finished)
        {
            // Writes a high surrogate left over at the end (as the replacement character).
            _utf8.WriteUtf8(_encoder, ReadOnlySpan<char>.Empty, flush: true);
            _finished = true;
        }
    }
}
