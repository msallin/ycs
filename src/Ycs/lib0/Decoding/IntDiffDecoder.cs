// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

namespace Ycs;

/// <seealso cref="IntDiffEncoder"/>
internal class IntDiffDecoder : AbstractStreamDecoder<long>
{
    private long _state;

    public IntDiffDecoder(Lib0Reader input, long start)
        : base(input)
    {
        _state = start;
    }

    /// <inheritdoc/>
    public override long Read()
    {

        _state += Reader.ReadVarInt().Value;
        return _state;
    }
}
