#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

/// <summary>
/// What the store does with the content of a <see cref="SlotKind.Type"/> item. The slot's payload is the index in
/// the type table of the item's type, given when the item is made and never changed; the type's entry is cleared when
/// the type is collected; the slot is still a Type slot when the entry is cleared and is rewritten right after.
/// </summary>
internal static class TypeContent
{
    internal static InvalidOperationException Unsplittable() =>
        new("The content of a type has length 1 and cannot be split.");

    /// <summary>Integrates the type of <paramref name="item"/>, which names it by its table index.</summary>
    internal static void Integrate(Transaction transaction, ItemRef item) => item.TypeOf.Integrate(transaction.Doc, item);

    /// <summary>The content of a redone item: a new, empty type of the kind of the item's type.</summary>
    internal static ContentType Copy(ItemRef item) => new(item.TypeOf.InternalCopy());

    // Deleting and collecting a type walk the types nested in it, as deep as an update may nest them
    // (UpdateLimits.MaxDepth), so they keep the open types in a list instead of recursing. Both visit the items
    // in the order of Yjs' recursion: depth first, a type's list from its start, then its keys' values; the order
    // decides the order of the changed types, and so of the events.
    private sealed class Open(AbstractType type, ItemRef owner)
    {
        public readonly AbstractType Type = type;
        public readonly ItemRef Owner = owner;
        public ItemRef Next = type._start;
        public bool InMap;
        public Dictionary<string, ItemHandle>.Enumerator Keys;
        public ItemRef Value;

        /// <summary>Starts on the keys' values, once the list is done.</summary>
        public void EnterMap()
        {
            InMap = true;
            Keys = Type._map.GetEnumerator();
        }
    }

    internal static void Delete(Transaction transaction, ItemRef typeItem)
    {
        var open = new List<Open> { new(typeItem.TypeOf, default) };
        while (open.Count > 0)
        {
            var frame = open[^1];
            ItemRef item;
            if (!frame.InMap && !frame.Next.IsNull)
            {
                item = frame.Next;
                frame.Next = item.Right.IsItem ? item.Right : default;
            }
            else
            {
                if (!frame.InMap)
                {
                    frame.EnterMap();
                }

                if (!frame.Keys.MoveNext())
                {
                    transaction.LiveChanged.Remove(frame.Type);
                    open.RemoveAt(open.Count - 1);
                    continue;
                }

                item = frame.Type.MapItem(frame.Keys.Current.Value);
            }

            if (item.MarkDeleted(transaction))
            {
                if (item.Kind == SlotKind.Type)
                {
                    open.Add(new Open(item.TypeOf, item));
                }
                else
                {
                    item.DeleteContent(transaction);
                }
            }
            else if (IsOlderThan(item, transaction))
            {
                // Collected with the type later, but not in the transaction's delete set: merge it then.
                transaction._mergeStructs.Add(item.Id);
            }
        }
    }

    private static bool IsOlderThan(ItemRef item, Transaction transaction) =>
        item.Id.Clock < (transaction.BeforeState.TryGetValue(item.Id.Client, out var clock) ? clock : 0);

    /// <summary>
    /// Collects the type of <paramref name="typeItem"/> and the types nested in it: each type's entry in the table is
    /// cleared, and an item is replaced by a GC struct after the types nested in it were collected, as in Yjs' Item.gc.
    /// The item itself is left to the caller.
    /// </summary>
    internal static void Gc(StructStore store, ItemRef typeItem)
    {
        var open = new List<Open> { new(typeItem.TypeOf, default) };
        while (open.Count > 0)
        {
            var frame = open[^1];
            ItemRef item;
            if (!frame.InMap && !frame.Next.IsNull)
            {
                item = frame.Next;
                frame.Next = item.Right.IsItem ? item.Right : default;
            }
            else if (!frame.Value.IsNull)
            {
                item = frame.Value;
                frame.Value = item.Left.IsItem ? item.Left : default;
            }
            else
            {
                if (!frame.InMap)
                {
                    frame.EnterMap();
                }

                if (frame.Keys.MoveNext())
                {
                    frame.Value = frame.Type.MapItem(frame.Keys.Current.Value);
                    continue;
                }

                frame.Type._start = default;
                (frame.Type as YArrayBase)?.ForgetList();
                frame.Type._map.Clear();
                frame.Type._mapCount = 0;
                frame.Type.Collect();
                open.RemoveAt(open.Count - 1);
                if (!frame.Owner.IsNull)
                {
                    store.ReplaceWithGC(frame.Owner);
                }

                continue;
            }

            if (!item.Deleted)
            {
                throw new InvalidOperationException();
            }

            if (item.Kind == SlotKind.Type)
            {
                open.Add(new Open(item.TypeOf, item));
            }
            else
            {
                if (item.Kind != SlotKind.Deleted)
                {
                    item.GcContent(store);
                }

                store.ReplaceWithGC(item);
            }
        }
    }
}
