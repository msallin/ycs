// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

using System.Diagnostics;

namespace Ycs;

public partial class Item : AbstractStruct
{
    [Flags]
    private enum InfoEnum : int
    {
        Keep = 1 << 0,
        Countable = 1 << 1,
        Deleted = 1 << 2,
        Marker = 1 << 3,

        // Whether _leftOrigin, _rightOrigin, _redone hold an id: an ID? field is 24 bytes, an ID 16.
        HasLeftOrigin = 1 << 4,
        HasRightOrigin = 1 << 5,
        HasRedone = 1 << 6,
    }

    private InfoEnum _info;
    private ID _leftOrigin;
    private ID _rightOrigin;
    private ID _redone;

    internal Item(ID id, AbstractStruct left, ID? leftOrigin, AbstractStruct right, ID? rightOrigin, object parent, string parentSub, IContent content)
        : base(id, content.Length)
    {
        _info = content.Countable ? InfoEnum.Countable : 0;
        LeftOrigin = leftOrigin;
        Left = left;
        Right = right;
        RightOrigin = rightOrigin;
        Parent = parent;
        ParentSub = parentSub;
        Redone = null;
        Content = (IContentEx)content;
    }

    /// <summary>
    /// The item that was originally to the left of this item.
    /// </summary>
    public ID? LeftOrigin
    {
        get => (_info & InfoEnum.HasLeftOrigin) != 0 ? _leftOrigin : null;
        internal set => SetId(ref _leftOrigin, InfoEnum.HasLeftOrigin, value);
    }

    /// <summary>
    /// The item that is currently to the left of this item.
    /// </summary>
    public AbstractStruct Left { get; internal set; }

    /// <summary>
    /// The item that was originally to the right of this item.
    /// </summary>
    public ID? RightOrigin
    {
        get => (_info & InfoEnum.HasRightOrigin) != 0 ? _rightOrigin : null;
        internal set => SetId(ref _rightOrigin, InfoEnum.HasRightOrigin, value);
    }

    /// <summary>
    /// The item that is currently to the right of this item.
    /// </summary>
    public AbstractStruct Right { get; internal set; }

    /// <summary>
    /// AbstractType or ID.
    /// </summary>
    public object Parent { get; internal set; }

    /// <summary>
    /// If the parent refers to this item with some kind of key (e.g. YMap).
    /// The key is then used to refer to the list in which to insert this item.
    /// If 'parentSub = null', type._start is the list in which to insert to.
    /// Otherwise, it is 'parent._map'.
    /// </summary>
    public string ParentSub { get; internal set; }

    /// <summary>
    /// Refers to the type that undid this operation.
    /// </summary>
    public ID? Redone
    {
        get => (_info & InfoEnum.HasRedone) != 0 ? _redone : null;
        internal set => SetId(ref _redone, InfoEnum.HasRedone, value);
    }

    private void SetId(ref ID field, InfoEnum flag, ID? value)
    {
        if (value is ID id)
        {
            field = id;
            _info |= flag;
        }
        else
        {
            field = default;
            _info &= ~flag;
        }
    }

    internal IContentEx Content { get; set; }

    public bool Marker
    {
        get => _info.HasFlag(InfoEnum.Marker);
        internal set
        {
            if (value)
            {
                _info |= InfoEnum.Marker;
            }
            else
            {
                _info &= ~InfoEnum.Marker;
            }
        }
    }

    /// <summary>
    /// If set to 'true', do not garbage collect this item.
    /// </summary>
    public bool Keep
    {
        get => _info.HasFlag(InfoEnum.Keep);
        internal set
        {
            if (value)
            {
                _info |= InfoEnum.Keep;
            }
            else
            {
                _info &= ~InfoEnum.Keep;
            }
        }
    }

    public bool Countable
    {
        get => _info.HasFlag(InfoEnum.Countable);
        internal set
        {
            if (value)
            {
                _info |= InfoEnum.Countable;
            }
            else
            {
                _info &= ~InfoEnum.Countable;
            }
        }
    }

    public override bool Deleted => _info.HasFlag(InfoEnum.Deleted);

    /// <summary>
    /// Computes the last content address of this Item.
    /// </summary>
    public ID LastId => Length == 1 ? Id : new ID(Id.Client, Id.Clock + Length - 1);

    /// <summary>
    /// Returns the next non-deleted item.
    /// </summary>
    public AbstractStruct Next
    {
        get
        {
            var n = Right;
            while (n != null && n.Deleted)
            {
                n = (n as Item)?.Right;
            }
            return n;
        }
    }

    /// <summary>
    /// Returns the previous non-deleted item.
    /// </summary>
    public AbstractStruct Prev
    {
        get
        {
            var n = Left;
            while (n != null && n.Deleted)
            {
                n = (n as Item)?.Left;
            }
            return n;
        }
    }

    internal void MarkDeleted()
    {
        _info |= InfoEnum.Deleted;
    }

    /// <summary>
    /// Whether <paramref name="right"/> merges into this item (Yjs' <c>Item#mergeWith</c> conditions; a content of a
    /// mergeable kind always merges with one of its kind).
    /// </summary>
    /// https://github.com/yjs/yjs/blob/c2f0ca3faef731a93ba28f286ffafe0e1ea1a3aa/src/structs/Item.js#L557
    internal override bool CanMergeWith(AbstractStruct right) =>
        right is Item rightItem &&
        ID.Equals(rightItem.LeftOrigin, LastId) &&
        Right == right &&
        ID.Equals(rightItem.RightOrigin, RightOrigin) &&
        Id.Client == right.Id.Client &&
        Id.Clock + Length == right.Id.Clock &&
        Deleted == right.Deleted &&
        Redone == null &&
        rightItem.Redone == null &&
        Content.GetType() == rightItem.Content.GetType() &&
        Content.Mergeable;

    /// <summary>Try to merge two items.</summary>
    internal override bool MergeWith(AbstractStruct right)
    {
        if (!CanMergeWith(right))
        {
            return false;
        }

        var rightItem = (Item)right;
        if (!Content.MergeWith(rightItem.Content))
        {
            throw new InvalidOperationException(
                $"The content of {right.Id.Client}:{right.Id.Clock} did not merge into its left neighbour's although its kind merges (a bug of " +
                "Ycs). Nothing was changed.");
        }

        (Parent as YArrayBase)?.OnMerge(this, rightItem);

        if (rightItem.Keep)
        {
            Keep = true;
        }

        Right = rightItem.Right;
        if (Right is Item)
        {
            (Right as Item).Left = this;
        }

        Length += rightItem.Length;
        return true;
    }

    /// <summary>
    /// Whether an insert of <paramref name="kind"/> content into <paramref name="parent"/> between this item and
    /// <paramref name="right"/>, as the next struct of <paramref name="client"/>, can continue this item in place: the
    /// new item would merge into this one at the end of the transaction (<see cref="CanMergeWith"/>: it would be this
    /// item's continuation by clock and origin, with this right origin, undeleted, not redone), this item is not kept,
    /// does not end with the first half of a surrogate pair (splitting there later would replace it), the parent
    /// is not deleted (the new item would be deleted at once), nobody observes the parent, and the transaction is
    /// the only one of its batch.
    /// </summary>
    internal bool CanExtendRun(Transaction transaction, AbstractType parent, Item right, Type kind, long client)
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
            && ID.Equals(RightOrigin, right?.Id)
            && !Deleted
            && !Keep
            && Redone == null
            && (parent._item == null || !parent._item.Deleted)
            && !(Content is ContentString str && str.EndsWithHighSurrogate);
    }

    /// <summary>
    /// Continues this item in place by <paramref name="length"/> new elements its content took in already (see
    /// <see cref="CanExtendRun"/>): what integrating them as an item of their own after it would do (the parent's
    /// length, the client's clock, the transaction's changed types), and the transaction records where that item
    /// would have started.
    /// </summary>
    internal void ExtendRun(Transaction transaction, int length)
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
    internal override void Delete(Transaction transaction)
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
    internal bool MarkDeleted(Transaction transaction)
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
        transaction.DeleteSet.Add(Id.Client, Id.Clock, Length);
        transaction.AddChangedTypeToTransaction(parent, ParentSub);
        return true;
    }

    internal override void Integrate(Transaction transaction, int offset)
    {
        if (offset > 0)
        {
            Id = new ID(Id.Client, Id.Clock + offset);
            Left = transaction.Doc.Store.GetItemCleanEnd(transaction, new ID(Id.Client, Id.Clock - 1));
            if (Left is GC && Parent != null)
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

            LeftOrigin = (Left as Item)?.LastId;
            Content = (IContentEx)Content.Splice(offset);
            Length -= offset;
        }

        if (Parent != null)
        {
            AbstractType mapParent = null;
            var previousCounted = false;

            if ((Left == null && (Right == null || (Right as Item)?.Left != null)) || (Left != null && (Left as Item)?.Right != Right))
            {
                var left = Left;
                AbstractStruct o;

                // Set 'o' to the first conflicting item.
                if (left != null)
                {
                    o = (left as Item)?.Right;
                }
                else if (ParentSub != null)
                {
                    Debug.Assert(Parent is AbstractType);

                    Item item = null;
                    (Parent as AbstractType)?._map?.TryGetValue(ParentSub, out item);
                    o = item;

                    while (o != null && (o as Item)?.Left != null)
                    {
                        o = (o as Item).Left;
                    }
                }
                else
                {
                    Debug.Assert(Parent is AbstractType);
                    o = (Parent as AbstractType)?._start;
                }

                Left = transaction.Doc.Store.ResolveConflict(this, left, o);
            }

            // Reconnect left/right + update parent map/start if necessary.
            if (Left != null)
            {
                if (Left is Item leftItem)
                {
                    var right = leftItem.Right;
                    Right = right;
                    leftItem.Right = this;
                }
                else
                {
                    Right = null;
                }
            }
            else
            {
                AbstractStruct r;

                if (ParentSub != null)
                {
                    Item item = null;
                    (Parent as AbstractType)?._map?.TryGetValue(ParentSub, out item);
                    r = item;

                    while (r != null && (r as Item)?.Left != null)
                    {
                        r = (r as Item).Left;
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
                        r = null;
                    }
                }

                Right = r;
            }

            if (Right != null)
            {
                if (Right is Item rightItem)
                {
                    rightItem.Left = this;
                }
            }
            else if (ParentSub != null)
            {
                // Set as current parent value if right == null and this is parentSub.
                mapParent = (AbstractType)Parent;
                previousCounted = mapParent._map.TryGetValue(ParentSub, out var previous) && !previous.Deleted;
                mapParent._map[ParentSub] = this;
                // This is the current attribute value of parent. Delete right.
                Left?.Delete(transaction);
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

            if (((Parent as AbstractType)?._item != null && (Parent as AbstractType)._item.Deleted) || (ParentSub != null && Right != null))
            {
                // Delete if parent is deleted or if this is not the current attribute value of parent.
                Delete(transaction);
            }
        }
        else
        {
            // Parent is not defined. Integrate GC struct instead.
            new GC(Id, Length).Integrate(transaction, 0);
        }
    }

    /// <summary>
    /// The client of a struct this item refers to that <paramref name="getState"/> does not cover
    /// yet, or null (the check half of Yjs' <c>Item.getMissing</c>). References to the item's own
    /// client are earlier clocks (checked when it was read), so they are always there.
    /// </summary>
    internal long? FindMissing(Func<long, long> getState)
    {
        if (LeftOrigin is ID left && left.Client != Id.Client && left.Clock >= getState(left.Client))
        {
            return left.Client;
        }

        if (RightOrigin is ID right && right.Client != Id.Client && right.Clock >= getState(right.Client))
        {
            return right.Client;
        }

        if (Parent is ID parentId && parentId.Client != Id.Client && parentId.Clock >= getState(parentId.Client))
        {
            return parentId.Client;
        }

        return null;
    }

    /// <summary>
    /// Finds the items this item refers to, once they are all there (the second half of Yjs'
    /// <c>Item.getMissing</c>): left and right are set, splitting items where needed, and the parent
    /// is resolved to a type, or to null when this item is to be integrated as a GC struct.
    /// </summary>
    internal void ResolveReferences(Transaction transaction, StructStore store)
    {
        if (Parent is string rootName)
        {
            Parent = transaction.Doc.Get<AbstractType>(rootName);
        }

        if (LeftOrigin != null)
        {
            Left = store.GetItemCleanEnd(transaction, LeftOrigin.Value);
            LeftOrigin = (Left as Item)?.LastId;
        }

        if (RightOrigin != null)
        {
            Right = store.GetItemCleanStart(transaction, RightOrigin.Value);
            RightOrigin = Right.Id;
        }

        if (Left is GC || Right is GC)
        {
            Parent = null;
        }
        else if (Parent == null)
        {
            // Only set parent if this shouldn't be garbage collected; the left neighbour first, as Yjs.
            if (Left is Item leftItem)
            {
                Parent = leftItem.Parent;
                ParentSub = leftItem.ParentSub;
            }
            else if (Right is Item rightItem)
            {
                Parent = rightItem.Parent;
                ParentSub = rightItem.ParentSub;
            }
        }
        else if (Parent is ID pid)
        {
            var parentItem = store.Find(pid);
            Parent = parentItem is GC ? null : ((parentItem as Item)?.Content as ContentType)?.Type;
        }
    }

    internal void Gc(StructStore store, bool parentGCd)
    {
        if (!Deleted)
        {
            throw new InvalidOperationException();
        }

        Content.Gc(store);

        if (parentGCd)
        {
            store.ReplaceStruct(this, new GC(Id, Length));
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
    internal void KeepItemAndParents(bool value)
    {
        var item = this;

        while (item != null && item.Keep != value)
        {
            item.Keep = value;
            item = (item.Parent as AbstractType)?._item;
        }
    }

    internal bool IsVisible(Snapshot snap)
    {
        return snap == null
            ? !Deleted
            : snap.StateVector.TryGetValue(Id.Client, out long value) && value > Id.Clock && !snap.DeleteSet.IsDeleted(Id);
    }

    internal override void Write(IUpdateEncoder encoder, int offset)
    {
        var origin = offset > 0 ? new ID(Id.Client, Id.Clock + offset - 1) : LeftOrigin;
        var rightOrigin = RightOrigin;
        var parentSub = ParentSub;
        var info = (Content.Ref & Bits.Bits5) |
            (origin == null ? 0 : Bit.Bit8) |
            (rightOrigin == null ? 0 : Bit.Bit7) |
            (parentSub == null ? 0 : Bit.Bit6);
        encoder.WriteInfo((byte)info);

        if (origin != null)
        {
            encoder.WriteLeftId(origin.Value);
        }

        if (rightOrigin != null)
        {
            encoder.WriteRightId(rightOrigin.Value);
        }

        if (origin == null && rightOrigin == null)
        {
            switch (Parent)
            {
                case string yKey:
                    // Not integrated (pending, or read by UpdateOperations): a root type by name.
                    encoder.WriteParentInfo(true);
                    encoder.WriteString(yKey);
                    break;
                case ID parentId:
                    // Not integrated: the id of the parent type's item.
                    encoder.WriteParentInfo(false);
                    encoder.WriteLeftId(parentId);
                    break;
                case AbstractType { _item: null } root:
                    encoder.WriteParentInfo(true);
                    encoder.WriteString(root.FindRootTypeKey());
                    break;
                case AbstractType type:
                    encoder.WriteParentInfo(false);
                    encoder.WriteLeftId(type._item.Id);
                    break;
                default:
                    throw new InvalidOperationException("An item without origins has no parent.");
            }

            if (parentSub != null)
            {
                encoder.WriteString(parentSub);
            }
        }

        Content.Write(encoder, offset);
    }

    /// <summary>
    /// Split 'leftItem' into two items.
    /// </summary>
    public Item SplitItem(Transaction transaction, int diff)
    {
        var client = Id.Client;
        var clock = Id.Clock;

        var rightItem = new Item(
            new ID(client, clock + diff),
            this,
            new ID(client, clock + diff - 1),
            Right,
            RightOrigin,
            Parent,
            ParentSub,
            Content.Splice(diff));

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
        if (rightItem.Right is Item rightIt)
        {
            rightIt.Left = rightItem;
        }

        // Right is more specific.
        transaction._mergeStructs.Add(rightItem);

        // Update parent._map.
        if (rightItem.ParentSub != null && rightItem.Right == null)
        {
            (rightItem.Parent as AbstractType)._map[rightItem.ParentSub] = rightItem;
        }

        Length = diff;
        return rightItem;
    }
}
