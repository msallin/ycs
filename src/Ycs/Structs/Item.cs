// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

using System.Diagnostics;

namespace Ycs;

internal partial class Item : AbstractStruct
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

        // Set once, on the right part of a merge and on an item a GC struct replaced: the struct is in no list, and
        // its Id and Length are the range it held (an ItemView's hint is stale).
        Detached = 1 << 7,
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
        internal set
        {
            SetId(ref _leftOrigin, InfoEnum.HasLeftOrigin, value);
            if (Handle >= 0)
            {
                WriteOrigins();
            }
        }
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
        internal set
        {
            SetId(ref _rightOrigin, InfoEnum.HasRightOrigin, value);
            if (Handle >= 0)
            {
                WriteOrigins();
            }
        }
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
        internal set
        {
            SetId(ref _redone, InfoEnum.HasRedone, value);
            WriteInfo();
        }
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

    private IContentEx _content;

    internal IContentEx Content
    {
        get => _content;
        set
        {
            _content = value;
            WriteInfo();
        }
    }

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

            WriteInfo();
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

            WriteInfo();
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

            WriteInfo();
        }
    }

    public override bool Deleted => _info.HasFlag(InfoEnum.Deleted);

    internal bool Detached => (_info & InfoEnum.Detached) != 0;

    internal void MarkDetached()
    {
        _info |= InfoEnum.Detached;
        FreeSlot();
    }

    /// <summary>
    /// Computes the last content address of this Item.
    /// </summary>
    public ID LastId => Length == 1 ? Id : new ID(Id.Client, Id.Clock + Length - 1);

    internal void SetStart(ID id) => Id = id;

    internal void SetLength(int length) => Length = length;

    // The run-head index holds whether an item is deleted: for an item the store holds, the caller then calls
    // StructStore.Refresh (ItemRef.MarkDeleted(Transaction), ContentDeleted.Integrate); a split's right part is not in it yet.
    internal void MarkDeleted()
    {
        _info |= InfoEnum.Deleted;
        WriteInfo();
    }

    // An item of the store is ItemRef's (ItemRef.Operations.cs). A decoded item (MergeUpdates, DiffUpdate, the conversions,
    // the rest store) never enters the store's walks: Write and the merge rules below run on its own fields.
    internal override bool CanMergeWith(AbstractStruct right) => false; // Yjs' Item#mergeWith needs this.right === right; a decoded item has no neighbours.

    internal override bool MergeWith(AbstractStruct right) => false;

    // The parent of a decoded item is a root's name or the id of its type's item.
    internal override void Write(IUpdateEncoder encoder, int offset)
    {
        string root = null;
        ID parentId = default;
        if (LeftOrigin == null && RightOrigin == null && offset == 0)
        {
            switch (Parent)
            {
                case string name:
                    root = name;
                    break;
                case ID id:
                    parentId = id;
                    break;
                default:
                    throw new InvalidOperationException("An item without origins has no parent.");
            }
        }

        WriteItem(encoder, offset, Id, LeftOrigin, RightOrigin, ParentSub, Content, root, parentId);
    }

    /// <summary>
    /// The item's encoding (Yjs' <c>Item#write</c>) from plain values. The parent is written only when neither origin
    /// is (<paramref name="offset"/> 0 and no origins): then <paramref name="parentRoot"/> names a root type, or, when
    /// it is null, <paramref name="parentId"/> is the id of the type's item.
    /// </summary>
    internal static void WriteItem(IUpdateEncoder encoder, int offset, ID id, ID? leftOrigin, ID? rightOrigin, string parentSub, IContentEx content, string parentRoot, ID parentId)
    {
        var origin = offset > 0 ? new ID(id.Client, id.Clock + offset - 1) : leftOrigin;
        var info = (content.Ref & Bits.Bits5) |
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
            if (parentRoot != null)
            {
                encoder.WriteParentInfo(true);
                encoder.WriteString(parentRoot);
            }
            else
            {
                encoder.WriteParentInfo(false);
                encoder.WriteLeftId(parentId);
            }

            if (parentSub != null)
            {
                encoder.WriteString(parentSub);
            }
        }

        content.Write(encoder, offset);
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
}
