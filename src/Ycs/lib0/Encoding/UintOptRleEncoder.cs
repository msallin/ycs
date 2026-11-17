namespace Ycs;

/// <summary>
/// Optimized RLE encoder that does not suffer from the mentioned problem of the basic RLE encoder.
/// Internally uses VarInt encoder to write unsigned integers.
/// If the input occurs multiple times, we write it as a negative number. The <see cref="UintOptRleDecoder"/>
/// then understands that it needs to read a count.
/// </summary>
/// <seealso cref="UintOptRleDecoder"/>
internal class UintOptRleEncoder : AbstractStreamEncoder<ulong>
{
    private ulong _state;
    private uint _count;

    public UintOptRleEncoder(bool pooled = false)
        : base(pooled)
    {
    }

    /// <inheritdoc/>
    protected override void WriteValue(ulong value)
    {

        if (_state == value)
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
                Writer.WriteVarInt((long)_state);
            }
            else
            {
                // Specify 'treatZeroAsNegative' in case we pass the '-0'.
                Writer.WriteVarInt(-(long)_state, negativeZero: _state == 0);

                // Since count is always >1, we can decrement by one. Non-standard encoding.
                Writer.WriteVarUint(_count - 2);
            }
        }
    }
}
