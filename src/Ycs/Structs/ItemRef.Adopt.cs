#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

internal readonly partial struct ItemRef
{
    /// <summary>
    /// Where a decoded struct enters the store (the one place). <paramref name="decoded"/> is a struct of an update the
    /// integration plan has put in order, with everything it refers to in the store or planned before it. An item's
    /// references are resolved first, whether or not it is integrated, in the order of Yjs: the origins' structs are split
    /// where an origin points into one, and the parent is resolved (to nothing when a neighbour is a GC struct or the
    /// parent type is collected: the item then enters as a GC struct). Then, when <paramref name="integrate"/> is set, the
    /// struct gets a slot and is integrated from <paramref name="offset"/>, which is the part of its range the store holds
    /// already; a GC struct is trimmed by it. Without <paramref name="integrate"/> nothing enters the store. The decoded
    /// object is read, never changed. A <see cref="Skip"/> struct is never adopted: it throws.
    /// </summary>
    public static void Adopt(AbstractStruct decoded, Transaction transaction, int offset, bool integrate)
    {
        var store = transaction.Doc.Store;
        switch (decoded)
        {
            case Item item:
                var (left, leftOrigin, right, rightOrigin, parent, parentSub) = ResolveReferences(item, transaction, store);
                if (integrate)
                {
                    var adopted = NewItem(store.Slab, item.Id, left, leftOrigin, right, rightOrigin, parent, parentSub, item.Content);
                    var start = item.Id.Clock + offset;
                    try
                    {
                        adopted.IntegrateItem(transaction, offset);
                    }
                    catch when (store.GetState(item.Id.Client) <= start)
                    {
                        // Not in its list yet, and nothing links to it: the slot would live without a struct.
                        adopted.FreeSlot();
                        throw;
                    }
                }

                break;
            case GC gc:
                if (integrate)
                {
                    store.AddStruct(NewGC(store.Slab, new ID(gc.Id.Client, gc.Id.Clock + offset), gc.Length - offset));
                }

                break;
            default:
                throw new InvalidOperationException("Only items and GC structs are adopted; a Skip struct stands for a gap and never enters a store.");
        }
    }

    /// <summary>
    /// Finds the structs a decoded item refers to, once they are all there (the second half of Yjs' <c>Item.getMissing</c>):
    /// left and right are the store's structs, split where needed, the origins are the ids of what they ended up naming,
    /// and the parent is resolved to a type, or to null when the item is to be integrated as a GC struct.
    /// </summary>
    private static (ItemRef Left, ID? LeftOrigin, ItemRef Right, ID? RightOrigin, AbstractType Parent, string ParentSub) ResolveReferences(Item item, Transaction transaction, StructStore store)
    {
        var parent = item.Parent;
        var parentSub = item.ParentSub;
        var leftOrigin = item.LeftOrigin;
        var rightOrigin = item.RightOrigin;
        ItemRef left = default;
        ItemRef right = default;
        if (parent is string rootName)
        {
            parent = transaction.Doc.Get<AbstractType>(rootName);
        }

        if (leftOrigin != null)
        {
            left = store.GetItemCleanEnd(transaction, leftOrigin.Value);
            leftOrigin = left.IsItem ? left.LastId : null;
        }

        if (rightOrigin != null)
        {
            right = store.GetItemCleanStart(transaction, rightOrigin.Value);
            rightOrigin = right.Id;
        }

        if (left.IsGC || right.IsGC)
        {
            parent = null;
        }
        else if (parent == null)
        {
            // Only set parent if this shouldn't be garbage collected; the left neighbour first, as Yjs.
            if (left.IsItem)
            {
                parent = left.Parent;
                parentSub = left.ParentSub;
            }
            else if (right.IsItem)
            {
                parent = right.Parent;
                parentSub = right.ParentSub;
            }
        }
        else if (parent is ID pid)
        {
            var parentItem = store.Find(pid);
            parent = parentItem.IsItem && parentItem.Kind == SlotKind.Type ? parentItem.TypeOf : null;
        }

        return (left, leftOrigin, right, rightOrigin, parent as AbstractType, parentSub);
    }
}
