// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

namespace Ycs;

/// <seealso cref="UintOptRleEncoder"/>
internal class UintOptRleDecoder : AbstractStreamDecoder<uint>
{
    private uint _state;
    private ulong _count;

    public UintOptRleDecoder(Lib0Reader input)
        : base(input)
    {
        // Do nothing.
    }

    public override uint Read()
    {

        if (_count == 0)
        {
            var (value, sign) = Reader.ReadVarInt();

            // If the sign is negative, we read the count too; otherwise, count is 1.
            bool isNegative = sign < 0;
            var magnitude = isNegative ? -value : value;
            if (magnitude > uint.MaxValue)
            {
                throw new YUpdateException("A value in a v2 column is out of range.");
            }

            _state = (uint)magnitude;
            _count = isNegative ? Reader.ReadVarUint() + 2UL : 1;
        }

        _count--;
        return _state;
    }
}
