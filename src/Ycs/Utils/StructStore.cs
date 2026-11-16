// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

using System.Diagnostics;

namespace Ycs;

internal sealed partial class StructStore
{
    /// <summary>One live slot for every struct of <see cref="Clients"/> (shadows the objects; <see cref="AbstractStruct.AttachSlot"/>).</summary>
    internal readonly Slab Slab = new();

    internal readonly ClientTable ClientTable = new();

    // TODO: [alekseyk] To private?
    public readonly Dictionary<long, ClientStructs> Clients = new Dictionary<long, ClientStructs>();

    /// <summary>
    /// Counts the structs added and split. Every change to the content of a type adds a struct, splits
    /// one (which can rewrite characters: a split surrogate pair) or deletes, and a delete of countable
    /// content lowers the type's <see cref="AbstractType.Length"/>: so while this and the length are
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
    internal ItemRef NextAfter(ItemRef held, ID id, int departures)
    {
        if (departures != Departures)
        {
            held = Find(id).AsItem;
            if (held.IsNull)
            {
                return default;
            }
        }

        return held.Right.AsItem;
    }

    /// <summary><paramref name="item"/>, the last struct of its client, grew in place by <paramref name="length"/> (<see cref="Item.ExtendRun"/>).</summary>
    internal void NoteRunExtended(ItemRef item, int length)
    {
        Clients[item.Id.Client].LastExtended(length);
        Version++;
        RunsExtended++;
    }

    /// <summary>How many inserts extended a run in place (the tests count how often the comparisons with Yjs see one).</summary>
    internal long RunsExtended { get; private set; }

    /// <summary>
    /// Return the states as a Map<int,int>.
    /// Note that clock refers to the next expected clock id.
    /// </summary>
    public IDictionary<long, long> GetStateVector()
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

    public long GetState(long clientId)
    {
        if (Clients.TryGetValue(clientId, out var structs))
        {
            return structs.End;
        }

        return 0;
    }

    public void IntegrityCheck()
    {
        foreach (var structs in Clients.Values)
        {
            if (structs.Count == 0)
            {
                throw new Exception($"{nameof(StructStore)} failed integrity check: no structs for client");
            }

            for (int i = 1; i < structs.Count; i++)
            {
                var left = structs[i - 1];
                var right = structs[i];

                if (left.Id.Clock + left.Length != right.Id.Clock)
                {
                    throw new Exception($"{nameof(StructStore)} failed integrity check: missing struct");
                }
            }
        }

        if (PendingDs.Count != 0 || Pending != null)
        {
            throw new Exception($"{nameof(StructStore)} failed integrity check: still have pending items");
        }
    }

    public void AddStruct(ItemRef str)
    {
        if (!Clients.TryGetValue(str.Id.Client, out var structs))
        {
            structs = _capacityHints != null && _capacityHints.Remove(str.Id.Client, out var capacity) ? new ClientStructs(capacity) : new ClientStructs();
            Clients[str.Id.Client] = structs;
        }
        else
        {
            if (structs.End != str.Id.Clock)
            {
                throw new Exception("Unexpected");
            }
        }

        structs.Add(str);
        str.AttachSlot(this);
        Version++;
    }

    /// <summary>
    /// <paramref name="item"/> changed what the run-head index holds of it (<see cref="ClientStructs.Refresh"/>): it
    /// was deleted, or an item was integrated between it and its left neighbour.
    /// </summary>
    internal void Refresh(ItemRef item)
    {
        var structs = Clients[item.Id.Client];
        var index = structs.FindIndex(item.Id.Clock);
        Debug.Assert(structs[index] == item, "Only a struct of the store changes the run-head index.");

        // Recounted from the struct at the position, which is the item.
        structs.Refresh(index);
    }

    /// <summary>The list and position of <paramref name="item"/>, if the store holds it (a collected type's items it does not).</summary>
    internal bool TryFindPosition(ItemRef item, out ClientStructs structs, out int index)
    {
        if (Clients.TryGetValue(item.Id.Client, out structs) && item.Id.Clock < structs.End)
        {
            index = structs.FindIndex(item.Id.Clock);
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
    private Dictionary<long, int> _capacityHints;

    internal void ForgetReservations() => _capacityHints?.Clear();

    /// <summary>
    /// Set once an undeleted item with a key was split: its first part can be a second live item of the key, which
    /// <see cref="IntegrationPlan.CheckGarbageCollection"/> then looks for in a type an update deletes. Never reset.
    /// </summary>
    internal bool MaySplitMapValues { get; private set; }

    private ItemRef SplitItem(Transaction transaction, ItemRef item, int diff)
    {
        Version++;
        MaySplitMapValues |= item.ParentSub != null && !item.Deleted;
        return item.SplitItem(transaction, diff);
    }

    /// <inheritdoc cref="ClientStructs.FindIndex(long)"/>
    public static int FindIndexSS(ClientStructs structs, long clock) => structs.FindIndex(clock);

    /// <inheritdoc cref="ClientStructs.FindIndex(long, ref int)"/>
    public static int FindIndexSS(ClientStructs structs, long clock, ref int hint) => structs.FindIndex(clock, ref hint);

    /// <summary>
    /// Expects that id is actually in store. This function throws or is an infinite loop otherwise.
    /// </summary>
    public ItemRef Find(ID id)
    {
        if (!Clients.TryGetValue(id.Client, out var structs))
        {
            throw new Exception($"No structs for client: {id.Client}");
        }

        int index = FindIndexSS(structs, id.Clock);
        if (index < 0 || index >= structs.Count)
        {
            throw new Exception($"Invalid struct index: {index}, max: {structs.Count}");
        }

        return structs[index];
    }

    public int FindIndexCleanStart(Transaction transaction, ClientStructs structs, long clock)
    {
        int index = FindIndexSS(structs, clock);
        var str = structs[index];
        if (str.Id.Clock < clock && str.IsItem)
        {
            structs.InsertSplit(index + 1, SplitItem(transaction, str, (int)(clock - str.Id.Clock)));
            return index + 1;
        }

        return index;
    }

    public ItemRef GetItemCleanStart(Transaction transaction, ID id) => GetItemCleanStart(transaction, id.Client, id.Clock);

    /// <summary>
    /// <see cref="GetItemCleanStart(Transaction, ID)"/> for a clock that can be negative (no <see cref="ID"/> is made of
    /// one): it throws then, as <see cref="ClientStructs.FindIndex(long)"/> does for any clock not in the store.
    /// </summary>
    public ItemRef GetItemCleanStart(Transaction transaction, long client, long clock)
    {
        if (!Clients.TryGetValue(client, out var structs))
        {
            throw new Exception();
        }

        int indexCleanStart = FindIndexCleanStart(transaction, structs, clock);
        Debug.Assert(indexCleanStart >= 0 && indexCleanStart < structs.Count);
        return structs[indexCleanStart];
    }

    public ItemRef GetItemCleanEnd(Transaction transaction, ID id)
    {
        if (!Clients.TryGetValue(id.Client, out var structs))
        {
            throw new Exception();
        }

        int index = FindIndexSS(structs, id.Clock);
        var str = structs[index];

        if ((id.Clock != str.Id.Clock + str.Length - 1) && !str.IsGC)
        {
            structs.InsertSplit(index + 1, SplitItem(transaction, str, (int)(id.Clock - str.Id.Clock + 1)));
        }

        return str;
    }

    public void ReplaceStruct(ItemRef oldStruct, ItemRef newStruct)
    {
        if (!Clients.TryGetValue(oldStruct.Id.Client, out var structs))
        {
            throw new Exception();
        }

        int index = FindIndexSS(structs, oldStruct.Id.Clock);
        structs.Replace(index, newStruct);
        newStruct.TakeSlotFrom(oldStruct);
        oldStruct.MarkDetached();
        Departures++;
    }

    public void IterateStructs(Transaction transaction, ClientStructs structs, long clockStart, long length, Predicate<ItemRef> fun)
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

            if (clockEnd < str.Id.Clock + str.Length)
            {
                FindIndexCleanStart(transaction, structs, clockEnd);
            }

            if (!fun(str))
            {
                break;
            }

            index++;
        } while (index < structs.Count && structs[index].Id.Clock < clockEnd);
    }

    public (ItemRef item, int diff) FollowRedone(ID id)
    {
        ID? nextId = id;
        int diff = 0;
        ItemRef item;

        do
        {
            if (diff > 0)
            {
                nextId = new ID(nextId.Value.Client, nextId.Value.Clock + diff);
            }

            item = Find(nextId.Value);
            diff = (int)(nextId.Value.Clock - item.Id.Clock);
            nextId = item.IsItem ? item.Redone : null;
        } while (nextId != null && item.IsItem);

        return (item, diff);
    }

    /// <summary>
    /// Applies the decoded structs and delete set of one update in <paramref name="transaction"/>
    /// (Yjs' <c>readUpdateV2</c> after <c>readClientsStructRefs</c>).
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
    internal void ApplyUpdate(Transaction transaction, Dictionary<long, List<AbstractStruct>> structs, DeleteSet ds, UpdateLimits limits, bool retrying = false)
    {
        // The conflict resolution walks items: it must find the ones Yjs has.
        transaction.MaterializeRunExtensions();

        var plan = _spare ?? new IntegrationPlan(this);
        _spare = null;
        plan.Plan(structs, limits.MaxDepth);
        if (transaction.Doc.Gc)
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

        var restMerged = !retrying && CheckPendingLimits(limits, retry, restUpdate, restStructs, plan.Rest, PendingDs.Growth(ds, plan));

        plan.Execute(transaction);

        // Clients whose structs all went pending got no list.
        ForgetReservations();

        if (restUpdate != null)
        {
            Pending ??= new PendingStructs();
            foreach (var kvp in plan.Missing)
            {
                if (!Pending.Missing.TryGetValue(kvp.Key, out var mclock) || mclock > kvp.Value)
                {
                    Pending.Missing[kvp.Key] = kvp.Value;
                }
            }

            if (!restMerged)
            {
                Pending.Add(restUpdate, restStructs, plan.Rest);
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
            var update = Pending.Merge();
            var count = Pending.Structs;
            Pending = null;

            // Their values were read under the limits of the updates that brought them.
            using var lease = UpdateReader.Read(update, 0, update.Length, v2: true, count, maxDepth: int.MaxValue);
            try
            {
                ApplyUpdate(transaction, lease.Structs, lease.DeleteSet, limits, retrying: true);
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
                transaction.Doc.InvokeOnPendingDropped(new PendingDrop(count, lease.Structs.Keys.ToArray()));
            }
        }
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
            Clients.TryGetValue(client, out var structs);
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

                var index = FindIndexSS(structs, clock, ref hint);

                // We can ignore the case of GC and Delete structs, because we are going to skip them.
                var str = structs[index];

                // Split the first item if necessary.
                if (!str.Deleted && str.Id.Clock < clock)
                {
                    structs.InsertSplit(index + 1, SplitItem(transaction, str, (int)(clock - str.Id.Clock)));

                    // Increase, we now want to use the next struct.
                    index++;
                }

                while (index < structs.Count)
                {
                    str = structs[index++];
                    if (str.Id.Clock >= clockEnd)
                    {
                        break;
                    }

                    if (!str.Deleted)
                    {
                        if (clockEnd < str.Id.Clock + str.Length)
                        {
                            structs.InsertSplit(index, SplitItem(transaction, str, (int)(clockEnd - str.Id.Clock)));
                        }

                        str.Delete(transaction);
                    }
                }
            }
        }
    }
}
