
namespace Ycs;

internal sealed partial class StructStore
{
    /// <summary>Merges at the end of a transaction in Yjs' order, pair by pair (the tests' reference; see <see cref="DeleteSet.TryToMergeWithLefts(ClientStructs, int)"/>).</summary>
    internal bool PairwiseMergeForTests;

    /// <summary>Recounts the struct lists after every transaction and throws on a difference (the tests switch it on).</summary>
    internal static bool CheckAfterTransactionsForTests;

    private long _checks;

    /// <summary>A live item of the store other than <paramref name="except"/> (<paramref name="except"/> itself when there is none).</summary>
    internal ItemRef OtherItemForTests(ItemRef except)
    {
        foreach (var list in Clients.Values)
        {
            foreach (var str in list)
            {
                if (str.IsItem && str != except)
                {
                    return str;
                }
            }
        }

        return except;
    }

    /// <summary>Adds a GC struct of <paramref name="length"/> from <paramref name="id"/> to its client's list, outside any transaction.</summary>
    internal ItemRef AddGcForTests(ItemId id, int length)
    {
        var gc = ItemRef.NewGC(Slab, id, length);
        AddStruct(gc);
        return gc;
    }

    /// <summary>
    /// What <see cref="CheckAfterTransactionsForTests"/> checks: every list (<see cref="ClientStructs.Violations"/>), the
    /// links and holders around its structs (<c>ConsistencyViolation</c>), the slot of every struct (<c>SlotViolation</c>)
    /// and the slab; the structs a transaction touched always, a list of more than 1,024 structs only every count/256th time, so that a test stays linear in its
    /// edits.
    /// </summary>
    internal void CheckForTests(Transaction? transaction = null)
    {
        _checks++;
        HashSet<YType>? visited = null;
        HashSet<int> handles = [];
        foreach (var (client, structs) in Clients)
        {
            if (structs.Count > 1024 && _checks % (structs.Count / 256) != 0)
            {
                continue;
            }

            if (structs.Violations() is { } violation)
            {
                throw new InvalidOperationException($"The structs of client {client} differ from a recount: {violation}.");
            }

            // Two lookups a struct, and as many to compare it with its slot: a list of more than 1,024 is walked every
            // count/32nd time.
            if (structs.Count <= 1024 || _checks % (structs.Count / 32) == 0)
            {
                if (ConsistencyViolation(structs, ref visited) is { } inconsistency)
                {
                    throw new InvalidOperationException($"The structs of client {client} are inconsistent: {inconsistency}.");
                }

                if (SlotViolation(structs, handles) is { } slotViolation)
                {
                    throw new InvalidOperationException($"The slots of client {client} differ from its structs: {slotViolation}.");
                }
            }
        }

        if (transaction != null && TouchedSlotViolation(transaction) is { } touched)
        {
            throw new InvalidOperationException($"The slots of the structs of the transaction differ from the structs: {touched}.");
        }

        if (SlabViolation() is { } slab)
        {
            throw new InvalidOperationException($"The slab differs from the store: {slab}.");
        }

        if (CharsViolation() is { } chars)
        {
            throw new InvalidOperationException($"The character arena differs from the String items: {chars}.");
        }

        if (ValuesViolation() is { } values)
        {
            throw new InvalidOperationException($"The value arena differs from the Any items: {values}.");
        }

        if (CollectedTypeViolation() is { } collected)
        {
            throw new InvalidOperationException($"The store holds a collected type that is not forgotten: {collected}.");
        }
    }
}
