#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

internal sealed partial class StructStore
{
    // A set that grew beyond this is dropped after the item, so that clearing stays cheap.
    private const int KeepConflictSetCapacity = 1024;

    // Yjs' itemsBeforeOrigin and conflictingItems, reused from item to item; only Integrate uses them.
    private HashSet<AbstractStruct> _itemsBeforeOrigin;
    private HashSet<AbstractStruct> _conflictingItems;

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
    internal AbstractStruct ResolveConflict(Item item, AbstractStruct left, AbstractStruct o)
    {
        var first = o;
        AbstractStruct prev = null;
        var leftMoved = false;
        HashSet<AbstractStruct> itemsBeforeOrigin = null;
        HashSet<AbstractStruct> conflictingItems = null;

        try
        {
            while (o != null && o != item.Right)
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

                var oItem = o as Item;
                itemsBeforeOrigin?.Add(o);
                conflictingItems?.Add(o);

                if (ID.Equals(item.LeftOrigin, oItem?.LeftOrigin))
                {
                    // Case 1
                    if (o.Id.Client < item.Id.Client)
                    {
                        left = o;
                        leftMoved = true;
                        conflictingItems?.Clear();
                    }
                    else if (ID.Equals(item.RightOrigin, oItem?.RightOrigin))
                    {
                        // This and 'o' are conflicting and point to the same integration points. The id
                        // decides which item comes first: this is to the left of 'o'.
                        break;
                    }

                    // Else, 'o' might be integrated before an item that this conflicts with. If so, we
                    // will find it in the next iterations.
                }
                else if (oItem?.LeftOrigin is { } leftOrigin)
                {
                    bool beforeOrigin, conflicting;
                    if (prev is Item p && leftOrigin.Client == p.Id.Client && leftOrigin.Clock >= p.Id.Clock && leftOrigin.Clock < p.Id.Clock + p.Length)
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
                            itemsBeforeOrigin = _itemsBeforeOrigin ??= new HashSet<AbstractStruct>();
                            conflictingItems = _conflictingItems ??= new HashSet<AbstractStruct>();
                            Fill(itemsBeforeOrigin, first, o);
                            Fill(conflictingItems, leftMoved ? ((Item)left).Right : first, o);
                        }

                        // Find, not GetItemCleanEnd: nothing needs to be split.
                        var origin = Find(leftOrigin);
                        beforeOrigin = itemsBeforeOrigin.Contains(origin);
                        conflicting = conflictingItems.Contains(origin);
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
                o = oItem?.Right;
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
        static void Fill(HashSet<AbstractStruct> set, AbstractStruct from, AbstractStruct to)
        {
            for (var s = from; s != null; s = (s as Item)?.Right)
            {
                set.Add(s);
                if (s == to)
                {
                    break;
                }
            }
        }

        static void Release(ref HashSet<AbstractStruct> set)
        {
            if (set.Count > KeepConflictSetCapacity)
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
