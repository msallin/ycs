// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

namespace Ycs;

/// <summary>
/// A combination of the <see cref="IntDiffEncoder"/> and the <see cref="UintOptRleEncoder"/>.
/// The count approach is similar to the <see cref="UintOptRleDecoder"/>, but instead of using
/// the negative bitflag, it encodes in the LSB whether a count is to be read.
/// <br/>
/// The flag is added to <c>diff * 2</c> (as lib0 does, not shifted in), so a value is not limited to 31 bits:
/// the clocks it encodes go up to <see cref="Limits.MaxClock"/>, and lib0 writes any safe integer.
/// <br/>
/// Encodes <c>[1, 2, 3, 2]</c> as <c>[3, 1, -2]</c> (more specifically <c>[(1 * 2) + 1, 3 - 2, (-1 * 2) + 0]</c>).
/// <br/>
/// Internally uses variable length encoding. Contrary to the normal UintVar encoding, the first byte contains:
/// * 1 bit that denotes whether the next value is a count (LSB).
/// * 1 bit that denotes whether this value is negative (MSB - 1).
/// * 1 bit that denotes whether to continue reading the variable length integer (MSB).
/// <br/>
/// Therefore, only five bits remain to encode diff ranges.
/// <br/>
/// Use this encoder only when appropriate. In most cases, this is probably a bad idea.
/// </summary>
/// <seealso cref="IntDiffOptRleDecoder"/>
internal class IntDiffOptRleEncoder : AbstractStreamEncoder<long>
{
    private long _state = 0;
    private long _diff = 0;
    private uint _count = 0;

    public IntDiffOptRleEncoder(bool pooled = false)
        : base(pooled)
    {
    }

    /// <inheritdoc/>
    protected override void WriteValue(long value)
    {
        if (_diff == value - _state)
        {
            _state = value;
            _count++;
        }
        else
        {
            WriteEncodedValue();

            _count = 1;
            _diff = value - _state;
            _state = value;
        }
    }

    public override void Reset()
    {
        base.Reset();
        _state = 0;
        _diff = 0;
        _count = 0;
    }

    protected override void Flush()
    {
        WriteEncodedValue();
    }

    private void WriteEncodedValue()
    {
        if (_count > 0)
        {
            // 31bit making up a diff | whether to write the counter (LSB). The flag is added to
            // `diff * 2` rather than OR'd into the magnitude, so a negative diff stays
            // `2 * diff + flag`, matching lib0's writer. Folding the flag into the magnitude
            // instead yields `2 * diff - 1`, which lib0's reader halves towards negative infinity
            // and so decodes one short, shifting every later value in the stream.
            var encodedDiff = _diff * 2 + (_count == 1 ? 0 : 1);

            Writer.WriteVarInt(encodedDiff);

            if (_count > 1)
            {
                // Since count is always >1, we can decrement by one. Non-standard encoding.
                Writer.WriteVarUint(_count - 2);
            }
        }
    }
}
