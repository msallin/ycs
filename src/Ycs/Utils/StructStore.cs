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
    /// <summary>The structs read from an update for one client, and how many were consumed.</summary>
    internal sealed class ClientStructRefs
    {
        public int NextReadOperation { get; set; }
        public List<AbstractStruct> Refs { get; set; }
    }

    private static readonly Dictionary<long, long> EmptyStateVector = new();
    private static readonly List<AbstractStruct> NoRefs = new();

    /// <summary>Merges at the end of a transaction in Yjs' order, pair by pair (the tests' reference; see <see cref="DeleteSet.TryToMergeWithLefts(ClientStructs, int)"/>).</summary>
    internal bool PairwiseMergeForTests;

    /// <summary>
    /// Structs that could not be integrated yet (Yjs' <c>store.pendingStructs</c>), and per client the
    /// lowest clock that is missing for them. Yjs keeps one encoded update and merges every new remainder
    /// into it (<c>mergeUpdatesV2([pending.update, rest])</c>), which costs the size of everything pending per
    /// update; here a remainder that shares no clock with what waits is kept as it comes (an encoded v2 update),
    /// and the remainders are merged once, when they are retried or compacted.
    /// <br/>
    /// A remainder that shares a clock with what waits is merged at once, as Yjs merges every one: where structs
    /// overlap, merging is not associative. Remainders disjoint by clock give the fold's bytes in one merge
    /// (docs/api.md, "Pending structs, kept").
    /// <br/>
    /// They are re-read from their encoding when they are retried, so a root type realized in between
    /// (e.g. with <see cref="YDoc.GetText"/>) is resolved then.
    /// </summary>
    internal sealed class PendingStructs
    {
        // The clocks of the GC structs and items of every remainder added (Skip structs are gaps). Never lowered: a
        // remainder rejected after Add stays recorded, which can only make a later Add merge where it need not,
        // and a merge at once is Yjs' own step.
        private readonly ClockRanges _clocks = new();

        public Dictionary<long, long> Missing { get; } = new();
        public List<byte[]> Updates { get; } = new();
        public long Bytes { get; private set; }

        /// <summary>How many structs <see cref="Updates"/> hold (Skip structs included), as they were written.</summary>
        public long Structs { get; private set; }

        /// <summary>Adds the remainder <paramref name="update"/>, which holds the structs of <paramref name="rest"/>.</summary>
        public void Add(byte[] update, long structs, StructStore rest)
        {
            if (SharesAClock(rest))
            {
                // Yjs' pending.update = mergeUpdatesV2([pending.update, rest.update]).
                var merged = new List<byte[]> { Merge(), update };
                var count = Structs + structs;
                Replace(UpdateOperations.MergeUpdatesTrusted(merged, ref count), count);
            }
            else
            {
                Updates.Add(update);
                Bytes += update.Length;
                Structs += structs;
            }

            foreach (var (client, list) in rest.Clients)
            {
                for (var i = 0; i < list.Count; i++)
                {
                    var str = list[i];
                    if (str is not Skip)
                    {
                        _clocks.Add(client, str.Id.Clock, str.Id.Clock + str.Length);
                    }
                }
            }
        }

        private bool SharesAClock(StructStore rest)
        {
            foreach (var (client, list) in rest.Clients)
            {
                for (var i = 0; i < list.Count; i++)
                {
                    var str = list[i];
                    if (str is not Skip && _clocks.Overlaps(client, str.Id.Clock, str.Id.Clock + str.Length))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public byte[] Merge()
        {
            var structs = Structs;
            var merged = UpdateOperations.MergeUpdatesTrusted(Updates, ref structs);
            Replace(merged, structs);
            return merged;
        }

        /// <summary>Makes <paramref name="update"/> all that waits.</summary>
        public void Replace(byte[] update, long structs)
        {
            Updates.Clear();
            Updates.Add(update);
            Bytes = update.Length;
            Structs = structs;
        }
    }

    // TODO: [alekseyk] To private?
    public readonly Dictionary<long, ClientStructs> Clients = new Dictionary<long, ClientStructs>();

    /// <summary>The struct lists by client, as the property tests read them (they reach no internal type).</summary>
    internal IEnumerable<KeyValuePair<long, IReadOnlyList<AbstractStruct>>> StructListsForTests() =>
        Clients.Select(kv => new KeyValuePair<long, IReadOnlyList<AbstractStruct>>(kv.Key, kv.Value));

    // The plan of the last update, kept for the next one (null while one is in use).
    private IntegrationPlan _spare;

    internal PendingStructs Pending { get; set; }

    /// <summary>Deletions of structs this store does not have yet (Yjs' <c>store.pendingDs</c>).</summary>
    internal PendingDeletes PendingDs { get; } = new();

    internal bool HasPending => Pending != null || PendingDs.Count != 0;

    /// <summary>The encoded size of what is pending, as <see cref="UpdateLimits.MaxPendingBytes"/> counts it.</summary>
    internal long PendingBytes => (Pending?.Bytes ?? 0) + PendingDs.Bytes;

    /// <summary>The structs pending, as <see cref="UpdateLimits.MaxPendingStructs"/> counts them.</summary>
    internal long PendingStructCount => Pending?.Structs ?? 0;

    /// <summary>
    /// Counts the structs added and split. Every change to the content of a type adds a struct, splits
    /// one (which can rewrite characters: a split surrogate pair) or deletes, and a delete of countable
    /// content lowers the type's <see cref="AbstractType.Length"/>: so while this and the length are
    /// unchanged, so is the content (<see cref="YText.ToString"/> caches on it).
    /// </summary>
    internal long Version { get; private set; }

    /// <summary><paramref name="item"/>, the last struct of its client, grew in place by <paramref name="length"/> (<see cref="Item.ExtendRun"/>).</summary>
    internal void NoteRunExtended(Item item, int length)
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

    public void AddStruct(AbstractStruct str)
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
        Version++;
    }

    /// <summary>
    /// <paramref name="item"/> changed what the run-head index holds of it (<see cref="ClientStructs.Refresh"/>): it
    /// was deleted, or an item was integrated between it and its left neighbour.
    /// </summary>
    internal void Refresh(Item item)
    {
        var structs = Clients[item.Id.Client];
        var index = structs.FindIndex(item.Id.Clock);
        Debug.Assert(ReferenceEquals(structs[index], item), "Only a struct of the store changes the run-head index.");

        // Recounted from the struct at the position, which is the item.
        structs.Refresh(index);
    }

    /// <summary>The list and position of <paramref name="item"/>, if the store holds it (a collected type's items it does not).</summary>
    internal bool TryFindPosition(Item item, out ClientStructs structs, out int index)
    {
        if (Clients.TryGetValue(item.Id.Client, out structs) && item.Id.Clock < structs.End)
        {
            index = structs.FindIndex(item.Id.Clock);
            if (ReferenceEquals(structs[index], item))
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
    internal Item SeekRight(Item item, ref int remaining, bool stopAtFormats)
    {
        if (remaining <= 0 || !TryFindPosition(item, out var structs, out var position))
        {
            return item;
        }

        var reached = structs.SeekRight(position, remaining, stopAtFormats, out var passed);
        remaining -= checked((int)passed);
        return (Item)structs[reached];
    }

    /// <summary>Recounts the struct lists after every transaction and throws on a difference (the tests switch it on).</summary>
    internal static bool CheckAfterTransactionsForTests;

    private long _checks;

    /// <summary>
    /// What <see cref="CheckAfterTransactionsForTests"/> checks: every list (<see cref="ClientStructs.Violations"/>); a
    /// list of more than 1,024 structs only every count/256th time, so that a test stays linear in its edits.
    /// </summary>
    internal void CheckForTests()
    {
        _checks++;
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
        }
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

    private Item SplitItem(Transaction transaction, Item item, int diff)
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
    public AbstractStruct Find(ID id)
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
        if (str.Id.Clock < clock && str is Item item)
        {
            structs.InsertSplit(index + 1, SplitItem(transaction, item, (int)(clock - item.Id.Clock)));
            return index + 1;
        }

        return index;
    }

    public AbstractStruct GetItemCleanStart(Transaction transaction, ID id) => GetItemCleanStart(transaction, id.Client, id.Clock);

    /// <summary>
    /// <see cref="GetItemCleanStart(Transaction, ID)"/> for a clock that can be negative (no <see cref="ID"/> is made of
    /// one): it throws then, as <see cref="ClientStructs.FindIndex(long)"/> does for any clock not in the store.
    /// </summary>
    public AbstractStruct GetItemCleanStart(Transaction transaction, long client, long clock)
    {
        if (!Clients.TryGetValue(client, out var structs))
        {
            throw new Exception();
        }

        int indexCleanStart = FindIndexCleanStart(transaction, structs, clock);
        Debug.Assert(indexCleanStart >= 0 && indexCleanStart < structs.Count);
        return structs[indexCleanStart];
    }

    public AbstractStruct GetItemCleanEnd(Transaction transaction, ID id)
    {
        if (!Clients.TryGetValue(id.Client, out var structs))
        {
            throw new Exception();
        }

        int index = FindIndexSS(structs, id.Clock);
        var str = structs[index];

        if ((id.Clock != str.Id.Clock + str.Length - 1) && !(str is GC))
        {
            structs.InsertSplit(index + 1, SplitItem(transaction, (Item)str, (int)(id.Clock - str.Id.Clock + 1)));
        }

        return str;
    }

    public void ReplaceStruct(AbstractStruct oldStruct, AbstractStruct newStruct)
    {
        if (!Clients.TryGetValue(oldStruct.Id.Client, out var structs))
        {
            throw new Exception();
        }

        int index = FindIndexSS(structs, oldStruct.Id.Clock);
        structs.Replace(index, newStruct);
    }

    public void IterateStructs(Transaction transaction, ClientStructs structs, long clockStart, long length, Predicate<AbstractStruct> fun)
    {
        if (length <= 0)
        {
            return;
        }

        var clockEnd = clockStart + length;

        var index = FindIndexCleanStart(transaction, structs, clockStart);
        AbstractStruct str;

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

    public (AbstractStruct item, int diff) FollowRedone(ID id)
    {
        ID? nextId = id;
        int diff = 0;
        AbstractStruct item;

        do
        {
            if (diff > 0)
            {
                nextId = new ID(nextId.Value.Client, nextId.Value.Clock + diff);
            }

            item = Find(nextId.Value);
            diff = (int)(nextId.Value.Clock - item.Id.Clock);
            nextId = (item as Item)?.Redone;
        } while (nextId != null && item is Item);

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
    /// when structs of its own were among them. See docs/api.md, "Atomicity".
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
            try
            {
                // Their values were read under the limits of the updates that brought them.
                using var lease = UpdateReader.Read(update, 0, update.Length, v2: true, count, maxDepth: int.MaxValue);
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
                    throw new YUpdateException(
                        "Structs that waited for this update, with structs of this update that waited for them, cannot be " +
                        $"integrated ({e.Message}). They were dropped; the rest of the update was applied.", e)
                    {
                        PartlyApplied = true,
                    };
                }
            }
        }
    }

    /// <summary>
    /// Rejects an update that would make more wait than <paramref name="limits"/> allow. One that lets
    /// pending structs integrate (<paramref name="retry"/>) may add up to the limits on top of what
    /// waits, since what waits is then integrated, wholly or in part: so what waits never exceeds
    /// twice the limits, and the retry's re-read is bounded with it. Returns true when
    /// <paramref name="rest"/> had to be merged into <see cref="Pending"/> to fit (a duplicate).
    /// </summary>
    private bool CheckPendingLimits(UpdateLimits limits, bool retry, byte[] rest, long restStructs, StructStore restStore, long deleteGrowth)
    {
        var restBytes = rest?.Length ?? 0;
        if (restStructs == 0 && restBytes + deleteGrowth <= 0)
        {
            return false;
        }

        var maxStructs = retry ? Twice(limits.MaxPendingStructs) : limits.MaxPendingStructs;
        var maxBytes = retry ? Twice(limits.MaxPendingBytes) : limits.MaxPendingBytes;
        if (Fits(restStructs, restBytes))
        {
            return false;
        }

        // Duplicates (the same update received again) shrink when merged, also with the update's rest.
        Pending?.Merge();
        PendingDs.Compact();
        if (Fits(restStructs, restBytes))
        {
            return false;
        }

        if (rest != null && Pending != null)
        {
            var (pending, pendingStructs) = (Pending.Updates[0], Pending.Structs);
            Pending.Add(rest, restStructs, restStore);
            Pending.Merge();
            if (Fits(0, 0))
            {
                return true;
            }

            // The remainder's clocks stay recorded: that can only make a later Add merge where it need not.
            Pending.Replace(pending, pendingStructs);
        }

        throw new YUpdateException(
            $"The update depends on structs this document does not have, and keeping it until they arrive would grow " +
            $"the pending data beyond {maxStructs} structs or {maxBytes} bytes (UpdateLimits.MaxPendingStructs, " +
            $"UpdateLimits.MaxPendingBytes). The document is unchanged.");

        bool Fits(long addedStructs, long addedBytes) =>
            addedStructs <= maxStructs - PendingStructCount && addedBytes + deleteGrowth <= maxBytes - PendingBytes;

        static long Twice(long limit) => limit > long.MaxValue / 2 ? long.MaxValue : 2 * limit;
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
                    structs.InsertSplit(index + 1, SplitItem(transaction, (Item)str, (int)(clock - str.Id.Clock)));

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
                            structs.InsertSplit(index, SplitItem(transaction, (Item)str, (int)(clockEnd - str.Id.Clock)));
                        }

                        str.Delete(transaction);
                    }
                }
            }
        }
    }

    /// <summary>
    /// How the structs of one update integrate (Yjs' <c>integrateStructs</c>), worked out against the
    /// store's state without changing the store, then carried out by <see cref="Execute"/>.
    /// <br/>
    /// While there is something to do, structs are taken in this order:
    /// 1. Top element on stack, if stack is not empty.
    /// 2. Next element from current struct reader (if empty, use next struct reader).
    /// <br/>
    /// If a struct causally depends on another struct (ref.missing), the next reader of
    /// 'ref.id.client' is put on top of the stack. At some point a struct has no causal dependencies,
    /// then the stack empties. There are no circles: struct1 (from client1) depends on struct2 (from
    /// client2) depends on struct3 (from client1). Therefore, the max stack size is the number of clients.
    /// <br/>
    /// When a struct depends on something that is not there, it and the rest of its client's structs
    /// are set aside (<see cref="Rest"/>) and everything else is still integrated.
    /// </summary>
    internal sealed class IntegrationPlan
    {
        private readonly StructStore _store;
        private readonly Dictionary<long, long> _state = new();
        private readonly List<(AbstractStruct Struct, int Offset, bool Integrate)> _steps = new();
        private readonly Func<long, long> _getState;

        // Planning state, kept between updates.
        private readonly Dictionary<long, ClientStructRefs> _clientsStructRefs = new();
        private readonly List<long> _clientsStructRefsIds = new();
        private readonly List<AbstractStruct> _stack = new();
        private readonly List<ClientStructRefs> _freeRefs = new();
        private readonly List<Item> _chain = new();
        private Dictionary<long, List<AbstractStruct>> _refs;

        // Nesting depth of the types this update creates (by the id of their item), and of the type
        // each item lands in (when it was needed), for the nesting limit.
        private Dictionary<ID, int> _typeDepths;
        private int _maxDepth;
        private Dictionary<Item, int> _parentDepths;

        // The parent and key of the planned items without a parent (by their references), for the garbage collection check.
        private Dictionary<Item, (object Parent, string Key)> _keys;
        private readonly List<(long Client, long Clock, long End)> _ranges = new();
        private readonly List<Item> _keyChain = new();

        public IntegrationPlan(StructStore store)
        {
            _store = store;
            _getState = GetState;
        }

        /// <summary>The structs set aside, by client, or null.</summary>
        public StructStore Rest { get; private set; }

        /// <summary>Per client, the lowest clock the structs set aside wait for (set with <see cref="Rest"/>).</summary>
        public Dictionary<long, long> Missing { get; private set; }

        /// <summary>A client's clock once the plan is carried out.</summary>
        public long GetState(long client) => _state.TryGetValue(client, out var clock) ? clock : _store.GetState(client);

        public void Execute(Transaction transaction)
        {
            foreach (var (str, offset, integrate) in _steps)
            {
                (str as Item)?.ResolveReferences(transaction, _store);

                if (integrate)
                {
                    str.Integrate(transaction, offset);
                }
            }
        }

        /// <summary>The structs set aside as a v2 update (with an empty delete set), or null, and how many they are.</summary>
        public (byte[] Update, long Structs) EncodeRest()
        {
            if (Rest == null)
            {
                return (null, 0);
            }

            long structs = 0;
            foreach (var list in Rest.Clients.Values)
            {
                structs += list.Count;
            }

            var encoder = UpdateEncoderV2.Rent();
            try
            {
                EncodingUtils.WriteClientsStructs(encoder, Rest, EmptyStateVector);

                // Write an empty delete set: there is no need for an extra function call, just write 0 deletes.
                encoder.RestWriter.WriteVarUint(0);
                return (encoder.ToArray(), structs);
            }
            finally
            {
                encoder.Return();
            }
        }

        /// <summary>Forgets the update, so that the plan can be used for the next one.</summary>
        public void Clear()
        {
            _refs = null;
            _state.Clear();
            _steps.Clear();
            _stack.Clear();
            _clientsStructRefsIds.Clear();
            foreach (var refs in _clientsStructRefs.Values)
            {
                refs.Refs = null;
                _freeRefs.Add(refs);
            }

            _clientsStructRefs.Clear();
            _typeDepths?.Clear();
            _parentDepths?.Clear();
            _keys?.Clear();
            Rest = null;
            Missing = null;
        }

        /// <summary>
        /// Works out how <paramref name="refs"/> integrate, with types nested at most <paramref name="maxDepth"/>
        /// levels. A plan that threw (or was used before) is cleared first, so nothing of an earlier update is left in it.
        /// </summary>
        public void Plan(Dictionary<long, List<AbstractStruct>> refs, int maxDepth)
        {
            Clear();
            _refs = refs;
            _maxDepth = maxDepth;
            var clientsStructRefs = _clientsStructRefs;
            var planned = 0;
            _store.ForgetReservations();
            foreach (var (client, list) in refs)
            {
                if (list.Count > 0)
                {
                    // Room for what the update adds (the update's own size bounds it), so a large update does not
                    // grow the client's list and the plan step by step.
                    planned += list.Count;
                    _store.ReserveStructs(client, list.Count);

                    ClientStructRefs clientRefs;
                    if (_freeRefs.Count > 0)
                    {
                        clientRefs = _freeRefs[^1];
                        _freeRefs.RemoveAt(_freeRefs.Count - 1);
                        clientRefs.NextReadOperation = 0;
                    }
                    else
                    {
                        clientRefs = new ClientStructRefs();
                    }

                    clientRefs.Refs = list;
                    clientsStructRefs[client] = clientRefs;
                    _clientsStructRefsIds.Add(client);
                }
            }

            _steps.EnsureCapacity(planned);

            // Sort them so that we take the higher id first, in case of conflicts the lower id will probably not conflict with the id from the higher user.
            _clientsStructRefsIds.Sort();

            var curStructsTarget = GetNextStructTarget();
            if (curStructsTarget == null)
            {
                return;
            }

            var stack = _stack;
            var stackHead = curStructsTarget.Refs[curStructsTarget.NextReadOperation++];

            // Iterate over all struct readers until we are done.
            while (true)
            {
                if (stackHead is not Skip)
                {
                    var offset = GetState(stackHead.Id.Client) - stackHead.Id.Clock;
                    if (offset < 0)
                    {
                        // Update from the same client is missing.
                        stack.Add(stackHead);
                        UpdateMissingSv(stackHead.Id.Client, stackHead.Id.Clock - 1);

                        // Hit a dead wall, add all items from stack to restSS.
                        AddStackToRestSS();
                    }
                    else
                    {
                        var missing = (stackHead as Item)?.FindMissing(_getState);
                        if (missing != null)
                        {
                            stack.Add(stackHead);

                            // Get the struct reader that has the missing struct.
                            if (!clientsStructRefs.TryGetValue(missing.Value, out var structRefs) || structRefs.Refs.Count == structRefs.NextReadOperation)
                            {
                                // This update message causally depends on another update message that doesn't exist yet.
                                UpdateMissingSv(missing.Value, GetState(missing.Value));
                                AddStackToRestSS();
                            }
                            else
                            {
                                stackHead = structRefs.Refs[structRefs.NextReadOperation++];
                                continue;
                            }
                        }
                        else if (offset < stackHead.Length)
                        {
                            // All fine, apply the stackhead.
                            if (stackHead is Item { Content: ContentType } typeItem)
                            {
                                CheckNesting(typeItem);
                            }

                            _steps.Add((stackHead, (int)offset, true));
                            _state[stackHead.Id.Client] = stackHead.Id.Clock + stackHead.Length;
                        }
                        else if (stackHead is Item)
                        {
                            // Known already. Yjs still looks up its references (splitting items there).
                            _steps.Add((stackHead, 0, false));
                        }
                    }
                }

                // Iterate to next stackHead.
                if (stack.Count > 0)
                {
                    stackHead = stack[^1];
                    stack.RemoveAt(stack.Count - 1);
                }
                else if (curStructsTarget != null && curStructsTarget.NextReadOperation < curStructsTarget.Refs.Count)
                {
                    stackHead = curStructsTarget.Refs[curStructsTarget.NextReadOperation++];
                }
                else
                {
                    curStructsTarget = GetNextStructTarget();
                    if (curStructsTarget == null)
                    {
                        // We are done!
                        break;
                    }

                    stackHead = curStructsTarget.Refs[curStructsTarget.NextReadOperation++];
                }
            }
        }

        private ClientStructRefs GetNextStructTarget()
        {
            var ids = _clientsStructRefsIds;
            while (ids.Count > 0)
            {
                var target = _clientsStructRefs[ids[^1]];
                if (target.NextReadOperation < target.Refs.Count)
                {
                    return target;
                }

                ids.RemoveAt(ids.Count - 1);
            }

            return null;
        }

        private void UpdateMissingSv(long client, long clock)
        {
            Missing ??= new Dictionary<long, long>();
            if (!Missing.TryGetValue(client, out var mclock) || mclock > clock)
            {
                Missing[client] = clock;
            }
        }

        private void AddStackToRestSS()
        {
            Rest ??= new StructStore();
            foreach (var item in _stack)
            {
                var client = item.Id.Client;
                if (_clientsStructRefs.TryGetValue(client, out var inapplicableItems))
                {
                    // Decrement because we weren't able to apply previous operation.
                    inapplicableItems.NextReadOperation--;
                    Rest.Clients[client] = new ClientStructs(inapplicableItems.Refs, inapplicableItems.NextReadOperation, inapplicableItems.Refs.Count - inapplicableItems.NextReadOperation);
                    _clientsStructRefs.Remove(client);

                    // The plan may still read it as its current target: it is empty now.
                    inapplicableItems.NextReadOperation = 0;
                    inapplicableItems.Refs = NoRefs;
                    _freeRefs.Add(inapplicableItems);
                }
                else
                {
                    // Item was the last item on clientsStructRefs and the field was already cleared. Add item to restStructs and continue.
                    var single = new ClientStructs(1);
                    single.Add(item);
                    Rest.Clients[client] = single;
                }

                // Remove client from clientsStructRefsIds to prevent users from applying the same update again.
                _clientsStructRefsIds.Remove(client);
            }

            _stack.Clear();
        }

        /// <summary>
        /// Rejects a type that would be nested deeper than <see cref="UpdateLimits.MaxDepth"/>. The depth of
        /// the type an item lands in follows its references as <see cref="Item.ResolveReferences"/> will:
        /// the given parent, else the parent of its left origin, else of its right origin. (When the
        /// other origin is a GC struct the item becomes one too; ignoring that only overestimates.)
        /// </summary>
        private void CheckNesting(Item typeItem)
        {
            var parentDepth = ParentDepth(typeItem);
            if (parentDepth >= 0)
            {
                if (parentDepth + 1 > _maxDepth)
                {
                    throw new YUpdateException($"Shared types are nested deeper than {_maxDepth} levels (UpdateLimits.MaxDepth).");
                }

                (_typeDepths ??= new Dictionary<ID, int>())[typeItem.Id] = parentDepth + 1;
            }
        }

        /// <summary>The depth of the type <paramref name="item"/> lands in, or -1 when it becomes a GC struct.</summary>
        private int ParentDepth(Item item)
        {
            _parentDepths ??= new Dictionary<Item, int>();
            var chain = _chain;
            chain.Clear();
            int depth;

            while (true)
            {
                if (_parentDepths.TryGetValue(item, out depth))
                {
                    break;
                }

                chain.Add(item);
                if (item.Parent is string)
                {
                    depth = 0;
                    break;
                }

                if (item.Parent is ID parentId)
                {
                    depth = TypeDepth(parentId);
                    break;
                }

                var origin = (item.LeftOrigin ?? item.RightOrigin).Value;
                if (origin.Clock < _store.GetState(origin.Client))
                {
                    depth = _store.Find(origin) is Item { Parent: AbstractType parent } ? parent.Depth : -1;
                    break;
                }

                if (FindPlanned(origin) is not Item next)
                {
                    depth = -1;
                    break;
                }

                item = next;
            }

            foreach (var c in chain)
            {
                _parentDepths[c] = depth;
            }

            return depth;
        }

        /// <summary>The depth of the type defined by the item <paramref name="id"/>, or -1 when it is not a type.</summary>
        private int TypeDepth(ID id)
        {
            if (id.Clock < _store.GetState(id.Client))
            {
                return _store.Find(id) is Item { Content: ContentType ct } ? ct.Type.Depth : -1;
            }

            return _typeDepths != null && _typeDepths.TryGetValue(id, out var depth) ? depth : -1;
        }

        /// <summary>
        /// Rejects an update whose cleanup would throw: the garbage collection of a deleted type requires every item
        /// in it to be deleted (Yjs' Item.gc throws "Unexpected case" there, after integrating). The one item a deleted
        /// type can leave undeleted is a part of a key's value other than its last part: when a value of several items
        /// (Yjs never writes one undeleted) is split, by an origin or a right origin inside it or by a deleted range that
        /// starts or ends inside it, the key holds two live items, and deleting the type deletes only the last. Such a
        /// part, made by this update or kept from an earlier one (unseen, as in Yjs), fails the update that collects a
        /// type above it. Called only when the document collects garbage; see docs/api.md (Untrusted input).
        /// </summary>
        public void CheckGarbageCollection(YDoc doc, DeleteSet ds, PendingDeletes waiting)
        {
            // The ranges in the order ApplyDeleteSet takes them: the update's, then the waiting ones.
            var ranges = _ranges;
            ranges.Clear();
            foreach (var (client, list) in ds.Clients)
            {
                foreach (var range in list)
                {
                    ranges.Add((client, range.Clock, range.Clock + range.Length));
                }
            }

            waiting.AddApplicableRanges(this, ranges);
            var splits = MapValueSplits(ranges);
            if (splits == null && !_store.MaySplitMapValues)
            {
                return;
            }

            var deleted = DeletedTypes(doc, ranges);
            if (deleted == null)
            {
                return;
            }

            if (_store.MaySplitMapValues)
            {
                foreach (var type in deleted.Keys)
                {
                    if (type is { Parent: AbstractType, Content: ContentType content } && HasLiveNonValue(content.Type, doc.GcFilter, !type.Keep && doc.GcFilter(type), ranges))
                    {
                        throw MapValueSplit();
                    }
                }
            }

            foreach (var part in LiveNonValues(doc, splits, ranges, deleted))
            {
                // Collected when a type above it is deleted now and collected (it, or one between them).
                var collected = false;
                for (var ancestor = ParentTypeItem(part); ancestor != null; ancestor = ParentTypeItem(ancestor))
                {
                    collected |= !ancestor.Keep && doc.GcFilter(ancestor);
                    if (collected && deleted.ContainsKey(ancestor))
                    {
                        throw MapValueSplit();
                    }
                }
            }
        }

        private static YUpdateException MapValueSplit() => new(
            "The update deletes a type in which a key has two values (its value was split, by this update or an earlier one), " +
            "which the garbage collection cannot remove. The document is unchanged.");

        // The times of the events that split and delete items, in the order the update carries them out: the structs'
        // references split items (and a key's new item deletes its old value), then range k (from 1, in ApplyDeleteSet's
        // order) splits at its start, deletes what it covers, and splits at its end.
        private const int ReferenceSplit = 0;
        private const int ValueReplaced = 1;

        private static int RangeStart(int k) => 3 * k;

        private static int RangeDeletes(int k) => (3 * k) + 1;

        private static int RangeEnd(int k) => (3 * k) + 2;

        /// <summary>
        /// The boundaries (with their time) at which the update's references and ranges cut an item with a key that
        /// is not deleted, by item, or null when there is none (the common case: a key's value is one item).
        /// </summary>
        private Dictionary<Item, List<(long Boundary, int Time)>> MapValueSplits(List<(long Client, long Clock, long End)> ranges)
        {
            Dictionary<Item, List<(long Boundary, int Time)>> splits = null;
            foreach (var (str, _, _) in _steps)
            {
                if (str is Item item)
                {
                    if (item.LeftOrigin is ID left)
                    {
                        Split(ref splits, left, left.Clock + 1, ReferenceSplit);
                    }

                    if (item.RightOrigin is ID right)
                    {
                        Split(ref splits, right, right.Clock, ReferenceSplit);
                    }
                }
            }

            for (var k = 0; k < ranges.Count; k++)
            {
                var (client, start, end) = ranges[k];
                Split(ref splits, new ID(client, start), start, RangeStart(k + 1));
                if (end > start)
                {
                    Split(ref splits, new ID(client, end - 1), end, RangeEnd(k + 1));
                }
            }

            return splits;
        }

        /// <summary>Records <paramref name="boundary"/> if it falls inside the item that holds <paramref name="id"/> and that item may be a key's undeleted value.</summary>
        private void Split(ref Dictionary<Item, List<(long Boundary, int Time)>> splits, ID id, long boundary, int time)
        {
            var (target, start) = Target(id);
            if (target != null && start < boundary && boundary < target.Id.Clock + target.Length
                && !target.Deleted && target.Content is not ContentDeleted && (target.ParentSub != null || target.Parent == null))
            {
                splits ??= new Dictionary<Item, List<(long, int)>>();
                if (!splits.TryGetValue(target, out var list))
                {
                    splits[target] = list = new List<(long, int)>();
                }

                list.Add((boundary, time));
            }
        }

        /// <summary>
        /// The type items this update deletes by its ranges and the values of keys it replaces (when the new item is
        /// estimated to land last, see <see cref="LandsLast"/>), with the time they are deleted, or null.
        /// </summary>
        private Dictionary<Item, int> DeletedTypes(YDoc doc, List<(long Client, long Clock, long End)> ranges)
        {
            Dictionary<Item, int> deleted = null;

            void Add(Item type, int time)
            {
                deleted ??= new Dictionary<Item, int>();
                if (!deleted.TryGetValue(type, out var earlier) || earlier > time)
                {
                    deleted[type] = time;
                }
            }

            var chains = new KeyChains();
            foreach (var (str, _, integrate) in _steps)
            {
                if (integrate && str is Item item && LandsLast(doc, item, chains) is { Deleted: false } replaced)
                {
                    Add(replaced, ValueReplaced);
                }
            }

            for (var k = 0; k < ranges.Count; k++)
            {
                var (client, start, end) = ranges[k];
                var known = _store.GetState(client);
                end = Math.Min(end, GetState(client));
                for (var clock = start; clock < end;)
                {
                    var str = clock < known ? _store.Find(new ID(client, clock)) : FindPlanned(new ID(client, clock));
                    if (str == null)
                    {
                        break;
                    }

                    if (str is Item { Deleted: false, Content: ContentType } type)
                    {
                        Add(type, RangeDeletes(k + 1));
                    }

                    clock = str.Id.Clock + str.Length;
                }
            }

            return deleted;
        }

        /// <summary>
        /// For a planned item with a key: whether it lands last among its key's items (it becomes the key's value) and
        /// so deletes the value before it, which is returned (or null when there was none or it does not land last).
        /// <paramref name="chains"/> holds each key's first item and value so far, taken from the store and updated
        /// with the planned items in plan order (those estimated deleted are kept there too). Where an item lands is estimated as YATA orders the items of one
        /// origin (by client): before its right origin; before the rest of an item its origin splits, unless that rest
        /// is of a lower client; after an origin only when that is the end of the key's value; without origins, after
        /// the key's first item only when that item's client is lower. Items under a deleted type are deleted.
        /// </summary>
        private Item LandsLast(YDoc doc, Item item, KeyChains chains)
        {
            var (parent, key) = KeyOf(item);
            if (parent is Item parentItem && (parentItem.Deleted || chains.Deleted.Contains(parentItem)))
            {
                chains.Deleted.Add(item);
                return null;
            }

            if (item.Content is ContentDeleted || key == null)
            {
                return null;
            }

            // A root is named by planned items and is the type itself for those in the store.
            if (parent is string name && doc.TryGetRoot(name, out var defined))
            {
                parent = defined;
            }

            if (!chains.Keys.TryGetValue((parent, key), out var chain))
            {
                var type = parent switch
                {
                    Item { Parent: AbstractType } typeItem => (typeItem.Content as ContentType)?.Type,
                    AbstractType root => root,
                    _ => null,
                };
                if (type != null && type._map.TryGetValue(key, out var value))
                {
                    var head = value;
                    while (head.Left is Item l)
                    {
                        head = l;
                    }

                    chain = (head, value);
                }
            }

            bool last;
            if (chain.Value == null)
            {
                last = item.RightOrigin == null;
            }
            else if (item.RightOrigin != null)
            {
                last = false;
            }
            else if (item.LeftOrigin is ID origin)
            {
                last = origin.Equals(chain.Value.LastId)
                    || (Target(origin).Item is { } left && !origin.Equals(left.LastId) && left.Id.Client < item.Id.Client && ReferenceEquals(left, chain.Value));
            }
            else
            {
                last = chain.Head.Id.Client < item.Id.Client;
            }

            // Without origins and not last, it lands before the key's first item.
            var first = chain.Head == null || (!last && item.LeftOrigin == null) ? item : chain.Head;
            chains.Keys[(parent, key)] = (first, last ? item : chain.Value);
            if (!last)
            {
                chains.Deleted.Add(item);
            }

            return last ? chain.Value : null;
        }

        /// <summary>The state of <see cref="LandsLast"/>: per key its first item and value, and the planned items estimated deleted on integration.</summary>
        private sealed class KeyChains
        {
            public Dictionary<(object, string), (Item Head, Item Value)> Keys { get; } = new();

            public HashSet<Item> Deleted { get; } = new();
        }

        /// <summary>
        /// Of the items <paramref name="splits"/> cuts, those that are a key's live value (or become one, see
        /// <see cref="LandsLast"/>) and keep a part other than the last undeleted: a boundary cuts a part only while
        /// that part is not deleted yet (by an earlier range, or as the key's value with a type above it).
        /// </summary>
        private List<Item> LiveNonValues(YDoc doc, Dictionary<Item, List<(long Boundary, int Time)>> splits, List<(long Client, long Clock, long End)> ranges, Dictionary<Item, int> deleted)
        {
            var parts = new List<Item>();
            if (splits == null)
            {
                return parts;
            }

            HashSet<Item> values = null;
            foreach (var (target, events) in splits)
            {
                if (KeyOf(target).Key == null)
                {
                    continue;
                }

                if (target.Parent is not AbstractType)
                {
                    if (values == null)
                    {
                        // The planned items that are a key's value once the plan is carried out.
                        values = new HashSet<Item>();
                        var chains = new KeyChains();
                        foreach (var (str, _, integrate) in _steps)
                        {
                            if (integrate && str is Item item)
                            {
                                LandsLast(doc, item, chains);
                            }
                        }

                        foreach (var chain in chains.Keys.Values)
                        {
                            if (!chains.Deleted.Contains(chain.Value))
                            {
                                values.Add(chain.Value);
                            }
                        }
                    }

                    if (!values.Contains(target))
                    {
                        continue;
                    }
                }

                // When the value is replaced, or a type above it is deleted, its last part (then the key's value) is deleted.
                var typeDeleted = deleted.TryGetValue(target, out var replacedAt) ? replacedAt : int.MaxValue;
                for (var ancestor = ParentTypeItem(target); ancestor != null; ancestor = ParentTypeItem(ancestor))
                {
                    if (deleted.TryGetValue(ancestor, out var time))
                    {
                        typeDeleted = Math.Min(typeDeleted, time);
                    }
                }

                var client = target.Id.Client;
                var valueEnd = target.Id.Clock + target.Length;
                var boundaries = new List<long> { Target(target.Id).Start, valueEnd };
                events.Sort((a, b) => a.Time.CompareTo(b.Time));
                foreach (var (boundary, time) in events)
                {
                    var i = boundaries.BinarySearch(boundary);
                    if (i >= 0)
                    {
                        continue;
                    }

                    i = ~i;
                    var (partStart, partEnd) = (boundaries[i - 1], boundaries[i]);
                    if (!((partEnd == valueEnd && typeDeleted < time) || IsCovered(client, partStart, partEnd, ranges, time)))
                    {
                        boundaries.Insert(i, boundary);
                    }
                }

                // Every part but the last must be deleted by a range.
                for (var i = 0; i < boundaries.Count - 2; i++)
                {
                    if (!IsCovered(client, boundaries[i], boundaries[i + 1], ranges, int.MaxValue))
                    {
                        parts.Add(target);
                        break;
                    }
                }
            }

            return parts;
        }

        /// <summary>
        /// Whether a type below a collected one holds an undeleted item that is not the value of its key and that
        /// <paramref name="ranges"/> do not delete (the collection walks every key's items, the value and those left of it).
        /// </summary>
        private static bool HasLiveNonValue(AbstractType type, Predicate<Item> gcFilter, bool collected, List<(long Client, long Clock, long End)> ranges)
        {
            var stack = new List<(AbstractType Type, bool Collected)> { (type, collected) };
            while (stack.Count > 0)
            {
                var (t, c) = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                foreach (var value in t._map.Values)
                {
                    for (var item = value; item != null; item = item.Left as Item)
                    {
                        if (c && item != value && !item.Deleted && !IsCovered(item.Id.Client, item.Id.Clock, item.Id.Clock + item.Length, ranges, int.MaxValue))
                        {
                            return true;
                        }

                        Push(item);
                    }
                }

                for (var item = t._start; item != null; item = item.Right as Item)
                {
                    Push(item);
                }

                void Push(Item item)
                {
                    if (item.Content is ContentType child)
                    {
                        stack.Add((child.Type, c || (!item.Keep && gcFilter(item))));
                    }
                }
            }

            return false;
        }

        /// <summary>Whether the ranges that delete before <paramref name="time"/> cover [<paramref name="start"/>, <paramref name="end"/>) of <paramref name="client"/>.</summary>
        private static bool IsCovered(long client, long start, long end, List<(long Client, long Clock, long End)> ranges, int time)
        {
            var own = new List<(long Clock, long End)>();
            for (var k = 0; k < ranges.Count && RangeDeletes(k + 1) < time; k++)
            {
                if (ranges[k].Client == client)
                {
                    own.Add((ranges[k].Clock, ranges[k].End));
                }
            }

            own.Sort((a, b) => a.Clock.CompareTo(b.Clock));
            var covered = start;
            foreach (var (clock, rangeEnd) in own)
            {
                if (clock > covered)
                {
                    break;
                }

                covered = Math.Max(covered, rangeEnd);
                if (covered >= end)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The item that holds <paramref name="id"/> once the plan is carried out, with the clock it starts at (a
        /// planned item that overlaps the store is integrated from the store's state on), or null for a GC struct.
        /// </summary>
        private (Item Item, long Start) Target(ID id)
        {
            var known = _store.GetState(id.Client);
            if (id.Clock < known)
            {
                return _store.Find(id) is Item item ? (item, item.Id.Clock) : (null, 0);
            }

            return id.Clock < GetState(id.Client) && FindPlanned(id) is Item planned ? (planned, Math.Max(planned.Id.Clock, known)) : (null, 0);
        }

        /// <summary>
        /// The parent (a root's name or the type's item, or the root type) and key <paramref name="item"/> has once
        /// integrated: its own, else those of the item its origin (or right origin) refers to, as
        /// <see cref="Item.ResolveReferences"/> takes them; (null, null) when it is integrated as a GC struct.
        /// </summary>
        private (object Parent, string Key) KeyOf(Item item)
        {
            // Only a planned item has no parent yet; its references are integrated before it. The walk is memoized
            // (a large update's items each refer to the one before).
            var chain = _keyChain;
            chain.Clear();
            (object Parent, string Key) result;
            while (true)
            {
                if (_keys != null && _keys.TryGetValue(item, out result))
                {
                    break;
                }

                if (item.Parent != null)
                {
                    result = (item.Parent switch
                    {
                        AbstractType type => (object)type._item ?? type,
                        ID id => Target(id).Item,
                        var root => root,
                    }, item.ParentSub);
                    break;
                }

                chain.Add(item);
                if ((item.LeftOrigin is ID l && Target(l).Item == null) || (item.RightOrigin is ID r && Target(r).Item == null) || (item.LeftOrigin ?? item.RightOrigin) is not ID origin)
                {
                    result = (null, null);
                    break;
                }

                item = Target(origin).Item;
            }

            if (chain.Count > 0)
            {
                _keys ??= new Dictionary<Item, (object, string)>();
                foreach (var c in chain)
                {
                    _keys[c] = result;
                }
            }

            return result;
        }

        /// <summary>The item of the type <paramref name="item"/> lands in, or null for a root (or a GC struct).</summary>
        private Item ParentTypeItem(Item item) => KeyOf(item).Parent as Item;

        private List<AbstractStruct> _plannedHintList;
        private int _plannedHint;

        private AbstractStruct FindPlanned(ID id)
        {
            if (!_refs.TryGetValue(id.Client, out var list) || list.Count == 0 || id.Clock < list[0].Id.Clock)
            {
                return null;
            }

            var last = list[^1];
            if (id.Clock >= last.Id.Clock + last.Length)
            {
                return null;
            }

            // The planned structs refer mostly to the one before them: the last lookup's neighbourhood comes first.
            if (!ReferenceEquals(list, _plannedHintList))
            {
                _plannedHintList = list;
                _plannedHint = 0;
            }

            return list[ClientStructs.FindIndex(new ClientStructs.Decoded(list), id.Clock, ref _plannedHint)];
        }
    }

    /// <summary>
    /// Deletions of structs the store does not have yet (Yjs' <c>store.pendingDs</c>). Yjs keeps them
    /// as one encoded update and re-reads all of it on every update; here they are kept per client
    /// and a client's ranges are only looked at once its state reaches the lowest of them.
    /// </summary>
    internal sealed class PendingDeletes
    {
        private sealed class Entry
        {
            public List<DeleteSet.DeleteItem> Ranges = new();
            public long MinClock = long.MaxValue;
            public long Bytes;
        }

        private readonly Dictionary<long, Entry> _clients = new();

        /// <summary>The size of the ranges as a v1 delete set would encode them.</summary>
        public long Bytes { get; private set; }

        /// <summary>How many ranges were ever added (it only grows), to tell whether an operation added one.</summary>
        public long Additions { get; private set; }

        public int Count => _clients.Count;

        public void Add(long client, long clock, long length)
        {
            if (!_clients.TryGetValue(client, out var entry))
            {
                entry = new Entry { Bytes = HeaderSize(client) };
                _clients[client] = entry;
                Bytes += entry.Bytes;
            }

            entry.Ranges.Add(new DeleteSet.DeleteItem(clock, length));
            Additions++;
            entry.MinClock = Math.Min(entry.MinClock, clock);
            var size = RangeSize(clock, length);
            entry.Bytes += size;
            Bytes += size;
        }

        /// <summary>By how much <see cref="Bytes"/> would grow if the parts of <paramref name="ds"/> beyond <paramref name="getState"/> were added.</summary>
        public long Growth(DeleteSet ds, IntegrationPlan plan)
        {
            long growth = 0;
            foreach (var (client, deletes) in ds.Clients)
            {
                var state = plan.GetState(client);
                var added = false;
                foreach (var delete in deletes)
                {
                    var clockEnd = delete.Clock + delete.Length;
                    if (clockEnd > state)
                    {
                        var clock = Math.Max(delete.Clock, state);
                        growth += RangeSize(clock, clockEnd - clock);
                        added = true;
                    }
                }

                if (added && !_clients.ContainsKey(client))
                {
                    growth += HeaderSize(client);
                }
            }

            return growth;
        }

        /// <summary>Sorts and merges every client's ranges, dropping duplicates.</summary>
        public void Compact()
        {
            var ds = new DeleteSet();
            foreach (var (client, entry) in _clients)
            {
                ds.Clients[client] = entry.Ranges;
            }

            ds.SortAndMergeDeleteSet();
            Bytes = 0;
            foreach (var (client, entry) in _clients)
            {
                entry.Bytes = HeaderSize(client);
                foreach (var range in entry.Ranges)
                {
                    entry.Bytes += RangeSize(range.Clock, range.Length);
                }

                Bytes += entry.Bytes;
            }
        }

        /// <summary>A copy of every client's ranges, sorted and merged (the v2 encoding needs them ascending).</summary>
        public DeleteSet ToDeleteSet()
        {
            var ds = new DeleteSet();
            foreach (var (client, entry) in _clients)
            {
                ds.Clients[client] = new List<DeleteSet.DeleteItem>(entry.Ranges);
            }

            ds.SortAndMergeDeleteSet();
            return ds;
        }

        /// <summary>Adds the ranges <see cref="TakeApplicable"/> will take once <paramref name="plan"/> is carried out, as (client, clock, end).</summary>
        public void AddApplicableRanges(IntegrationPlan plan, List<(long Client, long Clock, long End)> ranges)
        {
            foreach (var (client, entry) in _clients)
            {
                if (entry.MinClock < plan.GetState(client))
                {
                    foreach (var range in entry.Ranges)
                    {
                        ranges.Add((client, range.Clock, range.Clock + range.Length));
                    }
                }
            }
        }

        /// <summary>Removes and returns the ranges of the clients whose state reached one of them, or null.</summary>
        public DeleteSet TakeApplicable(StructStore store)
        {
            List<long> applicable = null;
            foreach (var (client, entry) in _clients)
            {
                if (entry.MinClock < store.GetState(client))
                {
                    (applicable ??= new List<long>()).Add(client);
                }
            }

            if (applicable == null)
            {
                return null;
            }

            // In the order Yjs reads its pending deletions back (writeDeleteSet writes the clients from the highest id
            // down): it decides the order of the transaction's delete set, and so of the garbage collection filter's calls.
            applicable.Sort(static (a, b) => b.CompareTo(a));
            var ds = new DeleteSet();
            foreach (var client in applicable)
            {
                ds.Clients[client] = _clients[client].Ranges;
                Bytes -= _clients[client].Bytes;
                _clients.Remove(client);
            }

            return ds;
        }

        private static long HeaderSize(long client) => VarUintSize(client) + 1;

        private static long RangeSize(long clock, long length) => VarUintSize(clock) + VarUintSize(length);

        private static int VarUintSize(long value) => value < 1L << 7 ? 1 : value < 1L << 14 ? 2 : value < 1L << 21 ? 3 : value < 1L << 28 ? 4 : 5;
    }
}
