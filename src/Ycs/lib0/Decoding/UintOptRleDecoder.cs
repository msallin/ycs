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
    private uint _count;

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
            if (isNegative)
            {
                _state = (uint)(-value);
                _count = Reader.ReadVarUint() + 2;
            }
            else
            {
                _state = (uint)value;
                _count = 1;
            }
        }

        _count--;
        return _state;
    }
}
