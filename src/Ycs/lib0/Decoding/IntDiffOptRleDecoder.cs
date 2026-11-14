// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

namespace Ycs;

/// <seealso cref="IntDiffOptRleEncoder"/>
internal class IntDiffOptRleDecoder : AbstractStreamDecoder<long>
{
    private long _state;
    private uint _count;
    private long _diff;

    public IntDiffOptRleDecoder(Stream input, bool leaveOpen = false)
        : base(input, leaveOpen)
    {
        // Do nothing.
    }

    /// <inheritdoc/>
    public override long Read()
    {
        CheckDisposed();

        if (_count == 0)
        {
            var diff = Stream.ReadVarInt().Value;

            // If the first bit is set, we read more data.
            bool hasCount = (diff & Bit.Bit1) > 0;

            // The counter flag rides in the LSB of `diff * 2`, so halve towards negative infinity
            // to drop it — an arithmetic shift already does that for both signs. Negating around a
            // shift of the magnitude instead rounds towards zero, reading a negative run one short.
            _diff = diff >> 1;

            _count = hasCount ? Stream.ReadVarUint() + 2 : 1;
        }

        _state += _diff;
        _count--;
        return _state;
    }
}
