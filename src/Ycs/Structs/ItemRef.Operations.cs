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

    /// <summary>The last content address of this struct.</summary>
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

    /// <summary>A new GC struct (not in a list) for the range <paramref name="length"/> from <paramref name="id"/>, in <paramref name="slab"/>.</summary>
    internal static ItemRef NewGC(Slab slab, ID id, int length)
    {
        var clientIndex = slab.Clients.GetOrAdd(id.Client);
        var gc = new ItemRef(slab, slab.Allocate());
        ref var slot = ref gc.Write();
        slot.Clock = id.Clock;
        slot.Client = clientIndex;
        slot.Length = length;
        slot.Info = Slot.DeletedFlag;
        return gc;
    }

    /// <summary>
    /// A new item (not integrated) in <paramref name="slab"/> between <paramref name="left"/> and <paramref name="right"/>,
    /// which are its origins (Yjs' <c>new Item(id, left, left &amp;&amp; left.lastId, right, right &amp;&amp; right.id, parent, parentSub, content)</c>).
    /// </summary>
    public static ItemRef New(Slab slab, ID id, ItemRef left, ItemRef right, AbstractType parent, string parentSub, IContent content) =>
        NewItem(slab, id, left, left.IsNull ? null : left.LastId, right, right.IsNull ? null : right.Id, parent, parentSub, (IContentEx)content);

    /// <summary>A new item whose slot holds the given links, origins, parent, key and content (nothing links to it yet).</summary>
    private static ItemRef NewItem(Slab slab, ID id, ItemRef left, ID? leftOrigin, ItemRef right, ID? rightOrigin, AbstractType parent, string parentSub, IContentEx content)
    {
        // Everything that can grow a table is done before the slot is taken: Allocate may move page 0 (see Slab.At).
        var clients = slab.Clients;
        var client = clients.GetOrAdd(id.Client);
        var originClient = leftOrigin is { } lo ? clients.GetOrAdd(lo.Client) : -1;
        var rightOriginClient = rightOrigin is { } ro ? clients.GetOrAdd(ro.Client) : -1;
        var parentIndex = parent?.TableIndex(slab.Types) ?? -1;
        var item = new ItemRef(slab, slab.Allocate());
        ref var slot = ref slab.Write(item._handle);
        slot.Clock = id.Clock;
        slot.Client = client;
        slot.Length = content.Length;
        slot.Info = (content.Countable ? Slot.CountableFlag : 0) | (content.Ref << 8);
        slot.OriginClient = originClient;
        slot.OriginClock = leftOrigin?.Clock ?? 0;
        slot.ROriginClient = rightOriginClient;
        slot.ROriginClock = rightOrigin?.Clock ?? 0;
        slot.Left = left.Handle;
        slot.Right = right.Handle;
        slot.Parent = parentIndex;
        slab.SetKey(item._handle, parentSub);
        slab.SetRef(item._handle, content);
        return item;
    }

    /// <summary>
    /// Where a decoded struct enters the store (the one place). <paramref name="decoded"/> is a struct of an update the
    /// integration plan has put in order, with everything it refers to in the store or planned before it. An item's
    /// references are resolved first, whether or not it is integrated, in the order of Yjs: the origins' structs are split
    /// where an origin points into one, and the parent is resolved (to nothing when a neighbour is a GC struct or the
    /// parent type is collected: the item then enters as a GC struct). Then, when <paramref name="integrate"/> is set, the
    /// struct gets a slot and is integrated from <paramref name="offset"/>, which is the part of its range the store holds
    /// already; a GC struct is trimmed by it. Without <paramref name="integrate"/> nothing enters the store. The decoded
    /// object is read, never changed. A <see cref="Skip"/> struct is never adopted: it throws.
    /// </summary>
    public static void Adopt(AbstractStruct decoded, Transaction transaction, int offset, bool integrate)
    {
        var store = transaction.Doc.Store;
        switch (decoded)
        {
            case Item item:
                var (left, leftOrigin, right, rightOrigin, parent, parentSub) = ResolveReferences(item, transaction, store);
                if (integrate)
                {
                    var adopted = NewItem(store.Slab, item.Id, left, leftOrigin, right, rightOrigin, parent, parentSub, item.Content);
                    var start = item.Id.Clock + offset;
                    try
                    {
                        adopted.IntegrateItem(transaction, offset);
                    }
                    catch when (store.GetState(item.Id.Client) <= start)
                    {
                        // Not in its list yet, and nothing links to it: the slot would live without a struct.
                        adopted.FreeSlot();
                        throw;
                    }
                }

                break;
            case GC gc:
                if (integrate)
                {
                    store.AddStruct(NewGC(store.Slab, new ID(gc.Id.Client, gc.Id.Clock + offset), gc.Length - offset));
                }

                break;
            default:
                throw new InvalidOperationException("Only items and GC structs are adopted; a Skip struct stands for a gap and never enters a store.");
        }
    }

    /// <summary>
    /// Finds the structs a decoded item refers to, once they are all there (the second half of Yjs' <c>Item.getMissing</c>):
    /// left and right are the store's structs, split where needed, the origins are the ids of what they ended up naming,
    /// and the parent is resolved to a type, or to null when the item is to be integrated as a GC struct.
    /// </summary>
    private static (ItemRef Left, ID? LeftOrigin, ItemRef Right, ID? RightOrigin, AbstractType Parent, string ParentSub) ResolveReferences(Item item, Transaction transaction, StructStore store)
    {
        var parent = item.Parent;
        var parentSub = item.ParentSub;
        var leftOrigin = item.LeftOrigin;
        var rightOrigin = item.RightOrigin;
        ItemRef left = default;
        ItemRef right = default;
        if (parent is string rootName)
        {
            parent = transaction.Doc.Get<AbstractType>(rootName);
        }

        if (leftOrigin != null)
        {
            left = store.GetItemCleanEnd(transaction, leftOrigin.Value);
            leftOrigin = left.IsItem ? left.LastId : null;
        }

        if (rightOrigin != null)
        {
            right = store.GetItemCleanStart(transaction, rightOrigin.Value);
            rightOrigin = right.Id;
        }

        if (left.IsGC || right.IsGC)
        {
            parent = null;
        }
        else if (parent == null)
        {
            // Only set parent if this shouldn't be garbage collected; the left neighbour first, as Yjs.
            if (left.IsItem)
            {
                parent = left.Parent;
                parentSub = left.ParentSub;
            }
            else if (right.IsItem)
            {
                parent = right.Parent;
                parentSub = right.ParentSub;
            }
        }
        else if (parent is ID pid)
        {
            var parentItem = store.Find(pid);
            parent = parentItem.IsGC ? null : ((parentItem.IsItem ? parentItem.Content : null) as ContentType)?.Type;
        }

        return (left, leftOrigin, right, rightOrigin, parent as AbstractType, parentSub);
    }

    public bool CanMergeWith(ItemRef right) => IsItem ? CanMergeItemWith(right) : right.IsGC;

    public bool MergeWith(ItemRef right)
    {
        if (IsItem)
        {
            return MergeItemWith(right);
        }

        if (!right.IsGC)
        {
            return false;
        }

        Length += right.Length;
        right.FreeSlot();
        return true;
    }

    public void Write(IUpdateEncoder encoder, int offset)
    {
        if (IsItem)
        {
            WriteItem(encoder, offset);
        }
        else
        {
            encoder.WriteInfo(GC.StructGCRefNumber);
            encoder.WriteLength(Length - offset);
        }
    }

    // The run-head index holds whether an item is deleted: for an item the store holds, the caller then calls
    // StructStore.Refresh (MarkDeleted(Transaction), ContentDeleted.Integrate); a split's right part is not in it yet.
    public void MarkDeleted() => Write().Info |= Slot.DeletedFlag;

    /// <summary>
    /// Whether <paramref name="right"/> merges into this item (Yjs' <c>Item#mergeWith</c> conditions; a content of a
    /// mergeable kind always merges with one of its kind).
    /// </summary>
    /// https://github.com/yjs/yjs/blob/c2f0ca3faef731a93ba28f286ffafe0e1ea1a3aa/src/structs/Item.js#L557
    private bool CanMergeItemWith(ItemRef right)
    {
        // No allocation here: both slots stay valid. The origins and the client compare by index, which names one client
        // in the slab, and clock; no origin is -1.
        if (right.IsNull)
        {
            return false;
        }

        ref readonly var l = ref Read();
        ref readonly var r = ref right.Peek();
        return r.Kind != 0 &&
            l.Kind == r.Kind &&
            r.OriginClient == l.Client &&
            r.OriginClock == l.Clock + l.Length - 1 &&
            l.Right == right._handle &&
            r.ROriginClient == l.ROriginClient &&
            (l.ROriginClient < 0 || r.ROriginClock == l.ROriginClock) &&
            l.Client == r.Client &&
            l.Clock + l.Length == r.Clock &&
            ((l.Info ^ r.Info) & Slot.DeletedFlag) == 0 &&
            ((l.Info | r.Info) & Slot.HasRedoneFlag) == 0 &&
            Content.GetType() == right.Content.GetType() &&
            Content.Mergeable;
    }

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

        var parent = Parent;
        parent?.Doc?.Store.NoteDeparture();
        (parent as YArrayBase)?.OnMerge(this, right);

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
        right.FreeSlot();
        return true;
    }

    /// <summary>
    /// Frees the slot of a struct that left every list (merged into its left neighbour): the reference and every copy of
    /// it are stale from here on, and the slot is reused after the outermost batch (<see cref="Slab.AllowReuse"/>).
    /// </summary>
    public void FreeSlot()
    {
        Check();
        _slab.Free(_handle);
    }

    /// <summary>
    /// Makes this struct a GC struct of the range it held, in its slot: what it held in objects and links is dropped.
    /// The references to it stay valid and read a GC struct.
    /// </summary>
    internal void BecomeGC()
    {
        ref var slot = ref Write();
        slot = new Slot
        {
            Clock = slot.Clock,
            Client = slot.Client,
            Length = slot.Length,
            Info = Slot.DeletedFlag,
            OriginClient = -1,
            ROriginClient = -1,
            Left = -1,
            Right = -1,
            Parent = -1,
        };
        _slab.SetRef(_handle, null);
        _slab.SetKey(_handle, null);
        _slab.SetRedone(_handle, null);
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
        if (!(transaction.Local
            && transaction.Doc._transactionCleanups.Count == 1
            && !transaction.Doc.RunExtensionsDisabledForTests
            && !parent.IsObserved(transaction)
            && !transaction.Doc.HasBeforeObserverCallsListeners
            && Content.GetType() == kind))
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

        var parent = Parent;
        if (Countable && ParentSub == null)
        {
            Debug.Assert(parent != null);
            parent.Length -= Length;
        }

        if (ParentSub != null && parent.TryGetMapItem(ParentSub, out var current) && current == this)
        {
            parent._mapCount--;
        }

        MarkDeleted();
        transaction.Doc.Store.Refresh(this);
        transaction.DeleteSet.Add(Id.Client, Id.Clock, Length);
        transaction.AddChangedTypeToTransaction(parent, ParentSub);
        return true;
    }

    private ItemRef IntegrateItem(Transaction transaction, int offset)
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

        // Rule: no ref Slot is held across a call that can allocate a slot (a split, ResolveConflict's Find does not) or
        // grow the slab; neighbours are read through the slab by handle, one ref at a time.
        var parent = Parent;
        if (parent != null)
        {
            var slab = _slab;
            AbstractType mapParent = null;
            var previousCounted = false;
            var parentSub = ParentSub;
            int leftHandle;
            int rightHandle;
            {
                ref readonly var self = ref Read();
                leftHandle = self.Left;
                rightHandle = self.Right;
            }

            bool conflicts;
            if (leftHandle < 0)
            {
                if (rightHandle < 0)
                {
                    conflicts = true;
                }
                else
                {
                    ref readonly var r = ref slab.Read(rightHandle);
                    conflicts = r.Kind != 0 && r.Left >= 0;
                }
            }
            else
            {
                ref readonly var l = ref slab.Read(leftHandle);
                conflicts = (l.Kind != 0 ? l.Right : -1) != rightHandle;
            }

            if (conflicts)
            {
                var left = leftHandle < 0 ? default : new ItemRef(slab, leftHandle);
                ItemRef o;

                // Set 'o' to the first conflicting item.
                if (!left.IsNull)
                {
                    o = left.IsItem ? left.Right : default;
                }
                else if (parentSub != null)
                {
                    parent.TryGetMapItem(parentSub, out var item);
                    o = item;

                    while (!o.IsNull && o.IsItem && !o.Left.IsNull)
                    {
                        o = o.Left;
                    }
                }
                else
                {
                    o = parent._start;
                }

                leftHandle = transaction.Doc.Store.ResolveConflict(this, left, o).Handle;
                Write().Left = leftHandle;
            }

            // Reconnect left/right + update parent map/start if necessary.
            if (leftHandle >= 0)
            {
                ref var l = ref slab.Write(leftHandle);
                if (l.Kind != 0)
                {
                    rightHandle = l.Right;
                    l.Right = _handle;
                }
                else
                {
                    rightHandle = -1;
                }

                Write().Right = rightHandle;
            }
            else
            {
                ItemRef r;

                if (parentSub != null)
                {
                    parent.TryGetMapItem(parentSub, out var item);
                    r = item;

                    while (!r.IsNull && r.IsItem && !r.Left.IsNull)
                    {
                        r = r.Left;
                    }
                }
                else
                {
                    r = parent._start;
                    parent._start = this;
                }

                rightHandle = r.Handle;
                Write().Right = rightHandle;
            }

            if (rightHandle >= 0)
            {
                ref var rightSlot = ref slab.Write(rightHandle);
                if (rightSlot.Kind != 0)
                {
                    // Its left changes: when the old one is its predecessor by clock, it was linked in the run-head index and
                    // becomes a break, which StructStore.Refresh records.
                    var relinked = false;
                    if (rightSlot.Left >= 0)
                    {
                        ref readonly var oldLeft = ref slab.Read(rightSlot.Left);
                        relinked = oldLeft.Kind != 0 && oldLeft.Client == rightSlot.Client && oldLeft.Clock + oldLeft.Length == rightSlot.Clock;
                    }

                    rightSlot.Left = _handle;
                    if (relinked)
                    {
                        transaction.Doc.Store.Refresh(new ItemRef(slab, rightHandle));
                    }
                }
            }
            else if (parentSub != null)
            {
                // Set as current parent value if right == null and this is parentSub.
                mapParent = parent;
                previousCounted = mapParent.TryGetMapItem(parentSub, out var previous) && !previous.Deleted;
                mapParent._map[parentSub] = Hold;

                // This is the current attribute value of parent. Delete right.
                if (leftHandle >= 0)
                {
                    new ItemRef(slab, leftHandle).Delete(transaction);
                }
            }

            // Adjust length of parent.
            if (parentSub == null)
            {
                ref readonly var self = ref Read();
                if ((self.Info & (Slot.CountableFlag | Slot.DeletedFlag)) == Slot.CountableFlag)
                {
                    parent.Length += self.Length;
                }
            }

            transaction.Doc.Store.AddStruct(this);
            Content.Integrate(transaction, this);

            // Counted once its content is integrated: deleted content marks the item deleted.
            if (mapParent != null)
            {
                mapParent._mapCount += (Deleted ? 0 : 1) - (previousCounted ? 1 : 0);
            }

            // Add parent to transaction.changed.
            transaction.AddChangedTypeToTransaction(parent, parentSub);

            if (parent.ItemDeleted || (parentSub != null && !Right.IsNull))
            {
                // Delete if parent is deleted or if this is not the current attribute value of parent.
                Delete(transaction);
            }
        }
        else
        {
            // Parent is not defined. Integrate a GC struct instead.
            BecomeGC();
            transaction.Doc.Store.AddStruct(this);
        }

        return this;
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
            store.ReplaceWithGC(this);
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
            Parent?.KeepItemAndParents(value);
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
            var parent = Parent ?? throw new InvalidOperationException("An item without origins has no parent.");
            if (parent.IsRoot)
            {
                root = parent.FindRootTypeKey();
            }
            else
            {
                parentId = parent.ItemId;
            }
        }

        Item.WriteItem(encoder, offset, Id, LeftOrigin, RightOrigin, ParentSub, Content, root, parentId);
    }

    /// <summary>
    /// Split 'leftItem' into two items.
    /// </summary>
    public ItemRef SplitItem(Transaction transaction, int diff)
    {
        var content = (IContentEx)Content.Splice(diff);
        var parent = Parent;
        var parentSub = ParentSub;
        var parentIndex = parent?.TableIndex(_slab.Types) ?? -1;
        var handle = _slab.Allocate();
        var rightItem = new ItemRef(_slab, handle);
        _slab.SetKey(handle, parentSub);
        _slab.SetRef(handle, content);

        // Rule: both slots are taken here, after the method's only allocation, and no call below allocates.
        ref var left = ref Write();
        ref var right = ref _slab.Write(handle);
        var clock = left.Clock + diff;
        right.Clock = clock;
        right.Client = left.Client;
        right.Length = content.Length;
        right.Info = (content.Countable ? Slot.CountableFlag : 0) | (content.Ref << 8) | (left.Info & (Slot.DeletedFlag | Slot.KeepFlag));
        right.OriginClient = left.Client;
        right.OriginClock = clock - 1;
        right.ROriginClient = left.ROriginClient;
        right.ROriginClock = left.ROriginClock;
        right.Left = _handle;
        right.Right = left.Right;
        right.Parent = parentIndex;

        if ((left.Info & Slot.HasRedoneFlag) != 0)
        {
            var redone = _slab.GetRedone(_handle);
            right.Info |= Slot.HasRedoneFlag;
            _slab.SetRedone(handle, new ID(redone.Client, redone.Clock + diff));
        }

        // Update left (do not set leftItem.RightOrigin as it will lead to problems when syncing).
        var rightRight = right.Right;
        left.Right = handle;
        left.Length = diff;

        // Update right.
        if (rightRight >= 0)
        {
            ref var next = ref _slab.Write(rightRight);
            if (next.Kind != 0)
            {
                next.Left = handle;
            }
        }

        // Right is more specific.
        transaction._mergeStructs.Add(new ID(_slab.Clients[right.Client], clock));

        // Update parent._map.
        if (parentSub != null && rightRight < 0)
        {
            parent._map[parentSub] = rightItem.Hold;
        }

        return rightItem;
    }
}
