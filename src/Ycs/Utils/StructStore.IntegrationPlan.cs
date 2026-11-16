#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

internal sealed partial class StructStore
{
    /// <summary>The structs read from an update for one client, and how many were consumed.</summary>
    internal sealed class ClientStructRefs
    {
        public int NextReadOperation { get; set; }
        public List<AbstractStruct> Refs { get; set; }
    }

    private static readonly List<AbstractStruct> NoRefs = new();

    // The plan of the last update, kept for the next one (null while one is in use).
    private IntegrationPlan _spare;

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

        /// <summary>The decoded structs set aside, by client, each list contiguous and ascending by clock, or null.</summary>
        public Dictionary<long, List<AbstractStruct>> Rest { get; private set; }

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
            foreach (var list in Rest.Values)
            {
                structs += list.Count;
            }

            var encoder = UpdateEncoderV2.Rent();
            try
            {
                WriteRest(encoder, Rest);

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
                                // Recorded now, as the walk may stop further on and then only records what its last struct
                                // waits for: the retry check would not fire when this client's structs arrive (yjs/yjs#824).
                                UpdateMissingSv(missing.Value, GetState(missing.Value));
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
            Rest ??= new Dictionary<long, List<AbstractStruct>>();
            foreach (var item in _stack)
            {
                var client = item.Id.Client;
                if (_clientsStructRefs.TryGetValue(client, out var inapplicableItems))
                {
                    // Decrement because we weren't able to apply previous operation.
                    inapplicableItems.NextReadOperation--;
                    Rest[client] = inapplicableItems.Refs.GetRange(inapplicableItems.NextReadOperation, inapplicableItems.Refs.Count - inapplicableItems.NextReadOperation);
                    _clientsStructRefs.Remove(client);

                    // The plan may still read it as its current target: it is empty now.
                    inapplicableItems.NextReadOperation = 0;
                    inapplicableItems.Refs = NoRefs;
                    _freeRefs.Add(inapplicableItems);
                }
                else
                {
                    // Item was the last item on clientsStructRefs and the field was already cleared. Add item to restStructs and continue.
                    Rest[client] = new List<AbstractStruct>(1) { item };
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
                    var found = _store.Find(origin);
                    depth = found.IsItem && found.Parent is AbstractType parent ? parent.Depth : -1;
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
                var found = _store.Find(id);
                return found.IsItem && found.Content is ContentType ct ? ct.Type.Depth : -1;
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
                    var str = clock < known ? _store.Find(new ID(client, clock)).ToStruct() : FindPlanned(new ID(client, clock));
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
                    while (head.Left.IsItem)
                    {
                        head = head.Left;
                    }

                    chain = (head.ToItem(), value.ToItem());
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
                        // The planned items that become a key's value when they integrate, a later planned item replacing them or not.
                        values = new HashSet<Item>();
                        var chains = new KeyChains();
                        foreach (var (str, _, integrate) in _steps)
                        {
                            if (integrate && str is Item item)
                            {
                                LandsLast(doc, item, chains);
                                if (!chains.Deleted.Contains(item) && item.Content is not ContentDeleted && KeyOf(item).Key != null)
                                {
                                    values.Add(item);
                                }
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
                    for (var item = value; !item.IsNull; item = item.Left.AsItem)
                    {
                        if (c && item != value && !item.Deleted && !IsCovered(item.Id.Client, item.Id.Clock, item.Id.Clock + item.Length, ranges, int.MaxValue))
                        {
                            return true;
                        }

                        Push(item);
                    }
                }

                for (var item = t._start; !item.IsNull; item = item.Right.AsItem)
                {
                    Push(item);
                }

                void Push(ItemRef item)
                {
                    if (item.Content is ContentType child)
                    {
                        stack.Add((child.Type, c || (!item.Keep && gcFilter(item.ToItem()))));
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
                return _store.Find(id).ToStruct() is Item item ? (item, item.Id.Clock) : (null, 0);
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
                        AbstractType type => type._item.IsNull ? type : type._item.ToItem(),
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
}
