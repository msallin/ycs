using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Ycs;

internal sealed partial class StructStore
{
    /// <summary>The struct of <see cref="Clients"/>: one live slot for each (more while an item is being integrated).</summary>
    internal readonly Slab Slab = new();

    internal ClientTable ClientTable => Slab.Clients;

    /// <summary>The types that items of this store name as their parent, by the index a slot holds.</summary>
    internal TypeTable Types => Slab.Types;

    internal readonly Dictionary<long, ClientStructs> Clients = new Dictionary<long, ClientStructs>();

    // The values of Clients by the client's index in the slab's client table (what a slot names its client by): the paths
    // that start from a struct find its list without hashing the client. Set where Clients is, by AddStruct.
    private ClientStructs[] _structsByIndex = [];

    /// <summary>The list of the struct's client; null when the store holds no struct of that client.</summary>
    private ClientStructs? StructsOf(ItemRef item)
    {
        var index = item.ClientIndex;
        return (uint)index < (uint)_structsByIndex.Length ? _structsByIndex[index] : null;
    }

    /// <summary>
    /// Counts the structs added and the splits that rewrite characters (a split surrogate pair). Every change to the
    /// content of a type adds a struct, splits one that way or deletes, and a delete of countable
    /// content lowers the type's <see cref="YType.Length"/>: so while this and the length are
    /// unchanged, so is the content (<see cref="YText.ToString"/> caches on it).
    /// </summary>
    internal long Version { get; private set; }

    /// <summary>
    /// Counts the structs that left a list: merged into their left neighbour, or replaced by a GC struct. A holder that
    /// runs the caller's code between two steps keeps the id of its struct and this count, and looks the id up again
    /// when the count moved (<see cref="NextAfter"/>).
    /// </summary>
    internal int Departures { get; private set; }

    internal void NoteDeparture() => Departures++;

    /// <summary>
    /// The item right of <paramref name="held"/>, a struct that began at <paramref name="id"/> when
    /// <paramref name="departures"/> was read, after caller's code ran. When structs left since, <paramref name="held"/>
    /// can be one of them: the walk goes on from the struct that holds <paramref name="id"/> now, which is where Yjs'
    /// orphan continues unless the list changed right of it since. <c>default</c> at the end of the list, and when
    /// the id lies in a GC struct (its type was collected: nothing visible follows).
    /// </summary>
    internal ItemRef NextAfter(ItemRef held, ItemId id, int departures)
    {
        held = Resolve(held, id, departures);
        return held.IsNull ? default : held.Right.AsItem;
    }

    /// <summary>
    /// <paramref name="held"/>, a struct that began at <paramref name="id"/> when <paramref name="departures"/> was read,
    /// as it is after caller's code ran: the struct that holds <paramref name="id"/> now when structs left since,
    /// <c>default</c> when that is a GC struct. Until then <paramref name="held"/> is not read.
    /// </summary>
    internal ItemRef Resolve(ItemRef held, ItemId id, int departures) => departures == Departures ? held : Find(id).AsItem;

    /// <summary><paramref name="item"/>, the last struct of its client, grew in place by <paramref name="length"/> (<see cref="ItemRef.ExtendRun"/>).</summary>
    internal void NoteRunExtended(ItemRef item, int length)
    {
        var structs = StructsOf(item)!;
        LogChange(structs, item.Id.Client);
        structs.LastExtended(length);
        Version++;
        RunsExtended++;
    }

    /// <summary>How many inserts extended a run in place (the tests count how often the comparisons with Yjs see one).</summary>
    internal long RunsExtended { get; private set; }

    /// <summary>The state vector; a clock is the next expected clock of its client.</summary>
    internal IDictionary<long, long> GetStateVector()
    {
        var result = new Dictionary<long, long>(Clients.Count);
        GetStateVector(result);
        return result;
    }

    /// <summary>Writes the state vector into <paramref name="into"/>, which is cleared first.</summary>
    internal void GetStateVector(Dictionary<long, long> into)
    {
        into.Clear();
        foreach (var kvp in Clients)
        {
            into[kvp.Key] = kvp.Value.End;
        }
    }

    internal long GetState(long clientId)
    {
        if (Clients.TryGetValue(clientId, out var structs))
        {
            return structs.End;
        }

        return 0;
    }

    internal void AddStruct(ItemRef str)
    {
        var structs = StructsOf(str);
        if (structs != null && structs.End != str.Clock)
        {
            throw new InvalidOperationException($"The struct {str.Id.Client}:{str.Clock} does not continue its client's list, which ends at {structs.End}; nothing was added.");
        }

        if (structs == null)
        {
            var client = str.Id.Client;
            structs = _capacityHints != null && _capacityHints.Remove(client, out var capacity) ? new ClientStructs(Slab, capacity) : new ClientStructs(Slab);
            structs.Ordinal = Clients.Count;
            Clients[client] = structs;
            var index = str.ClientIndex;
            if (index >= _structsByIndex.Length)
            {
                Array.Resize(ref _structsByIndex, Math.Max(index + 1, _structsByIndex.Length * 2));
            }

            _structsByIndex[index] = structs;
        }

        LogChange(structs, str.Id.Client);
        structs.Add(str);

        // Where the item is, for the Refresh its own content's integration may ask for.
        _knownIndex = structs.Count - 1;
        Version++;
        TestHooks.Compact(Slab, true);
    }

    /// <summary>
    /// <paramref name="item"/> changed what the run-head index holds of it (<see cref="ClientStructs.Refresh"/>): it
    /// was deleted, or an item was integrated between it and its left neighbour.
    /// </summary>
    internal void Refresh(ItemRef item)
    {
        var structs = StructsOf(item)!;
        var index = _knownIndex;
        if (index < 0 || index >= structs.Count || structs[index] != item)
        {
            index = structs.FindIndex(item.Clock);
            Debug.Assert(structs[index] == item, "Only a struct of the store changes the run-head index.");
        }


        // Recounted from the struct at the position, which is the item.
        structs.Refresh(index);
    }

    // The position of a struct that is about to be refreshed (the one ApplyDeleteSet is deleting, the one GetItemCleanStart
    // returned last, the one AddStruct added last), so that Refresh(ItemRef) need not search for it: used only where the
    // struct at it is the item, so any value left behind is harmless.
    private int _knownIndex = -1;

    /// <summary>The list and position of <paramref name="item"/>, if the store holds it (a collected type's items it does not).</summary>
    internal bool TryFindPosition(ItemRef item, [NotNullWhen(true)] out ClientStructs? structs, out int index)
    {
        structs = StructsOf(item);
        if (structs != null && item.Clock < structs.End)
        {
            index = structs.FindIndex(item.Clock);
            if (structs[index] == item)
            {
                return true;
            }
        }

        index = -1;
        return false;
    }

    /// <summary>
    /// <paramref name="item"/>, or the item of its run a walk to the right with <paramref name="remaining"/> visible
    /// elements to go reaches by jumping (<see cref="ClientStructs.SeekRight"/>); <paramref name="remaining"/> is
    /// lowered by what it jumped over.
    /// </summary>
    internal ItemRef SeekRight(ItemRef item, ref int remaining, bool stopAtFormats)
    {
        if (remaining <= 0 || !TryFindPosition(item, out var structs, out var position))
        {
            return item;
        }

        var reached = structs.SeekRight(position, remaining, stopAtFormats, out var passed);
        remaining -= checked((int)passed);
        return structs[reached];
    }

    /// <summary>Makes room for <paramref name="count"/> more structs of <paramref name="client"/> (none are added).</summary>
    internal void ReserveStructs(long client, int count)
    {
        if (Clients.TryGetValue(client, out var structs))
        {
            structs.EnsureCapacity(structs.Count + count);
        }
        else
        {
            // A client's list is made by its first struct: the store has no empty lists.
            (_capacityHints ??= new Dictionary<long, int>())[client] = count;
        }
    }

    // Capacities for the lists of clients the store does not have yet, from ReserveStructs (for the update being integrated).
    private Dictionary<long, int>? _capacityHints;

    internal void ForgetReservations() => _capacityHints?.Clear();

    /// <summary>
    /// Set once an undeleted item with a key was split: its first part can be a second live item of the key, which
    /// <see cref="IntegrationPlan.CheckGarbageCollection"/> then looks for in a type an update deletes. Never reset.
    /// </summary>
    internal bool MaySplitMapValues { get; private set; }

    /// <summary>Set once an item with a key was integrated, a deleted one too. Never reset.</summary>
    internal bool HasKeyedItems { get; set; }

    private ItemRef SplitItem(Transaction transaction, ItemRef item, int diff)
    {
        if (item.Kind == SlotKind.String && char.IsHighSurrogate(item.Chars[diff - 1]))
        {
            // The cut pair becomes two replacement characters: the only split that changes a text.
            Version++;
        }

        MaySplitMapValues |= item.ParentSub != null && !item.Deleted;
        return item.SplitItem(transaction, diff);
    }

    /// <summary>
    /// Expects that id is actually in store. This function throws or is an infinite loop otherwise.
    /// </summary>
    internal ItemRef Find(ItemId id)
    {
        if (!Clients.TryGetValue(id.Client, out var structs))
        {
            throw new InvalidOperationException($"No structs for client: {id.Client}");
        }

        int index = structs.FindIndex(id.Clock);
        if (index < 0 || index >= structs.Count)
        {
            throw new InvalidOperationException($"Invalid struct index: {index}, max: {structs.Count}");
        }

        return structs[index];
    }

    internal int FindIndexCleanStart(Transaction transaction, ClientStructs structs, long clock)
    {
        int index = structs.FindIndex(clock);
        var str = structs[index];
        if (str.Clock < clock && str.IsItem)
        {
            structs.InsertSplit(index + 1, SplitItem(transaction, str, (int)(clock - str.Clock)));
            return index + 1;
        }

        return index;
    }

    internal ItemRef GetItemCleanStart(Transaction transaction, ItemId id) => GetItemCleanStart(transaction, id.Client, id.Clock);

    /// <summary>
    /// <see cref="GetItemCleanStart(Transaction, ItemId)"/> for a clock that can be negative (no <see cref="ItemId"/> is made of
    /// one): it throws then, as <see cref="ClientStructs.FindIndex(long)"/> does for any clock not in the store.
    /// </summary>
    internal ItemRef GetItemCleanStart(Transaction transaction, long client, long clock)
    {
        if (!Clients.TryGetValue(client, out var structs))
        {
            throw new InvalidOperationException("No structs for the client of the clock asked for; the struct it refers to is not in the store.");
        }

        int indexCleanStart = FindIndexCleanStart(transaction, structs, clock);
        Debug.Assert(indexCleanStart >= 0 && indexCleanStart < structs.Count);

        // The neighbour an integration relinks is mostly this one, which Refresh then need not search for.
        _knownIndex = indexCleanStart;
        return structs[indexCleanStart];
    }

    internal ItemRef GetItemCleanEnd(Transaction transaction, ItemId id)
    {
        if (!Clients.TryGetValue(id.Client, out var structs))
        {
            throw new InvalidOperationException($"No structs for client: {id.Client}");
        }

        int index = structs.FindIndex(id.Clock);
        var str = structs[index];

        if ((id.Clock != str.Clock + str.Length - 1) && !str.IsGC)
        {
            structs.InsertSplit(index + 1, SplitItem(transaction, str, (int)(id.Clock - str.Clock + 1)));
        }

        return str;
    }

    /// <summary>
    /// Replaces <paramref name="item"/> in its list by a GC struct of the same range, which keeps its slot: the
    /// references to it stay valid and read the GC struct.
    /// </summary>
    internal void ReplaceWithGC(ItemRef item)
    {
        if (!Clients.TryGetValue(item.Id.Client, out var structs))
        {
            throw new InvalidOperationException($"No structs for client: {item.Id.Client}");
        }

        int index = structs.FindIndex(item.Clock);
        item.BecomeGC();
        structs.Replace(index, item);
        Departures++;
    }

    internal void IterateStructs(Transaction transaction, ClientStructs structs, long clockStart, long length, Predicate<ItemRef> fun)
    {
        if (length <= 0)
        {
            return;
        }

        var clockEnd = clockStart + length;

        var index = FindIndexCleanStart(transaction, structs, clockStart);
        ItemRef str;

        do
        {
            str = structs[index];

            if (clockEnd < str.Clock + str.Length)
            {
                FindIndexCleanStart(transaction, structs, clockEnd);
            }

            if (!fun(str))
            {
                break;
            }

            index++;
        } while (index < structs.Count && structs[index].Clock < clockEnd);
    }

    internal (ItemRef item, int diff) FollowRedone(ItemId id)
    {
        ItemId? nextId = id;
        int diff = 0;
        ItemRef item;

        do
        {
            if (diff > 0)
            {
                nextId = new ItemId(nextId.Value.Client, nextId.Value.Clock + diff);
            }

            item = Find(nextId.Value);
            diff = (int)(nextId.Value.Clock - item.Clock);
            nextId = item.IsItem ? item.Redone : null;
        } while (nextId != null && item.IsItem);

        return (item, diff);
    }

    /// <summary>
    /// Applies the structs and delete set of one update in <paramref name="transaction"/>
    /// (Yjs' <c>readUpdateV2</c> after <c>readClientsStructRefs</c>): the lease's staging table, whose structs are
    /// adopted from it. The lease stays open until this returns.
    /// <br/>
    /// First the integration is planned without changing anything: which structs integrate in which
    /// order, which wait for missing structs, and whether nesting or the pending limits of
    /// <paramref name="limits"/> would be exceeded (a <see cref="YUpdateException"/>, and the document is
    /// unchanged). Then the plan is carried out, the delete set applied, and pending structs that can
    /// now integrate are retried, as in Yjs.
    /// <br/>
    /// When pending structs are retried and their plan fails a check (nesting, garbage collection), they are
    /// dropped and the update stands; it is applied in part (<see cref="YUpdateException.PartlyApplied"/>) only
    /// when structs of its own waited for them, otherwise <see cref="YDoc.PendingDropped"/> reports the drop.
    /// See docs/api.md, "Atomicity".
    /// </summary>
    /// <param name="retrying">For the pending structs themselves, which were admitted to pending when they arrived.</param>
    internal void ApplyUpdate(Transaction transaction, StagingTable table, DeleteSet ds, UpdateLimits limits, bool retrying = false)
    {
        // The conflict resolution walks items: it must find the ones Yjs has.
        transaction.MaterializeRunExtensions();

        var plan = _spare ?? new IntegrationPlan(this);
        _spare = null;
        plan.Plan(table, limits.MaxDepth);
        // Without a key in the update or in the store nothing can be a key's value, which is all the check looks for.
        if (transaction.Doc.GarbageCollection && (table.HasKeys || HasKeyedItems))
        {
            plan.CheckGarbageCollection(transaction.Doc, ds, PendingDs);
        }

        var (restUpdate, restStructs) = plan.EncodeRest();
        var retry = false;
        if (Pending != null)
        {
            foreach (var (client, clock) in Pending.Missing)
            {
                if (clock < plan.GetState(client))
                {
                    retry = true;
                    break;
                }
            }
        }

        var restMerged = !retrying && CheckPendingLimits(limits, retry, restUpdate, restStructs, plan, PendingDs.Growth(ds, plan));

        plan.Execute(transaction);

        // Clients whose structs all went pending got no list.
        ForgetReservations();

        if (restUpdate != null)
        {
            Pending ??= new PendingStructs();
            foreach (var kvp in plan.Missing!)
            {
                if (!Pending.Missing.TryGetValue(kvp.Key, out var mclock) || mclock > kvp.Value)
                {
                    Pending.Missing[kvp.Key] = kvp.Value;
                }
            }

            if (!restMerged)
            {
                Pending.Add(restUpdate, restStructs, plan);
            }
        }

        plan.Clear();
        _spare = plan;

        // As in Yjs: the update's deletions first, then those that were waiting. When both leave deletions waiting,
        // Yjs merges the two (mergeUpdatesV2), which sorts and merges every client's ranges: adjacent ranges are then
        // applied as one, without a split between them (which would replace a split surrogate pair with U+FFFD).
        var waiting = PendingDs.TakeApplicable(this);
        var waitingKept = PendingDs.Count > 0;
        var additions = PendingDs.Additions;
        ApplyDeleteSet(transaction, ds);
        var updateLeft = PendingDs.Additions != additions;
        additions = PendingDs.Additions;
        if (waiting != null)
        {
            ApplyDeleteSet(transaction, waiting);
        }

        if (updateLeft && (waitingKept || PendingDs.Additions != additions))
        {
            PendingDs.Compact();
        }

        if (retry)
        {
            // Something the pending structs were waiting for arrived: read them again, from their
            // encoded form, as Yjs does.
            var update = Pending!.Merge();
            var count = Pending.Structs;
            Pending = null;

            // Their values were read under the limits of the updates that brought them.
            using var lease = UpdateReader.Read(update, v2: true, count, maxDepth: int.MaxValue);
            try
            {
                ApplyUpdate(transaction, lease.Staging, lease.DeleteSet, limits, retrying: true);
            }
            catch (YUpdateException e) when (!e.PartlyApplied)
            {
                // Their plan failed a check before it changed anything (a nested retry that failed was dropped by
                // the nested call): this update has been carried out, and what waited is dropped, which leaves the
                // document causally complete. Kept, it would fail every later update's retry again, and a fresh
                // document could not apply this one's state, of which pending structs are part.
                if (restUpdate != null)
                {
                    // Structs of this update waited for them and are dropped with them: it is cut short.
                    throw new YUpdateException(
                        "Structs that waited for this update, with structs of this update that waited for them, cannot be " +
                        "integrated. They were dropped; the rest of the update was applied.", e)
                    {
                        PartlyApplied = true,
                    };
                }

                // The update stood whole; what somebody else sent earlier is gone, which the document reports:
                // clients that were sent it as waiting structs may integrate it, under other limits.
                transaction.Doc.InvokeOnPendingDropped(new PendingDrop(count, ClientsOf(lease.Staging)));
            }
        }
    }

    // The clients that have structs in the table (not those only an origin names), in the order of their sections.
    private static long[] ClientsOf(StagingTable table)
    {
        var clients = new List<long>(table.ClientCount);
        var starts = new List<int>(table.ClientCount);
        for (var i = 0; i < table.ClientCount; i++)
        {
            var (start, count) = table.RangeOf(i);
            if (count > 0)
            {
                clients.Add(table.ClientAt(i));
                starts.Add(start);
            }
        }

        var result = clients.ToArray();
        Array.Sort(starts.ToArray(), result);
        return result;
    }

    /// <summary>
    /// Deletes what <paramref name="ds"/> covers (Yjs' <c>readAndApplyDeleteSet</c>); what this store
    /// does not have yet is added to <see cref="PendingDs"/>.
    /// </summary>
    private void ApplyDeleteSet(Transaction transaction, DeleteSet ds)
    {
        foreach (var (client, deletes) in ds.Clients)
        {
            var state = GetState(client);
            // Used only for a clock below the state, which the client's list holds.
            Clients.TryGetValue(client, out var found);
            var structs = found!;
            var hint = 0;

            foreach (var delete in deletes)
            {
                var clock = delete.Clock;
                var clockEnd = clock + delete.Length;

                if (clock >= state)
                {
                    PendingDs.Add(client, clock, clockEnd - clock);
                    continue;
                }

                if (state < clockEnd)
                {
                    PendingDs.Add(client, state, clockEnd - state);
                }

                var index = structs.FindIndex(clock, ref hint);

                // We can ignore the case of GC and Delete structs, because we are going to skip them.
                var str = structs[index];

                // Split the first item if necessary.
                if (!str.Deleted && str.Clock < clock)
                {
                    structs.InsertSplit(index + 1, SplitItem(transaction, str, (int)(clock - str.Clock)));

                    // Increase, we now want to use the next struct.
                    index++;
                }

                while (index < structs.Count)
                {
                    str = structs[index++];
                    if (str.Clock >= clockEnd)
                    {
                        break;
                    }

                    if (!str.Deleted)
                    {
                        if (clockEnd < str.Clock + str.Length)
                        {
                            structs.InsertSplit(index, SplitItem(transaction, str, (int)(clockEnd - str.Clock)));
                        }

                        _knownIndex = index - 1;
                        str.Delete(transaction);
                        _knownIndex = -1;
                    }
                }
            }
        }
    }
}
