#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

internal sealed partial class StructStore
{
    /// <summary>
    /// What is wrong with the links and holders around the structs of one client, or null: left and right mirror each
    /// other; the type of a type item names the item and the subdocument of a document item names an attached item (a document inserted twice belongs to the item integrated last); and, for every type an
    /// item names as its parent or holds, no holder points at a freed struct (one that is no longer in its client's
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

            if (LinkViolation(s) is { } link)
            {
                return link;
            }

            if (s.Content is ContentType content && content.Type._item != s)
            {
                return $"the type of {s.Id.Client}:{s.Id.Clock} names another item";
            }

            if (s.Content is ContentDoc doc && doc.Doc._item != s && !IsAttachedInOwner(doc.Doc._item))
            {
                return $"the subdocument of {s.Id.Client}:{s.Id.Clock} names a freed item";
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

    // The links of an item as its slot holds them, read without a reference to a slot that is free: left and right name
    // attached items that name it back.
    private string LinkViolation(ItemRef s)
    {
        var slot = Slab.Read(s.Handle);
        if (slot.Kind == 0)
        {
            return null;
        }

        if (slot.Left >= 0 && (!IsAttached(slot.Left) || Slab.Read(slot.Left).Kind == 0 || Slab.Read(slot.Left).Right != s.Handle))
        {
            return $"the left of {s.Id.Client}:{s.Id.Clock} is freed or does not point back";
        }

        if (slot.Right >= 0 && (!IsAttached(slot.Right) || Slab.Read(slot.Right).Kind == 0 || Slab.Read(slot.Right).Left != s.Handle))
        {
            return $"the right of {s.Id.Client}:{s.Id.Clock} is freed or does not point back";
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
            return "the item of a live type is freed";
        }

        var start = type._start;
        if (!start.IsNull && (!IsAttached(start) || !start.Left.IsNull))
        {
            return "the start of a type is freed or has a left";
        }

        if (type._map.Count <= 1024 || _checks % (type._map.Count / 256) == 0)
        {
            foreach (var (key, value) in type._map)
            {
                if (!IsAttached(value) || !value.Right.IsNull || value.ParentSub != key)
                {
                    return $"the map value of key {key.Length} chars long is freed, has a right or names another key";
                }
            }
        }

        if (type is YArrayBase list)
        {
            if (!list.PushHintForTests.IsNull && !IsAttached(list.PushHintForTests))
            {
                return "the push hint is freed";
            }

            foreach (var marker in list.MarkerItemsForTests)
            {
                if (!IsAttached(marker))
                {
                    return "a search marker is freed";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// What is wrong with the slots of the structs of one client, or null: a struct whose slot is not a live slot of
    /// this store's slab or that shares it with another (<paramref name="handles"/> holds the ones seen), or whose slot
    /// contradicts itself (<see cref="SlotViolation(ItemRef)"/>).
    /// </summary>
    private string SlotViolation(ClientStructs structs, HashSet<int> handles)
    {
        foreach (var s in structs)
        {
            if (SlotViolation(s) is { } violation)
            {
                return violation;
            }

            if (!handles.Add(s.Handle))
            {
                return $"{s.Id.Client}:{s.Id.Clock} shares its slot with another struct (handle {s.Handle})";
            }
        }

        return null;
    }

    /// <summary>
    /// What is wrong with the slot of one struct, or null: it is not a live slot of this store's slab, or its kind, flags,
    /// content, key, redone id and parent do not fit each other. A GC struct holds no links, origins, parent, content
    /// or key; an item holds a content object of its kind, and the index of a parent type that exists in the type table.
    /// </summary>
    private string SlotViolation(ItemRef s)
    {
        if (!ReferenceEquals(s.Slab, Slab) || !Slab.TryRead(s.Handle, out var slot) || slot.IsFree)
        {
            return $"a struct of the list has no live slot in this store (handle {s.Handle})";
        }

        var id = $"{ClientTable[slot.Client]}:{slot.Clock}";
        var content = Slab.GetRef(s.Handle);
        var hasRedone = (slot.Info & Slot.HasRedoneFlag) != 0;
        if (hasRedone != Slab.HasRedone(s.Handle))
        {
            return $"the slot of {id} has the has-redone flag {hasRedone} and {(hasRedone ? "no" : "a")} redone id";
        }

        if (slot.Kind == 0)
        {
            return slot.Info == Slot.DeletedFlag && slot.Left == -1 && slot.Right == -1 && slot.Parent == -1 && slot.OriginClient == -1 && slot.ROriginClient == -1 && content == null && Slab.GetKey(s.Handle) == null
                ? null
                : $"the slot of the GC struct {id} holds more than a range";
        }

        if (content is not IContentEx held || held.Ref != slot.Kind || held.Length != slot.Length)
        {
            return $"the slot of {id} holds the kind {slot.Kind} and length {slot.Length}, its content another";
        }

        if (slot.Parent < 0 || slot.Parent >= Types.Count)
        {
            return $"the slot of {id} names parent {slot.Parent} of {Types.Count} types";
        }

        return null;
    }

    /// <summary>
    /// What is wrong with the slots and links of the structs <paramref name="transaction"/> touched, which every transaction
    /// checks whatever the length of their lists: the structs added, those of its merge list, and their neighbours in
    /// the list (a merge changes the left one's links).
    /// </summary>
    private string TouchedSlotViolation(Transaction transaction)
    {
        foreach (var (client, structs) in Clients)
        {
            var from = transaction.BeforeState.TryGetValue(client, out var before) ? before : 0;
            if (from < structs.End)
            {
                for (var i = structs.FindIndex(from); i < structs.Count; i++)
                {
                    if ((SlotViolation(structs[i]) ?? LinkViolation(structs[i])) is { } violation)
                    {
                        return violation;
                    }
                }
            }
        }

        foreach (var id in transaction._mergeStructs)
        {
            if (!Clients.TryGetValue(id.Client, out var structs) || id.Clock >= structs.End)
            {
                continue;
            }

            var index = structs.FindIndex(id.Clock);
            for (var i = Math.Max(0, index - 1); i <= Math.Min(structs.Count - 1, index + 1); i++)
            {
                if ((SlotViolation(structs[i]) ?? LinkViolation(structs[i])) is { } violation)
                {
                    return violation;
                }
            }
        }

        return null;
    }

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
        !item.IsNull && item.Parent is { Doc: { } owner } && owner.Store.IsAttached(item);

    // By the slot, without a read through the reference: a holder that names a freed slot is what is looked for, and a
    // Debug read would throw. A holder of a slot that was freed and then reused names another struct of the list, which
    // only the Debug generation check of its next read finds.
    private bool IsAttached(ItemRef held) => !held.IsNull && ReferenceEquals(held.Slab, Slab) && IsAttached(held.Handle);

    private bool IsAttached(int handle)
    {
        if (!Slab.TryRead(handle, out var slot) || slot.IsFree)
        {
            return false;
        }

        return Clients.TryGetValue(ClientTable[slot.Client], out var structs) && slot.Clock < structs.End && structs.FindIndex(slot.Clock) is var index && index >= 0 && index < structs.Count && structs[index].Handle == handle;
    }
}
