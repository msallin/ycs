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

    /// <summary>
    /// Structs that could not be integrated yet (Yjs' <c>store.pendingStructs</c>), and per client the
    /// lowest clock that is missing for them. Yjs keeps one encoded update and merges every new remainder
    /// into it, which costs the size of everything pending per update; here the remainders (encoded v2
    /// updates) are kept as they come and merged once, when they are retried or compacted.
    /// <br/>
    /// They are re-read from their encoding when they are retried, so a root type realized in between
    /// (e.g. with <see cref="YDoc.GetText"/>) is resolved then.
    /// </summary>
    internal sealed class PendingStructs
    {
        public Dictionary<long, long> Missing { get; } = new();
        public List<byte[]> Updates { get; } = new();
        public long Bytes { get; private set; }

        public void Add(byte[] update)
        {
            Updates.Add(update);
            Bytes += update.Length;
        }

        public byte[] Merge()
        {
            var merged = UpdateOperations.MergeUpdatesTrusted(Updates);
            Updates.Clear();
            Updates.Add(merged);
            Bytes = merged.Length;
            return merged;
        }
    }

    // TODO: [alekseyk] To private?
    public readonly Dictionary<long, List<AbstractStruct>> Clients = new Dictionary<long, List<AbstractStruct>>();

    // The plan of the last update, kept for the next one (null while one is in use).
    private IntegrationPlan _spare;

    internal PendingStructs Pending { get; set; }

    /// <summary>Deletions of structs this store does not have yet (Yjs' <c>store.pendingDs</c>).</summary>
    internal PendingDeletes PendingDs { get; } = new();

    internal bool HasPending => Pending != null || PendingDs.Count != 0;

    /// <summary>The encoded size of what is pending, as <see cref="YDocOptions.MaxPendingBytes"/> counts it.</summary>
    internal long PendingBytes => (Pending?.Bytes ?? 0) + PendingDs.Bytes;

    /// <summary>
    /// Counts the structs added and split. Every change to the content of a type adds a struct, splits
    /// one (which can rewrite characters: a split surrogate pair) or deletes, and a delete of countable
    /// content lowers the type's <see cref="AbstractType.Length"/>: so while this and the length are
    /// unchanged, so is the content (<see cref="YText.ToString"/> caches on it).
    /// </summary>
    internal long Version { get; private set; }

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
            var str = kvp.Value[kvp.Value.Count - 1];
            into[kvp.Key] = str.Id.Clock + str.Length;
        }
    }

    public long GetState(long clientId)
    {
        if (Clients.TryGetValue(clientId, out var structs))
        {
            var lastStruct = structs[structs.Count - 1];
            return lastStruct.Id.Clock + lastStruct.Length;
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
            structs = new List<AbstractStruct>();
            Clients[str.Id.Client] = structs;
        }
        else
        {
            var lastStruct = structs[structs.Count - 1];
            if (lastStruct.Id.Clock + lastStruct.Length != str.Id.Clock)
            {
                throw new Exception("Unexpected");
            }
        }

        structs.Add(str);
        Version++;
    }

    private Item SplitItem(Transaction transaction, Item item, int diff)
    {
        Version++;
        return item.SplitItem(transaction, diff);
    }

    /// <summary>
    /// Perform a binary search on a sorted array.
    /// </summary>
    // TODO: [alekseyk] IList<AbstractStruct> to custom class, and move this method there?
    public static int FindIndexSS(IList<AbstractStruct> structs, long clock)
    {
        Debug.Assert(structs.Count > 0);

        var left = 0;
        var right = structs.Count - 1;
        var mid = structs[right];
        var midClock = mid.Id.Clock;

        if (midClock == clock)
        {
            return right;
        }

        var end = midClock + mid.Length;
        if (clock < 0 || clock >= end)
        {
            throw new InvalidOperationException($"Clock {clock} is not in the store (it ends at {end}).");
        }

        // @todo does it even make sense to pivot the search?
        // If a good split misses, it might actually increase the time to find the correct item.
        // Currently, the only advantage is that search with pivoting might find the item on the first try.
        // clock < end and clock != midClock, so end > 1: no division by zero, and the pivot is at most right.
        int midIndex = (int)((clock * right) / (end - 1));
        while (left <= right)
        {
            mid = structs[midIndex];
            midClock = mid.Id.Clock;

            if (midClock <= clock)
            {
                if (clock < midClock + mid.Length)
                {
                    return midIndex;
                }

                left = midIndex + 1;
            }
            else
            {
                right = midIndex - 1;
            }

            midIndex = (left + right) / 2;
        }

        // Always check state before looking for a struct in StructStore
        // Therefore the case of not finding a struct is unexpected.
        throw new InvalidOperationException($"Clock {clock} is not in the store.");
    }

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

    public int FindIndexCleanStart(Transaction transaction, List<AbstractStruct> structs, long clock)
    {
        int index = FindIndexSS(structs, clock);
        var str = structs[index];
        if (str.Id.Clock < clock && str is Item item)
        {
            structs.Insert(index + 1, SplitItem(transaction, item, (int)(clock - item.Id.Clock)));
            return index + 1;
        }

        return index;
    }

    public AbstractStruct GetItemCleanStart(Transaction transaction, ID id)
    {
        if (!Clients.TryGetValue(id.Client, out var structs))
        {
            throw new Exception();
        }

        int indexCleanStart = FindIndexCleanStart(transaction, structs, id.Clock);
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
            structs.Insert(index + 1, SplitItem(transaction, (Item)str, (int)(id.Clock - str.Id.Clock + 1)));
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
        structs[index] = newStruct;
    }

    public void IterateStructs(Transaction transaction, List<AbstractStruct> structs, long clockStart, long length, Predicate<AbstractStruct> fun)
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
    /// order, which wait for missing structs, and whether nesting or <paramref name="maxPendingBytes"/>
    /// would be exceeded (a <see cref="YUpdateException"/>, and the document is unchanged). Then the
    /// plan is carried out, the delete set applied, and pending structs that can now integrate are
    /// retried, as in Yjs.
    /// </summary>
    internal void ApplyUpdate(Transaction transaction, Dictionary<long, List<AbstractStruct>> structs, DeleteSet ds, long maxPendingBytes)
    {
        var plan = _spare ?? new IntegrationPlan(this);
        _spare = null;
        plan.Plan(structs);
        var restUpdate = plan.EncodeRest();
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

        // A retry integrates what is pending (and re-checks what is left), so only an update that adds
        // to it without a retry is held to the limit.
        if (!retry && maxPendingBytes < long.MaxValue)
        {
            var growth = (restUpdate?.Length ?? 0) + PendingDs.Growth(ds, plan);
            if (growth > 0 && PendingBytes + growth > maxPendingBytes)
            {
                // Duplicates (the same update received again) shrink when merged.
                Pending?.Merge();
                PendingDs.Compact();
                if (PendingBytes + growth > maxPendingBytes)
                {
                    throw new YUpdateException(
                        $"The update depends on structs this document does not have, and keeping it until they arrive would grow " +
                        $"the pending data beyond {maxPendingBytes} bytes (YDocOptions.MaxPendingBytes). The document is unchanged.");
                }
            }
        }

        plan.Execute(transaction);

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

            Pending.Add(restUpdate);
        }

        plan.Clear();
        _spare = plan;

        // As in Yjs: the update's deletions first, then those that were waiting.
        var waiting = PendingDs.TakeApplicable(this);
        ApplyDeleteSet(transaction, ds);
        if (waiting != null)
        {
            ApplyDeleteSet(transaction, waiting);
        }

        if (retry)
        {
            // Something the pending structs were waiting for arrived: read them again, from their
            // encoded form, as Yjs does.
            var update = Pending.Merge();
            Pending = null;
            using var lease = UpdateReader.Read(update, 0, update.Length, v2: true, trusted: true);
            ApplyUpdate(transaction, lease.Structs, lease.DeleteSet, long.MaxValue);
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

                var index = FindIndexSS(structs, clock);

                // We can ignore the case of GC and Delete structs, because we are going to skip them.
                var str = structs[index];

                // Split the first item if necessary.
                if (!str.Deleted && str.Id.Clock < clock)
                {
                    structs.Insert(index + 1, SplitItem(transaction, (Item)str, (int)(clock - str.Id.Clock)));

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
                            structs.Insert(index, SplitItem(transaction, (Item)str, (int)(clockEnd - str.Id.Clock)));
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
        private Dictionary<Item, int> _parentDepths;

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

        /// <summary>The structs set aside as a v2 update (with an empty delete set), or null.</summary>
        public byte[] EncodeRest()
        {
            if (Rest == null)
            {
                return null;
            }

            var encoder = UpdateEncoderV2.Rent();
            try
            {
                EncodingUtils.WriteClientsStructs(encoder, Rest, EmptyStateVector);

                // Write an empty delete set: there is no need for an extra function call, just write 0 deletes.
                encoder.RestWriter.WriteVarUint(0);
                return encoder.ToArray();
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
            Rest = null;
            Missing = null;
        }

        /// <summary>
        /// Works out how <paramref name="refs"/> integrate. A plan that threw (or was used before) is
        /// cleared first, so nothing of an earlier update is left in it.
        /// </summary>
        public void Plan(Dictionary<long, List<AbstractStruct>> refs)
        {
            Clear();
            _refs = refs;
            var clientsStructRefs = _clientsStructRefs;
            foreach (var (client, list) in refs)
            {
                if (list.Count > 0)
                {
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
                    Rest.Clients[client] = inapplicableItems.Refs.GetRange(inapplicableItems.NextReadOperation, inapplicableItems.Refs.Count - inapplicableItems.NextReadOperation);
                    _clientsStructRefs.Remove(client);

                    // The plan may still read it as its current target: it is empty now.
                    inapplicableItems.NextReadOperation = 0;
                    inapplicableItems.Refs = NoRefs;
                    _freeRefs.Add(inapplicableItems);
                }
                else
                {
                    // Item was the last item on clientsStructRefs and the field was already cleared. Add item to restStructs and continue.
                    Rest.Clients[client] = new List<AbstractStruct> { item };
                }

                // Remove client from clientsStructRefsIds to prevent users from applying the same update again.
                _clientsStructRefsIds.Remove(client);
            }

            _stack.Clear();
        }

        /// <summary>
        /// Rejects a type that would be nested deeper than <see cref="Limits.MaxDepth"/>. The depth of
        /// the type an item lands in follows its references as <see cref="Item.ResolveReferences"/> will:
        /// the given parent, else the parent of its right origin, else of its left origin. (When the
        /// other origin is a GC struct the item becomes one too; ignoring that only overestimates.)
        /// </summary>
        private void CheckNesting(Item typeItem)
        {
            var parentDepth = ParentDepth(typeItem);
            if (parentDepth >= 0)
            {
                Limits.CheckDepth(parentDepth + 1);
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

                var origin = (item.RightOrigin ?? item.LeftOrigin).Value;
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

        private AbstractStruct FindPlanned(ID id)
        {
            if (!_refs.TryGetValue(id.Client, out var list) || list.Count == 0 || id.Clock < list[0].Id.Clock)
            {
                return null;
            }

            var last = list[^1];
            return id.Clock < last.Id.Clock + last.Length ? list[FindIndexSS(list, id.Clock)] : null;
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

        /// <summary>Removes and returns the ranges of the clients whose state reached one of them, or null.</summary>
        public DeleteSet TakeApplicable(StructStore store)
        {
            DeleteSet ds = null;
            foreach (var (client, entry) in _clients)
            {
                if (entry.MinClock < store.GetState(client))
                {
                    ds ??= new DeleteSet();
                    ds.Clients[client] = entry.Ranges;
                }
            }

            if (ds != null)
            {
                foreach (var client in ds.Clients.Keys)
                {
                    Bytes -= _clients[client].Bytes;
                    _clients.Remove(client);
                }
            }

            return ds;
        }

        private static long HeaderSize(long client) => VarUintSize(client) + 1;

        private static long RangeSize(long clock, long length) => VarUintSize(clock) + VarUintSize(length);

        private static int VarUintSize(long value) => value < 1L << 7 ? 1 : value < 1L << 14 ? 2 : value < 1L << 21 ? 3 : value < 1L << 28 ? 4 : 5;
    }
}
