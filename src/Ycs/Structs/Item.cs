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
    // Whether it is this item's predecessor by clock is in the run-head index: a writer of an item the store holds
    // calls StructStore.Refresh (Integrate); a split and a merge go through ClientStructs, which keeps it.
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
    public ID LastId => ItemRef.Of(this).LastId;

    /// <summary>
    /// Returns the next non-deleted item.
    /// </summary>
    public AbstractStruct Next => ItemRef.Of(this).Next.ToStruct();

    /// <summary>
    /// Returns the previous non-deleted item.
    /// </summary>
    public AbstractStruct Prev => ItemRef.Of(this).Prev.ToStruct();

    internal void SetStart(ID id) => Id = id;

    internal void SetLength(int length) => Length = length;

    // The run-head index holds whether an item is deleted: for an item the store holds, the caller then calls
    // StructStore.Refresh (ItemRef.MarkDeleted(Transaction), ContentDeleted.Integrate); a split's right part is not in it yet.
    internal void MarkDeleted()
    {
        _info |= InfoEnum.Deleted;
    }

    // The algorithms are ItemRef's (ItemRef.Operations.cs).
    internal override bool CanMergeWith(AbstractStruct right) => ItemRef.Of(this).CanMergeWith(ItemRef.Of(right));

    internal override bool MergeWith(AbstractStruct right) => ItemRef.Of(this).MergeWith(ItemRef.Of(right));

    internal override void Delete(Transaction transaction) => ItemRef.Of(this).Delete(transaction);

    internal override void Integrate(Transaction transaction, int offset) => ItemRef.Of(this).Integrate(transaction, offset);

    internal override void Write(IUpdateEncoder encoder, int offset) => ItemRef.Of(this).Write(encoder, offset);

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
            var left = store.GetItemCleanEnd(transaction, LeftOrigin.Value);
            Left = left.ToStruct();
            LeftOrigin = left.IsItem ? left.LastId : null;
        }

        if (RightOrigin != null)
        {
            var right = store.GetItemCleanStart(transaction, RightOrigin.Value);
            Right = right.ToStruct();
            RightOrigin = right.Id;
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
            Parent = parentItem.IsGC ? null : ((parentItem.IsItem ? parentItem.Content : null) as ContentType)?.Type;
        }
    }

    /// <summary>
    /// Split 'leftItem' into two items.
    /// </summary>
    public Item SplitItem(Transaction transaction, int diff) => ItemRef.Of(this).SplitItem(transaction, diff).ToItem();
}
