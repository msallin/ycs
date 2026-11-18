namespace Ycs;

/// <seealso cref="UintOptRleEncoder"/>
internal struct UintOptRleDecoder
{
    private Lib0Reader _reader;
    private readonly ulong _max;
    private ulong _state;
    private ulong _count;

    /// <param name="max">The largest value the column may hold: 2^32 - 1 for lengths and type refs, 2^53 - 1 for client ids.</param>
    public UintOptRleDecoder(Lib0Reader column, ulong max = uint.MaxValue)
    {
        _reader = column;
        _max = max;
    }

    public ulong Read(ReadOnlySpan<byte> input)
    {
        if (_count == 0)
        {
            var (value, sign) = _reader.ReadVarInt(input);

            // If the sign is negative, we read the count too; otherwise, count is 1.
            bool isNegative = sign < 0;
            var magnitude = isNegative ? -value : value;
            if ((ulong)magnitude > _max)
            {
                throw new YUpdateException("A value in a v2 column is out of range.");
            }

            _state = (ulong)magnitude;
            _count = isNegative ? _reader.ReadVarUint(input) + 2UL : 1;
        }

        _count--;
        return _state;
    }
}
