using System.Diagnostics;

namespace Ycs;

/// <seealso cref="RleEncoder"/>
internal struct RleDecoder
{
    private Lib0Reader _reader;
    private byte _state;
    private long _count;

    public RleDecoder(Lib0Reader column)
    {
        _reader = column;
    }

    public byte Read(ReadOnlySpan<byte> input)
    {
        if (_count == 0)
        {
            _state = _reader.ReadByte(input);

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
