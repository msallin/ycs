using System.Diagnostics;

namespace Ycs;

/// <seealso cref="RleIntDiffEncoder"/>
internal struct RleIntDiffDecoder
{
    private Lib0Reader _reader;
    private long _state;
    private long _count;

    public RleIntDiffDecoder(Lib0Reader column, long start)
    {
        _reader = column;
        _state = start;
    }

    public long Read(ReadOnlySpan<byte> input)
    {
        if (_count == 0)
        {
            _state += _reader.ReadVarInt(input).Value;

            if (_reader.HasContent)
            {
                // See encoder implementation for the reason why this is incremented.
                _count = _reader.ReadVarUint(input) + 1;
                Debug.Assert(_count > 0);
            }
            else
            {
                // Read the current value forever.
                _count = -1;
            }
        }

        _count--;
        return _state;
    }
}
