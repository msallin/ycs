// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

using System.Diagnostics;

namespace Ycs;

/// <summary>Yjs' <c>Item</c> algorithms (integrate, split, merge, delete, gc, write), on the reference.</summary>
internal readonly partial struct ItemRef
{
    /// <summary>
    /// Returns the next non-deleted item.
    /// </summary>
    public ItemRef Next
    {
        get
        {
            var n = Right;
            while (!n.IsNull && n.Deleted)
            {
                n = n.IsItem ? n.Right : default;
            }

            return n;
        }
    }

    /// <summary>
    /// Returns the previous non-deleted item.
    /// </summary>
    public ItemRef Prev
    {
        get
        {
            var n = Left;
            while (!n.IsNull && n.Deleted)
            {
                n = n.IsItem ? n.Left : default;
            }

            return n;
        }
    }

    /// <summary>
    /// Computes the last content address of this Item.
    /// </summary>
    public ID LastId => Length == 1 ? Id : new ID(Id.Client, Id.Clock + Length - 1);

    // Deleting a GC struct does nothing (it is deleted already).
    public void Delete(Transaction transaction)
    {
        if (IsItem)
        {
            DeleteItem(transaction);
        }
    }

    /// <summary>
    /// Integrates this item, which is not in the store yet or is a store item put in again (Yjs' <c>Item#integrate</c>):
    /// <paramref name="offset"/> is the part of its range the store holds already.
    /// </summary>
    public void Integrate(Transaction transaction, int offset) => IntegrateItem(transaction, offset);

    /// <summary>
    /// Where a decoded struct enters the store (the one place; <see cref="Of"/> and the object's own methods are not a way
    /// in). <paramref name="decoded"/> is a struct of an update the integration plan has put in order, with everything
    /// it refers to in the store or planned before it. An item's references are resolved first, whether or not it is
    /// integrated, in the order of Yjs: the origins' structs are split where an origin points into one, and the parent
    /// is resolved (to nothing when a neighbour is a GC struct or the parent type is collected: the item then enters as
    /// a GC struct). Then, when <paramref name="integrate"/> is set, the struct is integrated from
    /// <paramref name="offset"/>, which is the part of its range the store holds already (an item that is integrated
    /// whole in the store is the same object; a GC struct is trimmed in place), and the reference to the store's
    /// struct is returned. Without <paramref name="integrate"/> the struct stays outside the store (<c>default</c>).
    /// A <see cref="Skip"/> struct is never adopted.
    /// </summary>
    public static ItemRef Adopt(AbstractStruct decoded, Transaction transaction, int offset, bool integrate)
    {
        var adopted = Of(decoded);
        if (adopted.IsItem)
        {
            adopted.ResolveReferences(transaction, transaction.Doc.Store);
        }

        if (!integrate)
        {
            return default;
        }

        if (adopted.IsItem)
        {
            adopted.IntegrateItem(transaction, offset);
        }
        else
        {
            Debug.Assert(decoded is GC, "Only items and GC structs are adopted.");
            ((GC)decoded).TrimStart(offset);
            transaction.Doc.Store.AddStruct(adopted);
        }

        return adopted;
    }

    /// <summary>
    /// Finds the structs a decoded item refers to, once they are all there (the second half of Yjs' <c>Item.getMissing</c>):
    /// left and right are set, splitting store structs where needed, and the parent is resolved to a type, or to null when
    /// the item is to be integrated as a GC struct.
    /// </summary>
    private void ResolveReferences(Transaction transaction, StructStore store)
    {
        if (Parent is string rootName)
        {
            Parent = transaction.Doc.Get<AbstractType>(rootName);
        }

        if (LeftOrigin != null)
        {
            var left = store.GetItemCleanEnd(transaction, LeftOrigin.Value);
            Left = left;
            LeftOrigin = left.IsItem ? left.LastId : null;
        }

        if (RightOrigin != null)
        {
            var right = store.GetItemCleanStart(transaction, RightOrigin.Value);
            Right = right;
            RightOrigin = right.Id;
        }

        if (Left.IsGC || Right.IsGC)
        {
            Parent = null;
        }
        else if (Parent == null)
        {
            // Only set parent if this shouldn't be garbage collected; the left neighbour first, as Yjs.
            if (Left.IsItem)
            {
                Parent = Left.Parent;
                ParentSub = Left.ParentSub;
            }
            else if (Right.IsItem)
            {
                Parent = Right.Parent;
                ParentSub = Right.ParentSub;
            }
        }
        else if (Parent is ID pid)
        {
            var parentItem = store.Find(pid);
            Parent = parentItem.IsGC ? null : ((parentItem.IsItem ? parentItem.Content : null) as ContentType)?.Type;
        }
    }

    public bool CanMergeWith(ItemRef right) => IsItem ? CanMergeItemWith(right) : _struct.CanMergeWith(right._struct);

    public bool MergeWith(ItemRef right) => IsItem ? MergeItemWith(right) : _struct.MergeWith(right._struct);

    public void Write(IUpdateEncoder encoder, int offset)
    {
        if (IsItem)
        {
            WriteItem(encoder, offset);
        }
        else
        {
            _struct.Write(encoder, offset);
        }
    }

    // The run-head index holds whether an item is deleted: for an item the store holds, the caller then calls
    // StructStore.Refresh (MarkDeleted(Transaction), ContentDeleted.Integrate); a split's right part is not in it yet.
    public void MarkDeleted() => ItemObject.MarkDeleted();

    /// <summary>
    /// Whether <paramref name="right"/> merges into this item (Yjs' <c>Item#mergeWith</c> conditions; a content of a
    /// mergeable kind always merges with one of its kind).
    /// </summary>
    /// https://github.com/yjs/yjs/blob/c2f0ca3faef731a93ba28f286ffafe0e1ea1a3aa/src/structs/Item.js#L557
    private bool CanMergeItemWith(ItemRef right) =>
        right.IsItem &&
        right.LeftOrigin == LastId &&
        Right == right &&
        right.RightOrigin == RightOrigin &&
        Id.Client == right.Id.Client &&
        Id.Clock + Length == right.Id.Clock &&
        Deleted == right.Deleted &&
        Redone == null &&
        right.Redone == null &&
        Content.GetType() == right.Content.GetType() &&
        Content.Mergeable;

    /// <summary>Try to merge two items.</summary>
    private bool MergeItemWith(ItemRef right)
    {
        if (!CanMergeItemWith(right))
        {
            return false;
        }

        if (!Content.MergeWith(right.Content))
        {
            throw new InvalidOperationException(
                $"The content of {right.Id.Client}:{right.Id.Clock} did not merge into its left neighbour's although its kind merges (a bug of " +
                "Ycs). Nothing was changed.");
        }

        (Parent as AbstractType)?.Doc?.Store.NoteDeparture();
        (Parent as YArrayBase)?.OnMerge(this, right);

        if (right.Keep)
        {
            Keep = true;
        }

        var rightRight = right.Right;
        Right = rightRight;
        if (rightRight.IsItem)
        {
            rightRight.Left = this;
        }

        Length += right.Length;
        right.MarkDetached();
        return true;
    }

    /// <summary>Marks the struct as out of every list (merged away, or replaced by a GC struct).</summary>
    public void MarkDetached()
    {
        if (_struct is Item item)
        {
            item.MarkDetached();
        }
        else
        {
            ((GC)_struct).MarkDetached();
        }
    }

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
        return transaction.Local
            && transaction.Doc._transactionCleanups.Count == 1
            && !transaction.Doc.RunExtensionsDisabledForTests
            && !parent.IsObserved(transaction)
            && !transaction.Doc.HasBeforeObserverCallsListeners
            && Content.GetType() == kind
            && Id.Client == client
            && Id.Clock + Length == transaction.Doc.Store.GetState(client)
            && Parent == parent
            && ParentSub == null
            && Right == right
            && RightOrigin == (right.IsNull ? null : right.Id)
            && !Deleted
            && !Keep
            && Redone == null
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
        ((AbstractType)Parent).Length += length;
        transaction.Doc.Store.NoteRunExtended(this, length);
        transaction.AddChangedTypeToTransaction((AbstractType)Parent, null);
        transaction.RecordRunExtension(start);
    }

    /// <summary>
    /// Mark this item as Deleted.
    /// </summary>
    private void DeleteItem(Transaction transaction)
    {
        if (MarkDeleted(transaction))
        {
            Content.Delete(transaction);
        }
    }

    /// <summary>
    /// Marks this item deleted in <paramref name="transaction"/> without deleting its content (what is
    /// nested in it); false when it was deleted already.
    /// </summary>
    public bool MarkDeleted(Transaction transaction)
    {
        if (Deleted)
        {
            return false;
        }

        var parent = Parent as AbstractType;
        if (Countable && ParentSub == null)
        {
            Debug.Assert(parent != null);
            parent.Length -= Length;
        }

        if (ParentSub != null && parent._map.TryGetValue(ParentSub, out var current) && current == this)
        {
            parent._mapCount--;
        }

        MarkDeleted();
        transaction.Doc.Store.Refresh(this);
        transaction.DeleteSet.Add(Id.Client, Id.Clock, Length);
        transaction.AddChangedTypeToTransaction(parent, ParentSub);
        return true;
    }

    private void IntegrateItem(Transaction transaction, int offset)
    {
        if (offset > 0)
        {
            Id = new ID(Id.Client, Id.Clock + offset);
            Left = transaction.Doc.Store.GetItemCleanEnd(transaction, new ID(Id.Client, Id.Clock - 1));
            if (Left.IsGC && Parent != null)
            {
                // The store collected the first part of an item that still has a parent: no peer writes that. Yjs
                // throws (a TypeError, in the conflict resolution) at the same point.
                throw new YUpdateException(
                    $"The struct {Id.Client}:{Id.Clock - offset} continues a struct the document holds as garbage collected, but has a parent. " +
                    "The structs integrated before stay in the document; the rest of the update, and its deletions, were not applied.")
                {
                    PartlyApplied = true,
                };
            }

            LeftOrigin = Left.IsItem ? Left.LastId : null;
            Content = (IContentEx)Content.Splice(offset);
            Length -= offset;
        }

        if (Parent != null)
        {
            AbstractType mapParent = null;
            var previousCounted = false;

            if ((Left.IsNull && (Right.IsNull || (Right.IsItem && !Right.Left.IsNull))) || (!Left.IsNull && (Left.IsItem ? Left.Right : default) != Right))
            {
                var left = Left;
                ItemRef o;

                // Set 'o' to the first conflicting item.
                if (!left.IsNull)
                {
                    o = left.IsItem ? left.Right : default;
                }
                else if (ParentSub != null)
                {
                    Debug.Assert(Parent is AbstractType);

                    ItemRef item = default;
                    (Parent as AbstractType)?._map?.TryGetValue(ParentSub, out item);
                    o = item;

                    while (!o.IsNull && o.IsItem && !o.Left.IsNull)
                    {
                        o = o.Left;
                    }
                }
                else
                {
                    Debug.Assert(Parent is AbstractType);
                    o = Parent is AbstractType start ? start._start : default;
                }

                Left = transaction.Doc.Store.ResolveConflict(this, left, o);
            }

            // Reconnect left/right + update parent map/start if necessary.
            if (!Left.IsNull)
            {
                var leftItem = Left;
                if (leftItem.IsItem)
                {
                    var right = leftItem.Right;
                    Right = right;
                    leftItem.Right = this;
                }
                else
                {
                    Right = default;
                }
            }
            else
            {
                ItemRef r;

                if (ParentSub != null)
                {
                    ItemRef item = default;
                    (Parent as AbstractType)?._map?.TryGetValue(ParentSub, out item);
                    r = item;

                    while (!r.IsNull && r.IsItem && !r.Left.IsNull)
                    {
                        r = r.Left;
                    }
                }
                else
                {
                    if (Parent is AbstractType abstractTypeParent)
                    {
                        r = abstractTypeParent._start;
                        abstractTypeParent._start = this;
                    }
                    else
                    {
                        r = default;
                    }
                }

                Right = r;
            }

            var rightItem = Right;
            if (!rightItem.IsNull)
            {
                if (rightItem.IsItem)
                {
                    // Its left changes: when the old one is its predecessor by clock, it was linked in the run-head index and
                    // becomes a break, which StructStore.Refresh records.
                    var oldLeft = rightItem.Left;
                    var relinked = oldLeft.IsItem && oldLeft.Id.Client == rightItem.Id.Client && oldLeft.Id.Clock + oldLeft.Length == rightItem.Id.Clock;
                    rightItem.Left = this;
                    if (relinked)
                    {
                        transaction.Doc.Store.Refresh(rightItem);
                    }
                }
            }
            else if (ParentSub != null)
            {
                // Set as current parent value if right == null and this is parentSub.
                mapParent = (AbstractType)Parent;
                previousCounted = mapParent._map.TryGetValue(ParentSub, out var previous) && !previous.Deleted;
                mapParent._map[ParentSub] = this;

                // This is the current attribute value of parent. Delete right.
                if (!Left.IsNull)
                {
                    Left.Delete(transaction);
                }
            }

            // Adjust length of parent.
            if (ParentSub == null && Countable && !Deleted)
            {
                Debug.Assert(Parent is AbstractType);
                (Parent as AbstractType).Length += Length;
            }

            transaction.Doc.Store.AddStruct(this);
            Content.Integrate(transaction, this);

            // Counted once its content is integrated: deleted content marks the item deleted.
            if (mapParent != null)
            {
                mapParent._mapCount += (Deleted ? 0 : 1) - (previousCounted ? 1 : 0);
            }

            // Add parent to transaction.changed.
            transaction.AddChangedTypeToTransaction(Parent as AbstractType, ParentSub);

            if ((Parent is AbstractType { ItemDeleted: true }) || (ParentSub != null && !Right.IsNull))
            {
                // Delete if parent is deleted or if this is not the current attribute value of parent.
                Delete(transaction);
            }
        }
        else
        {
            // Parent is not defined. Integrate GC struct instead.
            transaction.Doc.Store.AddStruct(NewGC(Id, Length));
        }
    }

    public void Gc(StructStore store, bool parentGCd)
    {
        if (!Deleted)
        {
            throw new InvalidOperationException();
        }

        Content.Gc(store);

        if (parentGCd)
        {
            store.ReplaceStruct(this, NewGC(Id, Length));
        }
        else
        {
            Content = new ContentDeleted(Length);
        }
    }

    /// <summary>
    /// Make sure that neither item nor any of its parents is ever deleted.
    /// This property does not persist when storing it into a database or when
    /// sending it to other peers.
    /// </summary>
    public void KeepItemAndParents(bool value)
    {
        if (!IsNull && Keep != value)
        {
            Keep = value;
            (Parent as AbstractType)?.KeepItemAndParents(value);
        }
    }

    public bool IsVisible(Snapshot snap)
    {
        return snap == null
            ? !Deleted
            : snap.StateVector.TryGetValue(Id.Client, out long value) && value > Id.Clock && !snap.DeleteSet.IsDeleted(Id);
    }

    private void WriteItem(IUpdateEncoder encoder, int offset)
    {
        string root = null;
        ID parentId = default;
        if (offset == 0 && LeftOrigin == null && RightOrigin == null)
        {
            switch (Parent)
            {
                case AbstractType { IsRoot: true } rootType:
                    root = rootType.FindRootTypeKey();
                    break;
                case AbstractType type:
                    parentId = type.ItemId;
                    break;
                default:
                    throw new InvalidOperationException("An item without origins has no parent.");
            }
        }

        Item.WriteItem(encoder, offset, Id, LeftOrigin, RightOrigin, ParentSub, Content, root, parentId);
    }

    /// <summary>
    /// Split 'leftItem' into two items.
    /// </summary>
    public ItemRef SplitItem(Transaction transaction, int diff)
    {
        var client = Id.Client;
        var clock = Id.Clock;

        var rightItem = Of(new Item(
            new ID(client, clock + diff),
            _struct,
            new ID(client, clock + diff - 1),
            Right._struct,
            RightOrigin,
            Parent,
            ParentSub,
            Content.Splice(diff)));

        if (Deleted)
        {
            rightItem.MarkDeleted();
        }

        if (Keep)
        {
            rightItem.Keep = true;
        }

        if (Redone != null)
        {
            rightItem.Redone = new ID(Redone.Value.Client, Redone.Value.Clock + diff);
        }

        // Update left (do not set leftItem.RightOrigin as it will lead to problems when syncing).
        Right = rightItem;

        // Update right.
        var rightRight = rightItem.Right;
        if (rightRight.IsItem)
        {
            rightRight.Left = rightItem;
        }

        // Right is more specific.
        transaction._mergeStructs.Add(rightItem.Id);

        // Update parent._map.
        if (rightItem.ParentSub != null && rightItem.Right.IsNull)
        {
            (rightItem.Parent as AbstractType)._map[rightItem.ParentSub] = rightItem;
        }

        Length = diff;
        return rightItem;
    }
}
