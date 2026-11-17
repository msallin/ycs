// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

namespace Ycs;

internal sealed class GC : AbstractStruct
{
    internal const byte StructGCRefNumber = 0;

    internal GC(ID id, int length)
        : base(id, length)
    {
        // Do nothing.
    }

    public override bool Deleted => true;

    // MergeUpdates offers any neighbour, as Yjs does; only a GC extends a GC.
    internal override bool CanMergeWith(AbstractStruct right) => right is GC;

    internal override bool MergeWith(AbstractStruct right)
    {
        if (!CanMergeWith(right))
        {
            return false;
        }

        Length += right.Length;
        return true;
    }

    internal override void Write(IUpdateEncoder encoder, int offset)
    {
        encoder.WriteInfo(StructGCRefNumber);
        encoder.WriteLength(Length - offset);
    }
}
