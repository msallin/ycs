namespace Ycs;

/// <seealso cref="IntDiffEncoder"/>
internal struct IntDiffDecoder
{
    private Lib0Reader _reader;
    private long _state;

    public IntDiffDecoder(Lib0Reader column, long start)
    {
        _reader = column;
        _state = start;
    }

    public long Read(ReadOnlySpan<byte> input)
    {
        _state += _reader.ReadVarInt(input).Value;
        return _state;
    }
}
