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
    internal sealed partial class IntegrationPlan
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
        private readonly List<PlanItem> _chain = new();
        private Dictionary<long, List<AbstractStruct>> _refs;

        // Nesting depth of the types this update creates (by the id of their item), and of the type
        // each item lands in (when it was needed), for the nesting limit.
        private Dictionary<ID, int> _typeDepths;
        private int _maxDepth;
        private Dictionary<PlanItem, int> _parentDepths;

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
                ItemRef.Adopt(str, transaction, offset, integrate);
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
                            if (offset > 0)
                            {
                                // What an item refers to is there when it is integrated, and a part of it that is there was
                                // integrated by an item of the same id: a second struct under that id (no update holds two:
                                // a client is listed once, so one was waiting). Waiting, it would hold up what follows it
                                // in its client for good, which the document, having the id, no longer lacks.
                                throw new YUpdateException(
                                    $"The struct {stackHead.Id.Client}:{stackHead.Id.Clock} refers to structs this document does not have, " +
                                    "but the document has a struct under its id already. The document is unchanged.");
                            }

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
                                CheckNesting(PlanItem.Decoded(typeItem));
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

                item = PlanItem.Decoded(next);
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
