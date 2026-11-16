using System.Text;

namespace Ycs.Layout;

/// <summary>Builds a <see cref="LayoutStore"/> from a ycs document's struct store, and reads content back from it.</summary>
internal static class Builder
{
    public static LayoutStore Build(YDoc doc) => Build(doc, Paging.Trimmed);

    public static LayoutStore Build(YDoc doc, Paging paging)
    {
        var store = new LayoutStore(paging);
        var clients = doc.Store.Clients;
        var handles = new Dictionary<AbstractStruct, int>(ReferenceEqualityComparer.Instance);
        var types = new Dictionary<YType, int>(ReferenceEqualityComparer.Instance);

        store.ClientIds = [.. clients.Keys];
        store.Orders = new ClientOrder[store.ClientIds.Length];
        for (var c = 0; c < store.ClientIds.Length; c++)
        {
            store.ClientIndex[store.ClientIds[c]] = c;
        }

        // Handles in clock order per client: what a load produces.
        for (var c = 0; c < store.ClientIds.Length; c++)
        {
            var list = clients[store.ClientIds[c]];
            var order = store.Orders[c] = new ClientOrder();
            foreach (var s in list)
            {
                var h = store.NewSlot();
                handles[s.ToStruct()] = h;
                order.Add(Clock(s.Id.Clock), h);
            }
        }

        int TypeIndex(YType t)
        {
            if (t == null)
            {
                return -1;
            }

            if (!types.TryGetValue(t, out var i))
            {
                i = store.Types.Count;
                types[t] = i;
                store.Types.Add(new TypeEntry());
            }

            return i;
        }

        int Handle(AbstractStruct s) => s == null ? -1 : handles[s];

        foreach (var (s, h) in handles)
        {
            ref var slot = ref store.Slots[h];
            slot.Clock = Clock(s.Id.Clock);
            slot.Length = s.Length;
            slot.Client = store.ClientIndex[s.Id.Client];
            slot.Left = slot.Right = slot.Parent = slot.OriginClient = slot.ROriginClient = -1;
            if (s is not Item item)
            {
                slot.Info = (int)ContentKind.Gc << 8;
                continue;
            }

            slot.Left = Handle(item.Left);
            slot.Right = Handle(item.Right);
            if (item.LeftOrigin is ItemId lo)
            {
                slot.OriginClient = store.ClientIndex[lo.Client];
                slot.OriginClock = Clock(lo.Clock);
            }

            if (item.RightOrigin is ItemId ro)
            {
                slot.ROriginClient = store.ClientIndex[ro.Client];
                slot.ROriginClock = Clock(ro.Clock);
            }

            slot.Parent = TypeIndex(item.Parent as YType);
            var info = (item.Keep ? Flags.Keep : 0) | (item.Countable ? Flags.Countable : 0) | (item.Deleted ? Flags.Deleted : 0) | (item.Marker ? Flags.Marker : 0);
            if (item.Redone is ItemId redone)
            {
                info |= Flags.HasRedone;
                store.Redone[h] = redone;
            }

            ContentKind kind;
            object cold = null;
            switch (item.Content)
            {
                case ContentDeleted:
                    kind = ContentKind.Deleted;
                    break;
                case ContentString str:
                    kind = ContentKind.String;
                    var chars = new char[str.Length];
                    str.CopyTo(chars);
                    slot.Payload = store.Chars.Append(chars);
                    break;
                case ContentAny any:
                    kind = ContentKind.Any;
                    var values = new object[any.Length];
                    for (var i = 0; i < values.Length; i++)
                    {
                        values[i] = any.ValueAt(i);
                    }

                    slot.Payload = store.Values.Append(values);
                    break;
                case ContentType type:
                    kind = ContentKind.Type;
                    var ti = TypeIndex(type.Type);
                    slot.Payload = ti;
                    store.Types[ti].Item = h;
                    break;
                default:
                    kind = ContentKind.Cold;
                    cold = item.Content;
                    break;
            }

            slot.Info = info | ((int)kind << 8);
            var key = item.ParentSub;
            store.Refs[h] = key != null && cold != null ? new KeyedRef(key, cold) : (object)key ?? cold;
        }

        foreach (var (t, i) in types)
        {
            var e = store.Types[i];
            e.Start = Handle(t._start.ToStruct());
            e.Length = t.Length;
            e.MapCount = t._mapCount;
            if (t._map.Count > 0)
            {
                e.Map = new Dictionary<string, int>(t._map.Count);
                foreach (var (k, v) in t._map)
                {
                    e.Map[k] = Handle(v.ToStruct());
                }
            }

            if (t._item.IsNull)
            {
                e.RootName = t.FindRootTypeKey();
            }
        }

        store.Trim();
        return store;
    }

    private static uint Clock(long clock) =>
        clock < LayoutStore.WideClock ? (uint)clock : throw new NotSupportedException("a clock above 2^32 - 2 (the design's side table is not modelled)");

    public static TypeEntry Root(LayoutStore store, string name) => store.Types.First(t => t.RootName == name);

    public static string Text(LayoutStore store, string name)
    {
        var sb = new StringBuilder();
        for (var h = Root(store, name).Start; h >= 0; h = store.Slots[h].Right)
        {
            ref var s = ref store.Slots[h];
            if (!s.Deleted && s.Kind == ContentKind.String)
            {
                sb.Append(store.Chars.Slice(s.Payload, s.Length));
            }
        }

        return sb.ToString();
    }

    public static List<object> Array(LayoutStore store, string name)
    {
        var list = new List<object>();
        for (var h = Root(store, name).Start; h >= 0; h = store.Slots[h].Right)
        {
            ref var s = ref store.Slots[h];
            if (!s.Deleted && s.Kind == ContentKind.Any)
            {
                list.AddRange(store.Values.Slice(s.Payload, s.Length).ToArray());
            }
        }

        return list;
    }

    public static Dictionary<string, object> Map(LayoutStore store, string name)
    {
        var map = new Dictionary<string, object>();
        foreach (var (k, h) in Root(store, name).Map ?? [])
        {
            ref var s = ref store.Slots[h];
            if (!s.Deleted && s.Kind == ContentKind.Any)
            {
                map[k] = store.Values.Slice(s.Payload, s.Length)[s.Length - 1];
            }
        }

        return map;
    }

    /// <summary>What is wrong with the links, or null: Left and Right mirror each other, and each client's order is contiguous by clock.</summary>
    public static string Check(LayoutStore store)
    {
        for (var h = 0; h < store.Count; h++)
        {
            ref var s = ref store.Slots[h];
            if (s.Right >= 0 && store.Slots[s.Right].Left != h)
            {
                return $"handle {h}: its right does not point back";
            }
        }

        foreach (var o in store.Orders)
        {
            for (var i = 1; i < o.Count; i++)
            {
                ref var p = ref store.Slots[o.Handles[i - 1]];
                if (p.Clock + (uint)p.Length != o.Starts[i] || store.Slots[o.Handles[i]].Clock != o.Starts[i])
                {
                    return $"order position {i}: not contiguous";
                }
            }
        }

        return null;
    }
}
