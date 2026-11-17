// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

using System.Diagnostics;

namespace Ycs;

/// <summary>
/// An item as decoded from an update (and as the update operations read it): in no store, without neighbours. Its
/// parent is a root's name or the id of its type's item (<see cref="ItemRef.Adopt"/> resolves it); an item of a store
/// is a slot (<see cref="ItemRef"/>).
/// </summary>
internal sealed class Item : AbstractStruct
{
    // Whether _leftOrigin and _rightOrigin hold an id: an ID? field is 24 bytes, an ID 16.
    private bool _hasLeftOrigin;
    private bool _hasRightOrigin;
    private ID _leftOrigin;
    private ID _rightOrigin;

    internal Item(ID id, ID? leftOrigin, ID? rightOrigin, object parent, string parentSub, IContent content)
        : base(id, content.Length)
    {
        if (leftOrigin is { } left)
        {
            _leftOrigin = left;
            _hasLeftOrigin = true;
        }

        if (rightOrigin is { } right)
        {
            _rightOrigin = right;
            _hasRightOrigin = true;
        }

        Parent = parent;
        ParentSub = parentSub;
        Content = (IContentEx)content;
    }

    /// <summary>
    /// The item that was originally to the left of this item.
    /// </summary>
    public ID? LeftOrigin => _hasLeftOrigin ? _leftOrigin : null;

    /// <summary>
    /// The item that was originally to the right of this item.
    /// </summary>
    public ID? RightOrigin => _hasRightOrigin ? _rightOrigin : null;

    /// <summary>
    /// A root's name (string), the id of the type's item (<see cref="ID"/>), or null when an origin names the parent.
    /// </summary>
    public object Parent { get; }

    /// <summary>
    /// If the parent refers to this item with some kind of key (e.g. YMap).
    /// The key is then used to refer to the list in which to insert this item.
    /// If 'parentSub = null', type._start is the list in which to insert to.
    /// Otherwise, it is 'parent._map'.
    /// </summary>
    public string ParentSub { get; }

    internal IContentEx Content { get; }

    public bool Countable => Content.Countable;

    public override bool Deleted => false;

    // A decoded item (MergeUpdates, DiffUpdate, the conversions, the rest store) never enters the store's walks: Write and
    // the merge rules below run on its own fields; an item of the store is ItemRef's (ItemRef.Operations.cs).
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
        WriteItemHeader(encoder, content.Ref, offset, id, leftOrigin, rightOrigin, parentSub, parentRoot, parentId);
        content.Write(encoder, offset);
    }

    /// <summary>
    /// What <see cref="WriteItem"/> writes before the content: the info byte (<paramref name="contentRef"/>, the origins
    /// and the parentSub flag), the origins, and the parent when neither origin is written.
    /// </summary>
    internal static void WriteItemHeader(IUpdateEncoder encoder, int contentRef, int offset, ID id, ID? leftOrigin, ID? rightOrigin, string parentSub, string parentRoot, ID parentId)
    {
        var origin = offset > 0 ? new ID(id.Client, id.Clock + offset - 1) : leftOrigin;
        var info = (contentRef & Bits.Bits5) |
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
