using System.Diagnostics;

namespace Ycs;

/// <summary>
/// Increasing Uint Optimized RLE Encoder.
/// <br/>
/// The RLE encoder counts the number of same occurences of the same value.
/// The <see cref="IncUintOptRleEncoder"/> counts if the value increases.
/// <br/>
/// I.e. <c>[7, 8, 9, 10]</c> will be encoded as <c>[-7, 4]</c>, and <c>[1, 3, 5]</c> will be encoded as <c>[1, 3, 5]</c>.
/// </summary>
/// <seealso cref="IncUintOptRleDecoder"/>
internal class IncUintOptRleEncoder : AbstractStreamEncoder<uint>
{
    private uint _state;
    private uint _count;

    public IncUintOptRleEncoder(bool pooled = false)
        : base(pooled)
    {
    }

    /// <inheritdoc/>
    protected override void WriteValue(uint value)
    {
        Debug.Assert(value <= int.MaxValue);

        if (_state + _count == value)
        {
            _count++;
        }
        else
        {
            WriteEncodedValue();

            _count = 1;
            _state = value;
        }
    }

    public override void Reset()
    {
        base.Reset();
        _state = 0;
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
            // Flush counter, unless this is the first value (count = 0).
            // Case 1: Just a single value. Set sign to positive.
            // Case 2: Write several values. Set sign to negative to indicate that there is a length coming.
            if (_count == 1)
            {
                Writer.WriteVarInt(_state);
            }
            else
            {
                // Specify 'treatZeroAsNegative' in case we pass the '-0' value.
                Writer.WriteVarInt(-_state, negativeZero: _state == 0);

                // Since count is always >1, we can decrement by one. Non-standard encoding.
                Writer.WriteVarUint(_count - 2);
            }
        }
    }
}
