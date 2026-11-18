namespace Ycs;

/// <seealso cref="IntDiffOptRleEncoder"/>
internal struct IntDiffOptRleDecoder
{
    private Lib0Reader _reader;

    private long _state;
    private ulong _count;
    private long _diff;

    public IntDiffOptRleDecoder(Lib0Reader column)
    {
        _reader = column;
    }

    public long Read(ReadOnlySpan<byte> input)
    {
        if (_count == 0)
        {
            var diff = _reader.ReadVarInt(input).Value;

            // If the first bit is set, we read more data.
            bool hasCount = (diff & Bit.Bit1) > 0;

            // The counter flag rides in the LSB of `diff * 2`, so halve towards negative infinity
            // to drop it — an arithmetic shift already does that for both signs. Negating around a
            // shift of the magnitude instead rounds towards zero, reading a negative run one short.
            _diff = diff >> 1;

            _count = hasCount ? _reader.ReadVarUint(input) + 2UL : 1;
        }

        // Both stay within ±2^53, so the sum cannot overflow; the result must stay there too.
        _state += _diff;
        if (_state > JsValue.MaxSafeInteger || _state < -JsValue.MaxSafeInteger)
        {
            throw new YUpdateException("A value in a v2 column is out of range.");
        }

        _count--;
        return _state;
    }
}
