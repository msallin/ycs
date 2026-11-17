using System.Diagnostics;

namespace Ycs;

/// <seealso cref="RleEncoder"/>
internal class RleDecoder : AbstractStreamDecoder<byte>
{
    private byte _state;
    private long _count;

    public RleDecoder(Lib0Reader input)
        : base(input)
    {
        // Do nothing.
    }

    /// <inheritdoc/>
    public override byte Read()
    {

        if (_count == 0)
        {
            _state = Reader.ReadByte();

            if (HasContent)
            {
                // See encoder implementation for the reason why this is incremented.
                _count = Reader.ReadVarUint() + 1;
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

    public override void Reset()
    {
        _state = 0;
        _count = 0;
    }
}
