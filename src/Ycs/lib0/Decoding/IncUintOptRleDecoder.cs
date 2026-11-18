namespace Ycs;

/// <seealso cref="IncUintOptRleEncoder"/>
internal struct IncUintOptRleDecoder
{
    private Lib0Reader _reader;
    private uint _state;
    private ulong _count;

    public IncUintOptRleDecoder(Lib0Reader column)
    {
        _reader = column;
    }

    public uint Read(ReadOnlySpan<byte> input)
    {
        if (_count == 0)
        {
            var (value, sign) = _reader.ReadVarInt(input);

            // If the sign is negative, we read the count too; otherwise. count is 1.
            bool isNegative = sign < 0;
            var magnitude = isNegative ? -value : value;
            if (magnitude > uint.MaxValue)
            {
                throw new YUpdateException("A value in a v2 column is out of range.");
            }

            _state = (uint)magnitude;
            _count = isNegative ? _reader.ReadVarUint(input) + 2UL : 1;
        }

        _count--;
        return unchecked(_state++);
    }
}
