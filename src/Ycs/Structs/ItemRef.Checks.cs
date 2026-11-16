#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

// What the store's checks read of the mirror: the object, not the slot the reads go through.
internal readonly partial struct ItemRef
{
    internal bool HasSlot => _struct.Handle >= 0;

    internal ID ObjectId => _struct.Id;

    internal ItemRef ObjectLeft => new(((Item)_struct).Left);

    internal ItemRef ObjectRight => new(((Item)_struct).Right);

    /// <summary>
    /// What is wrong with the slot of this struct, or null: its handle is not a live slot of <paramref name="store"/>'s
    /// slab, or the slot differs from the object in clock, client, length, origins, info (the flags and the content's
    /// kind), content object (by reference), key, owner, the handles of its left and right neighbours, or its parent
    /// (the index of the parent type in the type table, whose entry is that type until it is collected).
    /// </summary>
    internal string SlotViolation(StructStore store)
    {
        // The object, not the reference's reads: the slot is what is compared with it.
        var s = _struct;
        var item = s as Item;
        if (SlotStore != store || Handle < 0)
        {
            return $"{s.Id.Client}:{s.Id.Clock} has no slot in this store (handle {Handle})";
        }

        var slot = store.Slab.At(Handle);
        if (slot.IsFree)
        {
            return $"{s.Id.Client}:{s.Id.Clock} names a free slot";
        }

        if (!ReferenceEquals(store.Slab.GetRef(Handle), item?.Content))
        {
            return $"the slot of {s.Id.Client}:{s.Id.Clock} holds another content object than the struct";
        }

        if (!ReferenceEquals(store.Slab.GetKey(Handle), item?.ParentSub))
        {
            return $"the slot of {s.Id.Client}:{s.Id.Clock} holds another key than the struct";
        }

        if (!ReferenceEquals(store.Slab.ReadOwner(Handle), s))
        {
            return $"the slot of {s.Id.Client}:{s.Id.Clock} is owned by another struct";
        }

        var left = item?.LeftOrigin;
        var right = item?.RightOrigin;
        var info = (item != null ? (item.Keep ? 1 : 0) | (item.Countable ? 2 : 0) | (item.Marker ? 8 : 0) | (item.Redone != null ? 64 : 0) | item.Content.Ref << 8 : 0) | (s.Deleted ? 4 : 0);
        if (slot.Clock != s.Id.Clock || !store.ClientTable.TryGetIndex(s.Id.Client, out var client) || slot.Client != client || slot.Length != s.Length)
        {
            return $"the slot of {s.Id.Client}:{s.Id.Clock} differs in id or length";
        }

        if (!store.SlotOrigin(left, slot.OriginClient, slot.OriginClock) || !store.SlotOrigin(right, slot.ROriginClient, slot.ROriginClock))
        {
            return $"the slot of {s.Id.Client}:{s.Id.Clock} differs in an origin";
        }

        if (slot.Info != info)
        {
            return $"the slot of {s.Id.Client}:{s.Id.Clock} holds info {slot.Info}, the struct {info}";
        }

        var leftHandle = item?.Left?.Handle ?? -1;
        var rightHandle = item?.Right?.Handle ?? -1;
        if (slot.Left != leftHandle || slot.Right != rightHandle)
        {
            return $"the slot of {s.Id.Client}:{s.Id.Clock} links {slot.Left} and {slot.Right}, the struct {leftHandle} and {rightHandle}";
        }

        return store.ParentViolation(s.Id, slot.Parent, item?.Parent as AbstractType);
    }
}
