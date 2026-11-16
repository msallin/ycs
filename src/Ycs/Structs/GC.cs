// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

namespace Ycs;

internal partial class GC : AbstractStruct
{
    internal const byte StructGCRefNumber = 0;

    internal GC(ID id, int length)
        : base(id, length)
    {
        // Do nothing.
    }

    public override bool Deleted => true;

    // Set once, on the right part of a merge and on a GC struct a later one replaced: it is in no list, and its Id and
    // Length are the range it held (an ItemView's hint is stale).
    internal bool Detached { get; private set; }

    internal void MarkDetached() => Detached = true;

    // MergeUpdates offers any neighbour, as Yjs does; only a GC extends a GC.
    internal override bool CanMergeWith(AbstractStruct right) => right is GC;

    internal override bool MergeWith(AbstractStruct right)
    {
        if (!CanMergeWith(right))
        {
            return false;
        }

        Length += right.Length;
        ((GC)right).MarkDetached();
        return true;
    }

    internal override void Delete(Transaction transaction)
    {
        // Do nothing.
    }

    internal override void Integrate(Transaction transaction, int offset)
    {
        if (offset > 0)
        {
            Id = new ID(Id.Client, Id.Clock + offset);
            Length -= offset;
        }

        transaction.Doc.Store.AddStruct(ItemRef.Of(this));
    }

    internal override void Write(IUpdateEncoder encoder, int offset)
    {
        encoder.WriteInfo(StructGCRefNumber);
        encoder.WriteLength(Length - offset);
    }
}
