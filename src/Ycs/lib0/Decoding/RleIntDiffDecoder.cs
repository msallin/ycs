using System.Diagnostics;

namespace Ycs;

/// <seealso cref="RleIntDiffEncoder"/>
internal class RleIntDiffDecoder : AbstractStreamDecoder<long>
{
    private readonly long _start;
    private long _state;
    private long _count;

    public RleIntDiffDecoder(Lib0Reader input, long start)
        : base(input)
    {
        _start = start;
        _state = start;
    }

    /// <inheritdoc/>
    public override long Read()
    {

        if (_count == 0)
        {
            _state += Reader.ReadVarInt().Value;

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
        _state = _start;
        _count = 0;
    }
}
