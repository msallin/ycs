#nullable disable // Not annotated for nullable reference types yet.

using System.Diagnostics;

namespace Ycs;

internal sealed partial class StructStore
{
    /// <summary>
    /// How far the plan has read one client's range of the update's staging table (<see cref="StagingTable.RangeOf"/>).
    /// The plan keeps one per client index.
    /// </summary>
    internal struct ClientStructRefs
    {
        /// <summary>The table index of the next struct to read.</summary>
        public int Next;

        /// <summary>The table index after the client's last struct; the range is empty when <see cref="Next"/> reaches it.</summary>
        public int End;

        /// <summary>Whether the client's structs went to <see cref="Rest"/>; the range is then empty.</summary>
        public bool SetAside;
    }

    // The plan of the last update, kept for the next one (null while one is in use).
    private IntegrationPlan _spare;

    /// <summary>
    /// How the structs of one update integrate (Yjs' <c>integrateStructs</c>), worked out against the
    /// store's state without changing the store, then carried out by <see cref="Execute"/>. It reads the update's
    /// <see cref="StagingTable"/>: a struct is its index there, and a client is its index in the table, where each
    /// client's structs are one range in clock order.
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
    internal sealed partial class IntegrationPlan
    {
        private readonly StructStore _store;
        private readonly Dictionary<long, long> _state = new();
        private readonly List<(int Index, int Offset, bool Integrate)> _steps = new();
        private readonly Comparison<int> _byClientId;

        // Planning state, kept between updates. _table is null while no update is planned: what holds an index of a
        // table is cleared before the table is reset or reused (Clear).
        private StagingTable _table;
        private ClientStructRefs[] _clientsStructRefs = [];
        private readonly List<int> _clientsStructRefsIds = new();
        private readonly List<int> _stack = new();
        private readonly List<PlanItem> _chain = new();

        // The decoded objects of the update by client index, at the positions of the table, which Execute adopts (StructAt).
        private readonly List<List<AbstractStruct>> _objects = new();

        // The last client and struct a planned id was looked up in: planned structs refer mostly to the one before them.
        private int _plannedHint;

        // Nesting depth of the types this update creates (by the id of their item), and of the type
        // each item lands in (when it was needed), for the nesting limit.
        private Dictionary<ID, int> _typeDepths;
        private int _maxDepth;
        private Dictionary<PlanItem, int> _parentDepths;

        public IntegrationPlan(StructStore store)
        {
            _store = store;
            _byClientId = (a, b) => _table.ClientAt(a).CompareTo(_table.ClientAt(b));
        }

        /// <summary>The staging table the plan was made from; null while the plan is clear.</summary>
        public StagingTable Table => _table;

        /// <summary>
        /// The structs set aside, by client index of the table: each is a range of the table (start, count), contiguous
        /// and ascending by clock, or null when nothing waits.
        /// </summary>
        public Dictionary<int, (int Start, int Count)> Rest { get; private set; }

        /// <summary>Per client, the lowest clock the structs set aside wait for (set with <see cref="Rest"/>).</summary>
        public Dictionary<long, long> Missing { get; private set; }

        /// <summary>A client's clock once the plan is carried out.</summary>
        public long GetState(long client) => _state.TryGetValue(client, out var clock) ? clock : _store.GetState(client);

        public void Execute(Transaction transaction)
        {
            foreach (var (index, offset, integrate) in _steps)
            {
                ItemRef.Adopt(StructAt(index), transaction, offset, integrate);
            }
        }

        /// <summary>
        /// The decoded object of the staged struct <paramref name="index"/>: the one at the same position of the same
        /// client's list. The one place that pairs the table with the objects.
        /// </summary>
        private AbstractStruct StructAt(int index)
        {
            var client = _table.At(index).Client;
            return _objects[client][index - _table.RangeOf(client).Start];
        }

        /// <summary>
        /// The structs set aside as a v2 update (with an empty delete set), or null, and how many they are. Written from
        /// the table, so the lease that holds it must be open.
        /// </summary>
        public (byte[] Update, long Structs) EncodeRest()
        {
            if (Rest == null)
            {
                return (null, 0);
            }

            var encoder = UpdateEncoderV2.Rent();
            try
            {
                var writer = new LazyStructWriter(encoder);
                var order = SortedRestClients();
                foreach (var client in order)
                {
                    var (start, count) = Rest[client];
                    for (var i = start; i < start + count; i++)
                    {
                        LazyStructWriter.WriteStruct(writer, _table, i, 0);
                    }
                }

                LazyStructWriter.Finish(writer);

                // Write an empty delete set: there is no need for an extra function call, just write 0 deletes.
                encoder.RestWriter.WriteVarUint(0);
                var update = encoder.ToArray();
                if (CompareRestForTests && !update.AsSpan().SequenceEqual(EncodeRestFromObjects()))
                {
                    throw new InvalidOperationException("The structs set aside, written from the staging table, differ from the same written from the decoded objects.");
                }

                return (update, writer.TotalWritten);
            }
            finally
            {
                encoder.Return();
            }
        }

        /// <summary>Whether <see cref="EncodeRest"/> checks its bytes against <see cref="EncodeRestFromObjects"/>; the tests switch it on.</summary>
        internal static bool CompareRestForTests;

        /// <summary>
        /// The structs set aside as <see cref="EncodeRest"/> writes them, from the decoded objects (what Yjs does, which
        /// the table's encoding must equal byte for byte). The test of that equality compares them.
        /// </summary>
        internal byte[] EncodeRestFromObjects()
        {
            var rest = new Dictionary<long, List<AbstractStruct>>();
            foreach (var (client, (start, count)) in Rest)
            {
                rest[_table.ClientAt(client)] = _objects[client].GetRange(start - _table.RangeOf(client).Start, count);
            }

            var encoder = UpdateEncoderV2.Rent();
            try
            {
                WriteRest(encoder, rest);
                encoder.RestWriter.WriteVarUint(0);
                return encoder.ToArray();
            }
            finally
            {
                encoder.Return();
            }
        }

        /// <summary>The client indexes of <see cref="Rest"/>, highest client id first (Yjs' <c>writeClientsStructs</c>).</summary>
        private int[] SortedRestClients()
        {
            var clients = new int[Rest.Count];
            Rest.Keys.CopyTo(clients, 0);
            Array.Sort(clients, _byClientId);
            Array.Reverse(clients);
            return clients;
        }

        /// <summary>Whether a struct set aside (not a Skip) covers a clock that <paramref name="clocks"/> holds.</summary>
        public bool RestOverlaps(ClockRanges clocks)
        {
            foreach (var (client, (start, count)) in Rest)
            {
                var id = _table.ClientAt(client);
                for (var i = start; i < start + count; i++)
                {
                    ref readonly var s = ref _table.At(i);
                    if (s.Kind != StagedKind.Skip && clocks.Overlaps(id, s.Clock, s.Clock + s.Length))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>Adds the clocks of the structs set aside (not the Skips) to <paramref name="clocks"/>.</summary>
        public void RecordRest(ClockRanges clocks)
        {
            foreach (var (client, (start, count)) in Rest)
            {
                var id = _table.ClientAt(client);
                for (var i = start; i < start + count; i++)
                {
                    ref readonly var s = ref _table.At(i);
                    if (s.Kind != StagedKind.Skip)
                    {
                        clocks.Add(id, s.Clock, s.Clock + s.Length);
                    }
                }
            }
        }

        /// <summary>Forgets the update, so that the plan can be used for the next one.</summary>
        public void Clear()
        {
            _table = null;
            _state.Clear();
            _steps.Clear();
            _stack.Clear();
            _clientsStructRefsIds.Clear();
            _objects.Clear();
            _plannedHint = 0;
            _typeDepths?.Clear();
            _parentDepths?.Clear();
            _keys?.Clear();
            Rest = null;
            Missing = null;
        }

        /// <summary>
        /// Works out how the structs of <paramref name="table"/> integrate, with types nested at most
        /// <paramref name="maxDepth"/> levels. <paramref name="objects"/> are the same structs decoded, by client id, each
        /// list as long as the client's range. A plan that threw (or was used before) is cleared first, so nothing of an
        /// earlier update is left in it. The table stays as it is until <see cref="Clear"/> and is not reset by the plan.
        /// </summary>
        public void Plan(StagingTable table, Dictionary<long, List<AbstractStruct>> objects, int maxDepth)
        {
            Clear();
            _table = table;
            _maxDepth = maxDepth;
            if (_clientsStructRefs.Length < table.ClientCount)
            {
                _clientsStructRefs = new ClientStructRefs[Math.Max(table.ClientCount, _clientsStructRefs.Length * 2)];
            }

            var clientsStructRefs = _clientsStructRefs;
            for (var c = 0; c < table.ClientCount; c++)
            {
                clientsStructRefs[c] = default;
                _objects.Add(null);
            }

            foreach (var (client, list) in objects)
            {
                _objects[table.FindClient(client)] = list;
            }

            var planned = 0;
            _store.ForgetReservations();
            for (var i = 0; i < table.Count;)
            {
                var client = table.At(i).Client;
                var (start, count) = table.RangeOf(client);
                Debug.Assert(start == i && count > 0 && _objects[client].Count == count);

                // Room for what the update adds (the update's own size bounds it), so a large update does not
                // grow the client's list and the plan step by step.
                planned += count;
                _store.ReserveStructs(table.ClientAt(client), count);

                clientsStructRefs[client] = new ClientStructRefs { Next = start, End = start + count };
                _clientsStructRefsIds.Add(client);
                i = start + count;
            }

            _steps.EnsureCapacity(planned);

            // Sort them so that we take the higher id first, in case of conflicts the lower id will probably not conflict with the id from the higher user.
            // By the client's id: its index in the table is the order the update names the clients in.
            _clientsStructRefsIds.Sort(_byClientId);

            var curStructsTarget = GetNextStructTarget();
            if (curStructsTarget < 0)
            {
                return;
            }

            var stack = _stack;
            var stackHead = clientsStructRefs[curStructsTarget].Next++;

            // Iterate over all struct readers until we are done.
            while (true)
            {
                ref readonly var head = ref table.At(stackHead);
                if (head.Kind != StagedKind.Skip)
                {
                    var headClient = table.ClientAt(head.Client);
                    var offset = GetState(headClient) - head.Clock;
                    if (offset < 0)
                    {
                        // Update from the same client is missing.
                        stack.Add(stackHead);
                        UpdateMissingSv(headClient, head.Clock - 1);

                        // Hit a dead wall, add all items from stack to restSS.
                        AddStackToRestSS();
                    }
                    else
                    {
                        var missing = FindMissing(stackHead);
                        if (missing >= 0)
                        {
                            if (offset > 0)
                            {
                                // What an item refers to is there when it is integrated, and a part of it that is there was
                                // integrated by an item of the same id: a second struct under that id (no update holds two:
                                // a client is listed once, so one was waiting). Waiting, it would hold up what follows it
                                // in its client for good, which the document, having the id, no longer lacks.
                                throw new YUpdateException(
                                    $"The struct {headClient}:{head.Clock} refers to structs this document does not have, " +
                                    "but the document has a struct under its id already. The document is unchanged.");
                            }

                            stack.Add(stackHead);

                            // Get the struct reader that has the missing struct.
                            var missingClient = table.ClientAt(missing);
                            if (clientsStructRefs[missing].End == clientsStructRefs[missing].Next)
                            {
                                // This update message causally depends on another update message that doesn't exist yet.
                                UpdateMissingSv(missingClient, GetState(missingClient));
                                AddStackToRestSS();
                            }
                            else
                            {
                                // Recorded now, as the walk may stop further on and then only records what its last struct
                                // waits for: the retry check would not fire when this client's structs arrive (yjs/yjs#824).
                                UpdateMissingSv(missingClient, GetState(missingClient));
                                stackHead = clientsStructRefs[missing].Next++;
                                continue;
                            }
                        }
                        else if (offset < head.Length)
                        {
                            // All fine, apply the stackhead.
                            if (head.Kind == StagedKind.Type)
                            {
                                CheckNesting(PlanItem.Staged(table, stackHead));
                            }

                            _steps.Add((stackHead, (int)offset, true));
                            _state[headClient] = head.Clock + head.Length;
                        }
                        else if (head.Kind != StagedKind.GC)
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
                else if (curStructsTarget >= 0 && clientsStructRefs[curStructsTarget].Next < clientsStructRefs[curStructsTarget].End)
                {
                    stackHead = clientsStructRefs[curStructsTarget].Next++;
                }
                else
                {
                    curStructsTarget = GetNextStructTarget();
                    if (curStructsTarget < 0)
                    {
                        // We are done!
                        break;
                    }

                    stackHead = clientsStructRefs[curStructsTarget].Next++;
                }
            }
        }

        /// <summary>The client index of the highest client that has structs left to read, -1 when none has; exhausted clients (and those set aside) are dropped.</summary>
        private int GetNextStructTarget()
        {
            var ids = _clientsStructRefsIds;
            while (ids.Count > 0)
            {
                var client = ids[^1];
                if (_clientsStructRefs[client].Next < _clientsStructRefs[client].End)
                {
                    return client;
                }

                ids.RemoveAt(ids.Count - 1);
            }

            return -1;
        }

        /// <summary>
        /// The index in the table of the first client struct <paramref name="i"/> refers to that the plan's state does not
        /// have yet (the check half of Yjs' <c>Item.getMissing</c>: left origin, right origin, then parent), or -1. A
        /// reference to the item's own client is an earlier clock (checked when it was read), so it is always there.
        /// </summary>
        private int FindMissing(int i)
        {
            ref readonly var s = ref _table.At(i);
            if (s.Kind is StagedKind.GC or StagedKind.Skip)
            {
                return -1;
            }

            if (s.OriginClient >= 0 && s.OriginClient != s.Client && s.OriginClock >= GetState(_table.ClientAt(s.OriginClient)))
            {
                return s.OriginClient;
            }

            if (s.ROriginClient >= 0 && s.ROriginClient != s.Client && s.ROriginClock >= GetState(_table.ClientAt(s.ROriginClient)))
            {
                return s.ROriginClient;
            }

            if (s.ParentKind == StagedParent.Id && s.ParentClient != s.Client && s.ParentClock >= GetState(_table.ClientAt(s.ParentClient)))
            {
                return s.ParentClient;
            }

            return -1;
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
            Rest ??= new Dictionary<int, (int Start, int Count)>();
            foreach (var index in _stack)
            {
                var client = _table.At(index).Client;
                ref var inapplicableItems = ref _clientsStructRefs[client];
                if (!inapplicableItems.SetAside)
                {
                    // Decrement because we weren't able to apply previous operation.
                    inapplicableItems.Next--;
                    Rest[client] = (inapplicableItems.Next, inapplicableItems.End - inapplicableItems.Next);

                    // Empty, so that the walk skips it (GetNextStructTarget) and no struct of it is read again.
                    inapplicableItems.Next = inapplicableItems.End;
                    inapplicableItems.SetAside = true;
                }
                else
                {
                    // The client's structs went to the rest already (an earlier struct of the stack): as Yjs, the struct alone replaces them.
                    Rest[client] = (index, 1);
                }
            }

            _stack.Clear();
        }

        /// <summary>
        /// Rejects a type that would be nested deeper than <see cref="UpdateLimits.MaxDepth"/>. The depth of
        /// the type an item lands in follows its references as <see cref="ItemRef.Adopt"/> will:
        /// the given parent, else the parent of its left origin, else of its right origin. (When the
        /// other origin is a GC struct the item becomes one too; ignoring that only overestimates.)
        /// </summary>
        private void CheckNesting(PlanItem typeItem)
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
        private int ParentDepth(PlanItem item)
        {
            _parentDepths ??= new Dictionary<PlanItem, int>();
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
                if (item.ParentName != null)
                {
                    depth = 0;
                    break;
                }

                if (item.ParentId is ID parentId)
                {
                    depth = TypeDepth(parentId);
                    break;
                }

                var origin = (item.LeftOrigin ?? item.RightOrigin).Value;
                if (origin.Clock < _store.GetState(origin.Client))
                {
                    var found = _store.Find(origin);
                    depth = found.IsItem && found.Parent is AbstractType parent ? parent.Depth : -1;
                    break;
                }

                var next = FindPlanned(origin);
                if (next < 0 || _table.At(next).Kind is StagedKind.GC or StagedKind.Skip)
                {
                    depth = -1;
                    break;
                }

                item = PlanItem.Staged(_table, next);
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
                var found = _store.Find(id);
                return found.Kind == SlotKind.Type ? found.TypeOf.Depth : -1;
            }

            return _typeDepths != null && _typeDepths.TryGetValue(id, out var depth) ? depth : -1;
        }

        /// <summary>The index in the table of the planned struct that holds <paramref name="id"/>, -1 for none.</summary>
        private int FindPlanned(ID id)
        {
            var client = _table.FindClient(id.Client);
            return client < 0 ? -1 : _table.FindByClock(client, id.Clock, ref _plannedHint);
        }
    }
}
