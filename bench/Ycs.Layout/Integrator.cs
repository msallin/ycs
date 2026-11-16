namespace Ycs.Layout;

/// <summary>
/// A decoded struct as the design's staging table holds it (section 2.5): ids, origins, the parent as a root name or an
/// id (or neither, when an origin gives it), the key, and the content in arrays of its own, copied into the store's
/// arenas when the struct is integrated.
/// </summary>
internal struct Staged
{
    public long Client;
    public long Clock;
    public int Length;
    public ContentKind Kind;
    public bool Countable;
    public bool HasOrigin;
    public long OriginClient;
    public long OriginClock;
    public bool HasROrigin;
    public long ROriginClient;
    public long ROriginClock;
    public string ParentRoot;
    public bool HasParentId;
    public long ParentClient;
    public long ParentClock;
    public string Key;
    public char[] Chars;
    public object[] Values;
    public object Cold;
}

/// <summary>
/// Yjs' editing over a <see cref="LayoutStore"/>: <see cref="Integrate"/> is <c>Item.getMissing</c> and
/// <c>Item.integrate</c> as ycs implements them (<c>Item.ResolveReferences</c>, <c>Item.Integrate</c>,
/// <c>StructStore.ResolveConflict</c>), the same decisions in the same order, on handles; <see cref="ApplyDeleteSet"/>
/// is <c>readAndApplyDeleteSet</c> for ranges the store holds. What a transaction records is kept as ycs keeps it (the
/// delete set, the changed types with their keys, the state before); there is no cleanup (no merge, no GC, no events)
/// and no run-head index. The clock orders are <see cref="LeafOrder"/>s of <paramref name="leafCapacity"/> built from
/// the store's; <see cref="WriteBack"/> puts them back into the store for the checks.
/// </summary>
internal sealed class Integrator(LayoutStore store, int leafCapacity)
{
    private const int KeepConflictSetCapacity = 1024;

    private LeafOrder[] _orders = [.. store.Orders.Select(o => new LeafOrder(leafCapacity, o))];

    private readonly Dictionary<string, int> _roots = store.Types
        .Select((t, i) => (t, i)).Where(x => x.t.RootName != null).ToDictionary(x => x.t.RootName, x => x.i);

    private HashSet<int> _itemsBeforeOrigin;
    private HashSet<int> _conflictingItems;

    // The transaction: the state before it by client index, the delete set, the changed types (null: the list only).
    private uint[] _before = [];
    public readonly List<(int Client, uint Clock, int Length)> DeleteSet = [];
    public readonly Dictionary<int, HashSet<string>> Changed = [];

    public long IntegrationSteps = long.MaxValue;

    /// <summary>Starts a transaction: records the state, empties the delete set and the changed types.</summary>
    public void Begin()
    {
        _before = new uint[_orders.Length];
        for (var c = 0; c < _before.Length; c++)
        {
            ref var last = ref store.Slots[_orders[c].LastHandle];
            _before[c] = last.Clock + (uint)last.Length;
        }

        DeleteSet.Clear();
        Changed.Clear();
    }

    private ref Slot S(int h) => ref store.Slots[h];

    private bool IsGc(int h) => S(h).Kind == ContentKind.Gc;

    private bool IsDeleted(int h) => S(h).Deleted || IsGc(h);

    private int ClientOf(long client)
    {
        if (store.ClientIndex.TryGetValue(client, out var c))
        {
            return c;
        }

        c = store.ClientIds.Length;
        Array.Resize(ref store.ClientIds, c + 1);
        Array.Resize(ref store.Orders, c + 1);
        Array.Resize(ref _orders, c + 1);
        store.ClientIds[c] = client;
        store.Orders[c] = new ClientOrder();
        _orders[c] = new LeafOrder(leafCapacity, store.Orders[c]);
        store.ClientIndex[client] = c;
        return c;
    }

    private int Root(string name)
    {
        if (!_roots.TryGetValue(name, out var t))
        {
            t = store.Types.Count;
            store.Types.Add(new TypeEntry { RootName = name });
            _roots[name] = t;
        }

        return t;
    }

    /// <summary>Replaces the store's clock orders with this integrator's.</summary>
    public void WriteBack()
    {
        for (var c = 0; c < _orders.Length; c++)
        {
            store.Orders[c] = _orders[c].ToClientOrder();
        }
    }

    private int Find(int client, uint clock)
    {
        var order = _orders[client];
        return order.Handle(order.Find(clock));
    }

    // Splits the struct at c so that a struct starts at clock; returns the right part's position.
    private Cursor Split(LeafOrder order, Cursor c, uint clock)
    {
        var right = store.SplitSlot(order.Handle(c), clock);
        return order.InsertAfter(c, clock, right);
    }

    // StructStore.GetItemCleanEnd: the struct that ends at the clock, the item split after it when needed.
    private int CleanEnd(int client, uint clock)
    {
        var order = _orders[client];
        var c = order.Find(clock);
        var h = order.Handle(c);
        if (clock != S(h).Clock + (uint)S(h).Length - 1 && !IsGc(h))
        {
            Split(order, c, clock + 1);
        }

        return h;
    }

    // StructStore.GetItemCleanStart: the struct that starts at the clock, an item split before it when needed.
    private int CleanStart(int client, uint clock)
    {
        var order = _orders[client];
        var c = order.Find(clock);
        var h = order.Handle(c);
        return S(h).Clock < clock && !IsGc(h) ? order.Handle(Split(order, c, clock)) : h;
    }

    /// <summary>Integrates one struct whose clock is its client's state (offset 0, as every struct of the workloads).</summary>
    public void Integrate(in Staged s)
    {
        if (s.Kind == ContentKind.Gc)
        {
            // GC.Integrate: the struct is added to its client's order, nothing else.
            var g = store.NewSlot();
            store.Slots[g] = new Slot { Clock = (uint)s.Clock, Length = s.Length, Client = ClientOf(s.Client), Left = -1, Right = -1, OriginClient = -1, ROriginClient = -1, Parent = -1 };
            store.Refs[g] = null;
            _orders[store.Slots[g].Client].Add((uint)s.Clock, g);
            return;
        }

        // Item.ResolveReferences (Yjs' getMissing): the origins split where needed, the parent resolved.
        int left = -1, right = -1;
        int originClient = -1, rOriginClient = -1;
        uint originClock = 0, rOriginClock = 0;
        if (s.HasOrigin)
        {
            left = CleanEnd(ClientOf(s.OriginClient), (uint)s.OriginClock);
            if (!IsGc(left))
            {
                originClient = S(left).Client;
                originClock = S(left).Clock + (uint)S(left).Length - 1;
            }
        }

        if (s.HasROrigin)
        {
            right = CleanStart(ClientOf(s.ROriginClient), (uint)s.ROriginClock);
            rOriginClient = S(right).Client;
            rOriginClock = S(right).Clock;
        }

        int parent;
        var key = s.Key;
        if ((left >= 0 && IsGc(left)) || (right >= 0 && IsGc(right)))
        {
            parent = -1;
        }
        else if (s.ParentRoot != null)
        {
            parent = Root(s.ParentRoot);
        }
        else if (s.HasParentId)
        {
            var p = Find(ClientOf(s.ParentClient), (uint)s.ParentClock);
            parent = S(p).Kind == ContentKind.Type ? (int)S(p).Payload : -1;
        }
        else if (left >= 0)
        {
            parent = S(left).Parent;
            key = store.KeyOf(left);
        }
        else
        {
            parent = S(right).Parent;
            key = store.KeyOf(right);
        }

        var client = ClientOf(s.Client);
        var h = store.NewSlot();
        ref var n = ref S(h);
        n.Clock = (uint)s.Clock;
        n.Length = s.Length;
        n.Client = client;
        n.Left = left;
        n.Right = right;
        n.OriginClient = originClient;
        n.OriginClock = originClock;
        n.ROriginClient = rOriginClient;
        n.ROriginClock = rOriginClock;
        n.Parent = parent;
        if (parent < 0)
        {
            // Item.Integrate without a parent: a GC struct instead.
            n.Left = n.Right = n.OriginClient = n.ROriginClient = -1;
            n.Info = (int)ContentKind.Gc << 8;
            n.Payload = 0;
            store.Refs[h] = null;
            _orders[client].Add(n.Clock, h);
            return;
        }

        n.Info = (s.Countable ? Flags.Countable : 0) | ((int)s.Kind << 8);
        switch (s.Kind)
        {
            case ContentKind.String:
                n.Payload = store.Chars.Append(s.Chars);
                break;
            case ContentKind.Any:
                n.Payload = store.Values.Append(s.Values);
                break;
            case ContentKind.Type:
                n.Payload = store.Types.Count;
                store.Types.Add(new TypeEntry());
                break;
            default:
                n.Payload = 0;
                break;
        }

        store.Refs[h] = key != null && s.Cold != null ? new KeyedRef(key, s.Cold) : (object)key ?? s.Cold;
        IntegrateItem(h, key);
    }

    // Item.Integrate with a parent, from the conflict check on; no slot is allocated from here on.
    private void IntegrateItem(int h, string key)
    {
        var left = S(h).Left;
        var right = S(h).Right;
        var parent = S(h).Parent;
        var type = store.Types[parent];
        var mapParent = false;
        var previousCounted = false;

        if ((left < 0 && (right < 0 || S(right).Left >= 0)) || (left >= 0 && S(left).Right != right))
        {
            int o;
            if (left >= 0)
            {
                o = S(left).Right;
            }
            else if (key != null)
            {
                o = type.Map != null && type.Map.TryGetValue(key, out var v) ? v : -1;
                while (o >= 0 && S(o).Left >= 0)
                {
                    o = S(o).Left;
                }
            }
            else
            {
                o = type.Start;
            }

            left = ResolveConflict(h, left, o);
            S(h).Left = left;
        }

        if (left >= 0)
        {
            right = S(left).Right;
            S(left).Right = h;
        }
        else if (key != null)
        {
            right = type.Map != null && type.Map.TryGetValue(key, out var v) ? v : -1;
            while (right >= 0 && S(right).Left >= 0)
            {
                right = S(right).Left;
            }
        }
        else
        {
            right = type.Start;
            type.Start = h;
        }

        S(h).Right = right;
        if (right >= 0)
        {
            S(right).Left = h;
        }
        else if (key != null)
        {
            mapParent = true;
            type.Map ??= [];
            previousCounted = type.Map.TryGetValue(key, out var previous) && !IsDeleted(previous);
            type.Map[key] = h;
            if (left >= 0)
            {
                Delete(left);
            }
        }

        if (key == null && S(h).Countable && !S(h).Deleted)
        {
            type.Length += S(h).Length;
        }

        _orders[S(h).Client].Add(S(h).Clock, h);

        // Content.Integrate: deleted content marks the item deleted; a type gets its item.
        switch (S(h).Kind)
        {
            case ContentKind.Deleted:
                DeleteSet.Add((S(h).Client, S(h).Clock, S(h).Length));
                S(h).Info |= Flags.Deleted;
                break;
            case ContentKind.Type:
                store.Types[(int)S(h).Payload].Item = h;
                break;
        }

        if (mapParent)
        {
            type.MapCount += (S(h).Deleted ? 0 : 1) - (previousCounted ? 1 : 0);
        }

        AddChanged(parent, key);
        if ((type.Item >= 0 && IsDeleted(type.Item)) || (key != null && right >= 0))
        {
            Delete(h);
        }
    }

    // StructStore.ResolveConflict over handles (its comment explains the lazily filled sets and the shortcut).
    private int ResolveConflict(int item, int left, int o)
    {
        var first = o;
        var prev = -1;
        var leftMoved = false;
        HashSet<int> itemsBeforeOrigin = null;
        HashSet<int> conflictingItems = null;
        var itemRight = S(item).Right;
        try
        {
            while (o >= 0 && o != itemRight)
            {
                if (--IntegrationSteps < 0)
                {
                    throw new InvalidOperationException("integration steps exhausted");
                }

                itemsBeforeOrigin?.Add(o);
                conflictingItems?.Add(o);
                ref var os = ref S(o);
                ref var it = ref S(item);
                if (os.OriginClient == it.OriginClient && (it.OriginClient < 0 || os.OriginClock == it.OriginClock))
                {
                    // Case 1
                    if (store.ClientIds[os.Client] < store.ClientIds[it.Client])
                    {
                        left = o;
                        leftMoved = true;
                        conflictingItems?.Clear();
                    }
                    else if (os.ROriginClient == it.ROriginClient && (it.ROriginClient < 0 || os.ROriginClock == it.ROriginClock))
                    {
                        break;
                    }
                }
                else if (os.OriginClient >= 0)
                {
                    bool beforeOrigin, conflicting;
                    if (prev >= 0 && os.OriginClient == S(prev).Client && os.OriginClock >= S(prev).Clock && os.OriginClock < S(prev).Clock + (uint)S(prev).Length)
                    {
                        beforeOrigin = true;
                        conflicting = left != prev;
                    }
                    else
                    {
                        if (itemsBeforeOrigin == null)
                        {
                            itemsBeforeOrigin = _itemsBeforeOrigin ??= [];
                            conflictingItems = _conflictingItems ??= [];
                            Fill(itemsBeforeOrigin, first, o);
                            Fill(conflictingItems, leftMoved ? S(left).Right : first, o);
                        }

                        var origin = Find(os.OriginClient, os.OriginClock);
                        beforeOrigin = itemsBeforeOrigin.Contains(origin);
                        conflicting = conflictingItems.Contains(origin);
                    }

                    if (!beforeOrigin)
                    {
                        break;
                    }

                    // Case 2
                    if (!conflicting)
                    {
                        left = o;
                        leftMoved = true;
                        conflictingItems?.Clear();
                    }
                }
                else
                {
                    break;
                }

                prev = o;
                o = S(o).Right;
            }
        }
        finally
        {
            if (itemsBeforeOrigin != null)
            {
                Release(ref _itemsBeforeOrigin);
                Release(ref _conflictingItems);
            }
        }

        return left;

        void Fill(HashSet<int> set, int from, int to)
        {
            for (var h = from; h >= 0; h = S(h).Right)
            {
                set.Add(h);
                if (h == to)
                {
                    break;
                }
            }
        }

        static void Release(ref HashSet<int> set)
        {
            if (set.Count > KeepConflictSetCapacity)
            {
                set = null;
            }
            else
            {
                set.Clear();
            }
        }
    }

    // Transaction.AddChangedTypeToTransaction.
    private void AddChanged(int type, string key)
    {
        var item = store.Types[type].Item;
        if (item >= 0)
        {
            ref var s = ref S(item);
            if (s.Client >= _before.Length || s.Clock >= _before[s.Client] || s.Deleted)
            {
                return;
            }
        }

        if (!Changed.TryGetValue(type, out var keys))
        {
            Changed[type] = key == null ? null : [key];
        }
        else if (keys != null)
        {
            keys.Add(key);
        }
        else if (key != null)
        {
            Changed[type] = [null, key];
        }
    }

    /// <summary>Item.Delete: marks the item deleted, adjusts its parent's length or map count, records the delete.</summary>
    public void Delete(int h)
    {
        ref var s = ref S(h);
        if (s.Deleted)
        {
            return;
        }

        var key = store.KeyOf(h);
        var type = store.Types[s.Parent];
        if (s.Countable && key == null)
        {
            type.Length -= s.Length;
        }

        if (key != null && type.Map.TryGetValue(key, out var current) && current == h)
        {
            type.MapCount--;
        }

        s.Info |= Flags.Deleted;
        DeleteSet.Add((s.Client, s.Clock, s.Length));
        AddChanged(s.Parent, key);
        if (s.Kind == ContentKind.Type)
        {
            throw new NotSupportedException("deleting a nested type's content is not modelled");
        }
    }

    /// <summary>
    /// Yjs' readAndApplyDeleteSet (ycs' StructStore.ApplyDeleteSet) for ranges the store holds: the first struct split
    /// at the range's start, the last at its end, every live struct between deleted.
    /// </summary>
    public void ApplyDeleteSet(List<(long Client, (long Clock, long Length)[] Ranges)> ds)
    {
        foreach (var (clientId, ranges) in ds)
        {
            var order = _orders[store.ClientIndex[clientId]];
            foreach (var (clock64, length) in ranges)
            {
                var clock = (uint)clock64;
                var clockEnd = (uint)(clock64 + length);
                var c = order.Find(clock);
                if (!IsDeleted(order.Handle(c)) && S(order.Handle(c)).Clock < clock)
                {
                    c = Split(order, c, clock);
                }

                do
                {
                    var h = order.Handle(c);
                    if (S(h).Clock >= clockEnd)
                    {
                        break;
                    }

                    if (!IsDeleted(h))
                    {
                        if (clockEnd < S(h).Clock + (uint)S(h).Length)
                        {
                            // The right part starts at the range's end: the walk ends there, as ycs' does one step on.
                            Split(order, c, clockEnd);
                            Delete(h);
                            break;
                        }

                        Delete(h);
                    }
                }
                while (order.Next(ref c));
            }
        }
    }
}
