#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

internal sealed partial class StructStore
{
    /// <summary>
    /// What is wrong with the links and holders around the structs of one client, or null: left and right mirror each
    /// other; the type of a type item names the item and the subdocument of a document item names an attached item (a document inserted twice belongs to the item integrated last); and, for every type an
    /// item names as its parent or holds, no holder points at a detached struct (one that is no longer in its client's
    /// list: merged into its left neighbour or replaced by a GC struct). The holders are the type's start, its map
    /// values, its push hint and its search markers. A collected type's item is excluded: it names the GC struct that
    /// replaced it. Only types that an item of the store reaches are visited.
    /// </summary>
    private HashSet<AbstractType> _typesSeen;
    private List<AbstractType> _typeList;

    private string ConsistencyViolation(ClientStructs structs, ref HashSet<AbstractType> visited)
    {
        foreach (var s in structs)
        {
            if (!s.IsItem)
            {
                continue;
            }

            var left = s.Left;
            if (!left.IsNull && (!IsAttached(left) || !left.IsItem || left.Right != s))
            {
                return $"the left of {s.Id.Client}:{s.Id.Clock} is detached or does not point back";
            }

            var right = s.Right;
            if (!right.IsNull && (!IsAttached(right) || !right.IsItem || right.Left != s))
            {
                return $"the right of {s.Id.Client}:{s.Id.Clock} is detached or does not point back";
            }

            if (s.Content is ContentType content && content.Type._item != s)
            {
                return $"the type of {s.Id.Client}:{s.Id.Clock} names another item";
            }

            if (s.Content is ContentDoc doc && doc.Doc._item != s && !IsAttachedInOwner(doc.Doc._item))
            {
                return $"the subdocument of {s.Id.Client}:{s.Id.Clock} names a detached item";
            }

            visited ??= [];
            var type = s.Content is ContentType held ? held.Type : s.Parent as AbstractType;
            if (type != null && visited.Add(type))
            {
                _typesSeen ??= [];
                if (_typesSeen.Add(type))
                {
                    (_typeList ??= []).Add(type);
                }

                if (TypeViolation(type) is { } violation)
                {
                    return $"{violation} (reached from {s.Id.Client}:{s.Id.Clock})";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// What is wrong with the types seen earlier that have been collected since, or null: no item reaches a collected
    /// type, so its list start, map values, push hint and search markers are checked from the type (an insert into it
    /// after the collection links deleted items into its list, as in Yjs). A type is seen when an item reaches it; the
    /// check runs every count/256th time over more than 1,024 types.
    /// </summary>
    private string CollectedTypeViolation()
    {
        if (_typeList == null || (_typeList.Count > 1024 && _checks % (_typeList.Count / 256) != 0))
        {
            return null;
        }

        foreach (var type in _typeList)
        {
            if (!type.IsCollected)
            {
                continue;
            }

            if (TypeViolation(type) is { } violation)
            {
                return $"a collected type: {violation}";
            }
        }

        return null;
    }

    private string TypeViolation(AbstractType type)
    {
        if (!type.IsCollected && !type._item.IsNull && !IsAttached(type._item))
        {
            return "the item of a live type is detached";
        }

        var start = type._start;
        if (!start.IsNull && (!IsAttached(start) || !start.Left.IsNull))
        {
            return "the start of a type is detached or has a left";
        }

        if (type._map.Count <= 1024 || _checks % (type._map.Count / 256) == 0)
        {
            foreach (var (key, value) in type._map)
            {
                if (!IsAttached(value) || !value.Right.IsNull || value.ParentSub != key)
                {
                    return $"the map value of key {key.Length} chars long is detached, has a right or names another key";
                }
            }
        }

        if (type is YArrayBase list)
        {
            if (!list.PushHintForTests.IsNull && !IsAttached(list.PushHintForTests))
            {
                return "the push hint is detached";
            }

            foreach (var marker in list.MarkerItemsForTests)
            {
                if (!IsAttached(marker))
                {
                    return "a search marker is detached";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// What is wrong with the slots of the structs of one client, or null: a struct whose handle is not a live slot of
    /// this store's slab (or that shares it with another: <paramref name="handles"/> holds the ones seen), or whose slot
    /// differs from the object in clock, client, length, origins or info (the flags and the content's kind).
    /// </summary>
    private string SlotViolation(ClientStructs structs, HashSet<int> handles)
    {
        foreach (var s in structs)
        {
            if (s.SlotStore != this || s.Handle < 0 || !handles.Add(s.Handle))
            {
                return $"{s.Id.Client}:{s.Id.Clock} has no slot of its own in this store (handle {s.Handle})";
            }

            var slot = Slab.At(s.Handle);
            if (slot.IsFree)
            {
                return $"{s.Id.Client}:{s.Id.Clock} names a free slot";
            }

            var left = s.IsItem ? s.LeftOrigin : null;
            var right = s.IsItem ? s.RightOrigin : null;
            var info = (s.IsItem ? (s.Keep ? 1 : 0) | (s.Countable ? 2 : 0) | (s.Marker ? 8 : 0) | (s.Redone != null ? 64 : 0) | s.Content.Ref << 8 : 0) | (s.Deleted ? 4 : 0);
            if (slot.Clock != s.Id.Clock || !ClientTable.TryGetIndex(s.Id.Client, out var client) || slot.Client != client || slot.Length != s.Length)
            {
                return $"the slot of {s.Id.Client}:{s.Id.Clock} differs in id or length";
            }

            if (!SlotOrigin(left, slot.OriginClient, slot.OriginClock) || !SlotOrigin(right, slot.ROriginClient, slot.ROriginClock))
            {
                return $"the slot of {s.Id.Client}:{s.Id.Clock} differs in an origin";
            }

            if (slot.Info != info)
            {
                return $"the slot of {s.Id.Client}:{s.Id.Clock} holds info {slot.Info}, the struct {info}";
            }
        }

        return null;
    }

    private bool SlotOrigin(ID? origin, int client, long clock) =>
        origin is ID id ? ClientTable.TryGetIndex(id.Client, out var index) && index == client && id.Clock == clock : client == -1 && clock == 0;

    /// <summary>
    /// What is wrong with the slab as a whole, or null: its pages and free lists recount (<see cref="Slab.Violations"/>),
    /// and it holds as many live slots as the store holds structs, so that no slot lives without one.
    /// </summary>
    private string SlabViolation()
    {
        var structs = 0L;
        foreach (var list in Clients.Values)
        {
            structs += list.Count;
        }

        if (structs != Slab.LiveCount)
        {
            return $"the slab holds {Slab.LiveCount} live slots, the store {structs} structs";
        }

        return Slab.LiveCount <= 4096 || _checks % (Slab.LiveCount / 32) == 0 ? Slab.Violations().FirstOrDefault() : null;
    }

    // A document inserted twice belongs to the item integrated last, possibly in another document.
    private static bool IsAttachedInOwner(ItemRef item) =>
        !item.IsNull && item.Parent is AbstractType { Doc: { } owner } && owner.Store.IsAttached(item);

    private bool IsAttached(ItemRef held) =>
        Clients.TryGetValue(held.Id.Client, out var structs) && structs.FindIndex(held.Id.Clock) is var index && index >= 0 && index < structs.Count && structs[index] == held;
}
