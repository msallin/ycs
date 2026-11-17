#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

internal readonly partial struct ItemRef
{
    /// <summary>
    /// Whether an insert of <paramref name="kind"/> content into <paramref name="parent"/> between this item and
    /// <paramref name="right"/>, as the next struct of <paramref name="client"/>, can continue this item in place: the
    /// new item would merge into this one at the end of the transaction (<see cref="CanMergeItemWith"/>: it would be this
    /// item's continuation by clock and origin, with this right origin, undeleted, not redone), this item is not kept,
    /// does not end with the first half of a surrogate pair (splitting there later would replace it), the parent
    /// is not deleted (the new item would be deleted at once), nobody observes the parent, and the transaction is
    /// the only one of its batch.
    /// </summary>
    public bool CanExtendRun(Transaction transaction, AbstractType parent, ItemRef right, Type kind, long client)
    {
        // Not when the run would be split again at the end of the transaction (someone observes it), which costs
        // more than the item: a split copies a long run's text out of its builder.
        // Not in a transaction started while another one is cleaned up (from its observers): Yjs integrates the new
        // item before the other one's merge, which merges it on its own terms, and the other one's events, read
        // then, see it as an item of its own.
        if (!(transaction.Local
            && transaction.Doc._transactionCleanups.Count == 1
            && !transaction.Doc.RunExtensionsDisabledForTests
            && !parent.IsObserved(transaction)
            && !transaction.Doc.HasBeforeObserverCallsListeners
            && Content?.GetType() == kind))
        {
            return false;
        }

        // No allocation here: the slot stays valid. The right origin compares by client index and clock.
        ref readonly var slot = ref Read();
        return _slab.Clients[slot.Client] == client
            && slot.Clock + slot.Length == transaction.Doc.Store.GetState(client)
            && Parent == parent
            && ParentSub == null
            && slot.Right == right.Handle
            && (right.IsNull ? slot.ROriginClient < 0 : slot.ROriginClient == right.ClientIndex && slot.ROriginClock == right.Clock)
            && (slot.Info & (Slot.DeletedFlag | Slot.KeepFlag | Slot.HasRedoneFlag)) == 0
            && !parent.ItemDeleted
            && !(Content is ContentString str && str.EndsWithHighSurrogate);
    }

    /// <summary>
    /// Continues this item in place by <paramref name="length"/> new elements its content took in already (see
    /// <see cref="CanExtendRun"/>): what integrating them as an item of their own after it would do (the parent's
    /// length, the client's clock, the transaction's changed types), and the transaction records where that item
    /// would have started.
    /// </summary>
    public void ExtendRun(Transaction transaction, int length)
    {
        var start = new ID(Id.Client, Id.Clock + Length);
        Length += length;
        var parent = Parent;
        parent.Length += length;
        transaction.Doc.Store.NoteRunExtended(this, length);
        transaction.AddChangedTypeToTransaction(parent, null);
        transaction.RecordRunExtension(start);
    }
}
