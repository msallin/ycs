// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

namespace Ycs;

/// <seealso cref="UintOptRleEncoder"/>
internal class UintOptRleDecoder : AbstractStreamDecoder<ulong>
{
    private readonly ulong _max;
    private ulong _state;
    private ulong _count;

    /// <param name="max">The largest value the column may hold: 2^32 - 1 for lengths and type refs, 2^53 - 1 for client ids.</param>
    public UintOptRleDecoder(Lib0Reader input, ulong max = uint.MaxValue)
        : base(input)
    {
        _max = max;
    }

    public override ulong Read()
    {

        if (_count == 0)
        {
            var (value, sign) = Reader.ReadVarInt();

            // If the sign is negative, we read the count too; otherwise, count is 1.
            bool isNegative = sign < 0;
            var magnitude = isNegative ? -value : value;
            if ((ulong)magnitude > _max)
            {
                throw new YUpdateException("A value in a v2 column is out of range.");
            }

            _state = (ulong)magnitude;
            _count = isNegative ? Reader.ReadVarUint() + 2UL : 1;
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
