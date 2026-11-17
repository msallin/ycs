namespace Ycs;

internal sealed partial class StructStore
{
    // A set that grew beyond this is dropped after the item, so that clearing stays cheap.
    private const int KeepConflictSetCapacity = 1024;

    // Yjs' itemsBeforeOrigin and conflictingItems, reused from item to item; only Integrate uses them.
    private HashSet<ItemRef>? _itemsBeforeOrigin;
    private HashSet<ItemRef>? _conflictingItems;

    /// <summary>
    /// The steps the conflict resolution may still take for the update being applied
    /// (<see cref="UpdateLimits.MaxIntegrationSteps"/>); unlimited outside <see cref="YDoc"/>'s apply.
    /// </summary>
    internal long IntegrationSteps { get; set; } = long.MaxValue;

    /// <summary>
    /// The item <paramref name="item"/> goes right of, among the items between its origins that other
    /// clients inserted concurrently (Yjs' conflict loop in <c>Item.integrate</c>), starting from
    /// <paramref name="left"/> and the first item after it, <paramref name="o"/>.
    /// <br/>
    /// Yjs adds every scanned item to two sets, which are read only for a scanned item whose origin is
    /// another item, and not even then when that origin is the item scanned just before (a run typed
    /// after it), whose membership the walk knows. So the sets are filled when they are needed first,
    /// from the walk so far, and a walk over siblings and runs (n items of one client after n of
    /// another, or before a run of n: n^2 steps) neither allocates nor hashes. Each step counts
    /// against <see cref="IntegrationSteps"/>.
    /// </summary>
    internal ItemRef ResolveConflict(ItemRef item, ItemRef left, ItemRef o)
    {
        var first = o;
        ItemRef prev = default;
        var leftMoved = false;
        HashSet<ItemRef>? itemsBeforeOrigin = null;
        HashSet<ItemRef>? conflictingItems = null;

        // ResolveConflict allocates no slot, so a ref Slot taken here stays valid through the walk; item's own fields
        // (origins, right) do not change in it.
        ref readonly var self = ref item.Peek();
        var itemRight = self.Right;
        try
        {
            while (!o.IsNull && o.Handle != itemRight)
            {
                if (--IntegrationSteps < 0)
                {
                    throw new YUpdateException(
                        "Integrating the update takes more conflict resolution steps than UpdateLimits.MaxIntegrationSteps allows. " +
                        "The structs integrated before stay in the document; the rest of the update, and its deletions, were not applied.")
                    {
                        PartlyApplied = true,
                    };
                }

                ref readonly var os = ref o.Peek();
                var oIsItem = os.Kind != 0;
                itemsBeforeOrigin?.Add(o);
                conflictingItems?.Add(o);

                // The origins compare by client index, which names one client in the slab, and clock; none is -1.
                var oOriginClient = oIsItem ? os.OriginClient : -1;
                if (self.OriginClient == oOriginClient && (oOriginClient < 0 || self.OriginClock == os.OriginClock))
                {
                    // Case 1
                    if (o.Id.Client < item.Id.Client)
                    {
                        left = o;
                        leftMoved = true;
                        conflictingItems?.Clear();
                    }
                    else if (oIsItem ? self.ROriginClient == os.ROriginClient && (self.ROriginClient < 0 || self.ROriginClock == os.ROriginClock) : self.ROriginClient < 0)
                    {
                        // This and 'o' are conflicting and point to the same integration points. The id
                        // decides which item comes first: this is to the left of 'o'.
                        break;
                    }

                    // Else, 'o' might be integrated before an item that this conflicts with. If so, we
                    // will find it in the next iterations.
                }
                else if (oOriginClient >= 0)
                {
                    var leftOrigin = o.LeftOrigin!.Value;
                    bool beforeOrigin, conflicting;
                    var p = prev;
                    if (p.IsItem && leftOrigin.Client == p.Id.Client && leftOrigin.Clock >= p.Clock && leftOrigin.Clock < p.Clock + p.Length)
                    {
                        // The origin is the item scanned last (a run typed after it): scanned, and
                        // conflicting unless 'left' moved there.
                        beforeOrigin = true;
                        conflicting = left != p;
                    }
                    else
                    {
                        if (itemsBeforeOrigin == null)
                        {
                            itemsBeforeOrigin = _itemsBeforeOrigin ??= new HashSet<ItemRef>();
                            conflictingItems = _conflictingItems ??= new HashSet<ItemRef>();
                            Fill(itemsBeforeOrigin, first, o);
                            Fill(conflictingItems, leftMoved ? left.Right : first, o);
                        }

                        // Find, not GetItemCleanEnd: nothing needs to be split.
                        var origin = Find(leftOrigin);
                        beforeOrigin = itemsBeforeOrigin.Contains(origin);
                        conflicting = conflictingItems!.Contains(origin); // made together with itemsBeforeOrigin
                    }

                    if (!beforeOrigin)
                    {
                        break;
                    }

                    // Case 2
                    if (!conflicting)
                    {
                        left = o;
                        leftMoved = true;
                        conflictingItems?.Clear();
                    }
                }
                else
                {
                    break;
                }

                prev = o;
                o = oIsItem ? o.Right : default;
            }
        }
        finally
        {
            if (itemsBeforeOrigin != null)
            {
                Release(ref _itemsBeforeOrigin);
                Release(ref _conflictingItems);
            }
        }

        return left;

        // The walk from 'from' to 'to', both included.
        static void Fill(HashSet<ItemRef> set, ItemRef from, ItemRef to)
        {
            for (var s = from; !s.IsNull; s = s.IsItem ? s.Right : default)
            {
                set.Add(s);
                if (s == to)
                {
                    break;
                }
            }
        }

        // Called only once both sets exist (they are made together).
        static void Release(ref HashSet<ItemRef>? set)
        {
            if (set!.Count > KeepConflictSetCapacity)
            {
                set = null;
            }
            else
            {
                set.Clear();
            }
        }
    }
}
