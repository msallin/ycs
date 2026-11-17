#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

internal readonly partial struct ItemRef
{
    /// <summary>
    /// Where a staged struct enters the store (the one place). Struct <paramref name="i"/> of <paramref name="table"/> is a
    /// struct of an update the integration plan has put in order, with everything it refers to in the store or planned
    /// before it. An item's references are resolved first, whether or not it is integrated, in the order of Yjs: the
    /// origins' structs are split where an origin points into one, and the parent is resolved (to nothing when a neighbour
    /// is a GC struct or the parent type is collected: the item then enters as a GC struct). Then, when
    /// <paramref name="integrate"/> is set, the part of the struct from <paramref name="offset"/> on (the part before it
    /// the store holds already) gets a slot and is integrated: characters and values are copied from the offset, a Type
    /// is made here and given its table index, a cold object becomes the slot's, a GC struct is trimmed. Without
    /// <paramref name="integrate"/> nothing enters the store. The table is read, never changed. A Skip struct is never
    /// adopted: it throws.
    /// </summary>
    public static void Adopt(StagingTable table, int i, Transaction transaction, int offset, bool integrate)
    {
        var store = transaction.Doc.Store;
        var kind = table.At(i).Kind;
        if (kind == StagedKind.Skip)
        {
            throw new InvalidOperationException("Only items and GC structs are adopted; a Skip struct stands for a gap and never enters a store.");
        }

        var id = table.IdOf(i);
        if (kind == StagedKind.GC)
        {
            if (integrate)
            {
                store.AddStruct(NewGC(store.Slab, new ID(id.Client, id.Clock + offset), table.At(i).Length - offset));
            }

            return;
        }

        var (left, leftOrigin, right, rightOrigin, parent, parentSub) = ResolveReferences(table, i, transaction, store);
        if (!integrate)
        {
            return;
        }

        var length = table.At(i).Length - offset;
        var start = new ID(id.Client, id.Clock + offset);
        ItemRef adopted;
        switch (kind)
        {
            case StagedKind.String:
                var text = table.Chars(i);
                var cutPair = offset > 0 && char.IsHighSurrogate(text[offset - 1]);
                adopted = NewItem(store.Slab, start, left, leftOrigin, right, rightOrigin, parent, parentSub, SlotKind.String, length, null, text[offset..], default, default, null, cutPair);
                break;
            case StagedKind.Any:
                adopted = NewItem(store.Slab, start, left, leftOrigin, right, rightOrigin, parent, parentSub, SlotKind.Any, length, null, default, table.Values(i)[offset..], default);
                break;
            case StagedKind.Type:
                var type = ContentType.Create(table.At(i).TypeRef, table.TypeName(i));
                adopted = NewItem(store.Slab, start, left, leftOrigin, right, rightOrigin, parent, parentSub, SlotKind.Type, 1, null, default, default, default, type);
                break;
            case StagedKind.Deleted:
                adopted = NewItem(store.Slab, start, left, leftOrigin, right, rightOrigin, parent, parentSub, SlotKind.Deleted, length, null, default, default, default);
                break;
            default:
                var cold = (IContentEx)table.Cold(i);
                if (offset > 0)
                {
                    cold = (IContentEx)cold.Splice(offset);
                }

                adopted = NewItem(store.Slab, start, left, leftOrigin, right, rightOrigin, parent, parentSub, (SlotKind)kind, length, cold, default, default, default);
                break;
        }

        try
        {
            adopted.IntegrateItem(transaction, offset);
        }
        catch when (store.GetState(id.Client) <= start.Clock)
        {
            // Not in its list yet, and nothing links to it: the slot would live without a struct.
            adopted.FreeSlot();
            throw;
        }
    }

    /// <summary>
    /// Finds the structs a staged item refers to, once they are all there (the second half of Yjs' <c>Item.getMissing</c>):
    /// left and right are the store's structs, split where needed, the origins are the ids of what they ended up naming,
    /// and the parent is resolved to a type, or to null when the item is to be integrated as a GC struct.
    /// </summary>
    private static (ItemRef Left, ID? LeftOrigin, ItemRef Right, ID? RightOrigin, AbstractType Parent, string ParentSub) ResolveReferences(StagingTable table, int i, Transaction transaction, StructStore store)
    {
        var parentSub = table.ParentSub(i);
        var leftOrigin = table.LeftOrigin(i);
        var rightOrigin = table.RightOrigin(i);
        AbstractType parent = null;
        ID? parentId = null;
        switch (table.At(i).ParentKind)
        {
            case StagedParent.Root:
                parent = transaction.Doc.Get<AbstractType>(table.ParentName(i));
                break;
            case StagedParent.Id:
                parentId = table.ParentId(i);
                break;
        }

        ItemRef left = default;
        ItemRef right = default;
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
        else if (parent == null && parentId == null)
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
        else if (parentId is { } pid)
        {
            var parentItem = store.Find(pid);
            parent = parentItem.IsItem && parentItem.Kind == SlotKind.Type ? parentItem.TypeOf : null;
        }

        return (left, leftOrigin, right, rightOrigin, parent, parentSub);
    }
}
