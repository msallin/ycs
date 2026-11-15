using System.Globalization;
using System.Text;
using static Ycs.Properties.Json;

namespace Ycs.Properties;

/// <summary>The content of a fuzzed item (lib0 v1 content refs).</summary>
internal abstract record FContent
{
    public abstract int Ref { get; }

    public abstract int Length { get; }

    public virtual bool IsType => false;

    public sealed record Deleted(int Len) : FContent
    {
        public override int Ref => 1;
        public override int Length => Len;
    }

    public sealed record Json(IReadOnlyList<string> Values) : FContent
    {
        public override int Ref => 2;
        public override int Length => Values.Count;
    }

    public sealed record Binary(byte[] Bytes) : FContent
    {
        public override int Ref => 3;
        public override int Length => 1;
    }

    public sealed record Str(string Text) : FContent
    {
        public override int Ref => 4;
        public override int Length => Text.Length;
    }

    public sealed record Embed(string JsonText) : FContent
    {
        public override int Ref => 5;
        public override int Length => 1;
    }

    public sealed record Format(string Key, string JsonValue) : FContent
    {
        public override int Ref => 6;
        public override int Length => 1;
    }

    /// <summary>A shared type: 0 YArray, 1 YMap, 2 YText, 3 YXmlElement, 4 YXmlFragment, 5 YXmlHook, 6 YXmlText.</summary>
    public sealed record Type(int TypeRef, string Name = null) : FContent
    {
        public override int Ref => 7;
        public override int Length => 1;
        public override bool IsType => true;
    }

    public sealed record Any(IReadOnlyList<object> Values) : FContent
    {
        public override int Ref => 8;
        public override int Length => Values.Count;
    }
}

/// <summary>One struct of a fuzzed update.</summary>
internal abstract record FStruct(long Client, long Clock)
{
    public abstract long Length { get; }

    public sealed record Gc(long Client, long Clock, long Len) : FStruct(Client, Clock)
    {
        public override long Length => Len;
    }

    public sealed record Skip(long Client, long Clock, long Len) : FStruct(Client, Clock)
    {
        public override long Length => Len;
    }

    /// <summary>An item; <see cref="ParentRoot"/> or <see cref="ParentId"/> only when it has neither origin.</summary>
    public sealed record Item(long Client, long Clock, (long, long)? Origin, (long, long)? Right, string ParentRoot, (long, long)? ParentId, string ParentSub, FContent Content)
        : FStruct(Client, Clock)
    {
        public override long Length => Content.Length;
    }
}

/// <summary>A fuzzed update: structs (in the order written, per client) and delete-set ranges (as written).</summary>
internal sealed record FUpdate(IReadOnlyList<(long Client, IReadOnlyList<FStruct> Structs)> Clients, IReadOnlyList<(long Client, IReadOnlyList<(long Clock, long Len)> Ranges)> DeleteSet)
{
    /// <summary>The lib0 v1 encoding (Yjs' UpdateEncoderV1), written here from the model, not by Ycs.</summary>
    public byte[] EncodeV1()
    {
        var w = new List<byte>();
        VarUint(w, (ulong)Clients.Count);
        foreach (var (client, structs) in Clients)
        {
            VarUint(w, (ulong)structs.Count);
            VarUint(w, (ulong)client);
            VarUint(w, (ulong)(structs.Count > 0 ? structs[0].Clock : 0));
            foreach (var s in structs)
            {
                switch (s)
                {
                    case FStruct.Gc gc:
                        w.Add(0);
                        VarUint(w, (ulong)gc.Len);
                        break;
                    case FStruct.Skip skip:
                        w.Add(10);
                        VarUint(w, (ulong)skip.Len);
                        break;
                    case FStruct.Item item:
                        WriteItem(w, item);
                        break;
                }
            }
        }

        VarUint(w, (ulong)DeleteSet.Count);
        foreach (var (client, ranges) in DeleteSet)
        {
            VarUint(w, (ulong)client);
            VarUint(w, (ulong)ranges.Count);
            foreach (var (clock, len) in ranges)
            {
                VarUint(w, (ulong)clock);
                VarUint(w, (ulong)len);
            }
        }

        return w.ToArray();
    }

    private static void WriteItem(List<byte> w, FStruct.Item item)
    {
        var info = (byte)((item.Content.Ref & 0x1F) | (item.Origin != null ? 0x80 : 0) | (item.Right != null ? 0x40 : 0) | (item.ParentSub != null ? 0x20 : 0));
        w.Add(info);
        if (item.Origin is var (oc, ok))
        {
            VarUint(w, (ulong)oc);
            VarUint(w, (ulong)ok);
        }

        if (item.Right is var (rc, rk))
        {
            VarUint(w, (ulong)rc);
            VarUint(w, (ulong)rk);
        }

        if (item.Origin == null && item.Right == null)
        {
            if (item.ParentRoot != null)
            {
                VarUint(w, 1);
                Str(w, item.ParentRoot);
            }
            else
            {
                VarUint(w, 0);
                VarUint(w, (ulong)item.ParentId.Value.Item1);
                VarUint(w, (ulong)item.ParentId.Value.Item2);
            }

            if (item.ParentSub != null)
            {
                Str(w, item.ParentSub);
            }
        }

        switch (item.Content)
        {
            case FContent.Deleted d:
                VarUint(w, (ulong)d.Len);
                break;
            case FContent.Json j:
                VarUint(w, (ulong)j.Values.Count);
                foreach (var v in j.Values)
                {
                    Str(w, v);
                }

                break;
            case FContent.Binary b:
                VarUint(w, (ulong)b.Bytes.Length);
                w.AddRange(b.Bytes);
                break;
            case FContent.Str s:
                Str(w, s.Text);
                break;
            case FContent.Embed e:
                Str(w, e.JsonText);
                break;
            case FContent.Format f:
                Str(w, f.Key);
                Str(w, f.JsonValue);
                break;
            case FContent.Type t:
                VarUint(w, (ulong)t.TypeRef);
                if (t.TypeRef is 3 or 5)
                {
                    Str(w, t.Name);
                }

                break;
            case FContent.Any a:
                VarUint(w, (ulong)a.Values.Count);
                foreach (var v in a.Values)
                {
                    Any(w, v);
                }

                break;
        }
    }

    private static void Any(List<byte> w, object v)
    {
        switch (v)
        {
            case null:
                w.Add(126);
                break;
            case bool b:
                w.Add((byte)(b ? 120 : 121));
                break;
            case string s:
                w.Add(119);
                Str(w, s);
                break;
            case int i:
                w.Add(125);
                VarInt(w, i);
                break;
            case double d:
                w.Add(123);
                var bits = BitConverter.DoubleToInt64Bits(d);
                for (var k = 7; k >= 0; k--)
                {
                    w.Add((byte)(bits >> (8 * k)));
                }

                break;
            case List<object> l:
                w.Add(117);
                VarUint(w, (ulong)l.Count);
                l.ForEach(x => Any(w, x));
                break;
            default:
                throw new ArgumentException(v.GetType().Name);
        }
    }

    public static void VarUint(List<byte> w, ulong n)
    {
        while (n > 0x7F)
        {
            w.Add((byte)(0x80 | (n & 0x7F)));
            n >>= 7;
        }

        w.Add((byte)n);
    }

    private static void VarInt(List<byte> w, long n)
    {
        var negative = n < 0;
        var abs = (ulong)Math.Abs(n);
        w.Add((byte)((abs > 0x3F ? 0x80 : 0) | (negative ? 0x40 : 0) | (byte)(abs & 0x3F)));
        abs >>= 6;
        while (abs > 0)
        {
            w.Add((byte)((abs > 0x7F ? 0x80 : 0) | (byte)(abs & 0x7F)));
            abs >>= 7;
        }
    }

    private static void Str(List<byte> w, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        VarUint(w, (ulong)bytes.Length);
        w.AddRange(bytes);
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        foreach (var (client, structs) in Clients)
        {
            sb.Append($"client {client}: ");
            sb.AppendJoin(", ", structs.Select(Describe));
            sb.Append("; ");
        }

        sb.Append("ds: ");
        sb.AppendJoin(", ", DeleteSet.Select(d => $"{d.Client}:[{string.Join(" ", d.Ranges.Select(r => $"{r.Clock}+{r.Len}"))}]"));
        return sb.ToString();
    }

    private static string Describe(FStruct s) => s switch
    {
        FStruct.Gc g => $"GC {g.Clock}+{g.Len}",
        FStruct.Skip k => $"Skip {k.Clock}+{k.Len}",
        FStruct.Item i => $"Item {i.Clock}+{i.Length} {i.Content.GetType().Name}" +
            (i.Origin is var (oc, ok) ? $" o={oc}:{ok}" : "") + (i.Right is var (rc, rk) ? $" r={rc}:{rk}" : "") +
            (i.ParentRoot != null ? $" p={i.ParentRoot}" : "") + (i.ParentId is var (pc, pk) ? $" p={pc}:{pk}" : "") +
            (i.ParentSub != null ? $" sub={i.ParentSub}" : ""),
        _ => "?",
    };
}

/// <summary>
/// Builds valid updates that Yjs itself would rarely produce: several synthetic clients written in
/// any order, runs split where Yjs would merge them, origins in the middle of other runs, items whose
/// parents arrive later, Skip and GC structs, delete sets that overlap, repeat and are unsorted,
/// clocks close to the documented limit, deep nesting, clients with no structs. It keeps a model of
/// what exists so that every reference points at something that is, or will be, there.
/// </summary>
internal sealed class UpdateFuzzer
{
    private const int Receivers = 2;
    private static readonly string[] Roots = ["t", "a", "m", "x"];

    private readonly Rng _rng;
    private readonly Dictionary<long, long> _clock = new();
    // Sequence elements by parent ("root:t" or "id:c:k"): every (client, clock) that is one element.
    private readonly Dictionary<string, List<(long, long)>> _elements = new();
    // Nested types: (id, type ref, parent key of its children).
    private readonly List<((long, long) Id, int TypeRef)> _types = new();
    private readonly List<(long Client, long Clock, long Len)> _all = new();
    // Items with a key (map values), by parent: their id and length.
    private readonly Dictionary<string, List<(long Client, long Clock, long Len)>> _keyed = new();

    private readonly bool _sortedDeleteSets;

    private UpdateFuzzer(ulong seed, bool sortedDeleteSets)
    {
        _rng = new Rng(seed);
        _sortedDeleteSets = sortedDeleteSets;
    }

    /// <summary>
    /// A script: two receivers (ids 1 and 2) get the same updates, in two different orders, as raw bytes.
    /// With <paramref name="sortedDeleteSets"/> the ranges of a client's delete set are written sorted and
    /// disjoint: unsorted, overlapping or repeated ones are valid v1, but make Yjs itself write corrupt v2
    /// delete sets (KnownFailures: yjs-unsorted-delete-set), so the comparison with Yjs leaves them out.
    /// </summary>
    public static (Dictionary<string, object> Script, List<FUpdate> Updates) Generate(ulong seed, bool sortedDeleteSets = false)
    {
        var f = new UpdateFuzzer(seed, sortedDeleteSets);
        var updates = new List<FUpdate>();
        var count = f._rng.Range(1, 6);
        for (var i = 0; i < count; i++)
        {
            updates.Add(f.NextUpdate());
        }

        return (ToScript(updates, seed), updates);
    }

    public static Dictionary<string, object> ToScript(IReadOnlyList<FUpdate> updates, ulong seed)
    {
        var rng = new Rng(seed ^ 0x5DEECE66DUL);
        var steps = new List<object>();
        var order = Enumerable.Range(0, updates.Count).ToList();
        foreach (var i in order)
        {
            steps.Add(Raw(updates[i], 0, rng));
        }

        rng.Shuffle(order);
        foreach (var i in order)
        {
            steps.Add(Raw(updates[i], 1, rng));
        }

        // A duplicate, applied once more.
        if (updates.Count > 0)
        {
            steps.Add(Raw(updates[rng.Int(updates.Count)], 1, rng));
        }

        return (Dictionary<string, object>)Parse(Canonical(Obj(
            ("clients", Enumerable.Range(1, Receivers).Select(id => (object)Obj(("id", (long)id), ("gc", id == 1))).ToList()),
            ("roots", new List<object> { Obj(("name", "t"), ("kind", "text")), Obj(("name", "a"), ("kind", "array")), Obj(("name", "m"), ("kind", "map")), Obj(("name", "x"), ("kind", "xml")) }),
            ("steps", steps))));
    }

    private static Dictionary<string, object> Raw(FUpdate u, long to, Rng rng) =>
        Obj(("op", "raw"), ("to", to), ("u", Convert.ToBase64String(u.EncodeV1())), ("via", rng.Int(4) == 0 ? "v1to2" : "v1"));

    private long NextClock(long client) => _clock.GetValueOrDefault(client);

    private FUpdate NextUpdate()
    {
        var clients = new List<(long, IReadOnlyList<FStruct>)>();
        var authors = Enumerable.Range(0, _rng.Range(1, 3)).Select(_ => (long)_rng.Pick(new[] { 10, 11, 12, 300, 70000, 2147483000 })).Distinct().ToList();
        foreach (var client in authors)
        {
            var structs = new List<FStruct>();
            var n = _rng.Range(1, 5);
            if (!_clock.ContainsKey(client) && _rng.Chance(0.15))
            {
                // A client whose history was collected, or one close to the clock limit (2^31 - 1).
                var len = _rng.Chance(0.3) ? (1L << 31) - 1 - 64 : _rng.Range(1, 5);
                structs.Add(new FStruct.Gc(client, 0, len));
                _all.Add((client, 0, len));
                _clock[client] = len;
            }

            for (var i = 0; i < n; i++)
            {
                var clock = NextClock(client);
                if (clock > (1L << 31) - 1 - 16)
                {
                    break; // stay within the documented clock limit
                }

                if (i > 0 && _rng.Chance(0.08))
                {
                    // A gap that is never filled: what follows waits, in Yjs as in Ycs.
                    var gap = _rng.Range(1, 3);
                    structs.Add(new FStruct.Skip(client, clock, gap));
                    _clock[client] = clock + gap;
                    continue;
                }

                var item = NewItem(client, clock);
                structs.Add(item);
                _clock[client] = clock + item.Length;
                _all.Add((client, clock, item.Length));
            }

            clients.Add((client, structs));
        }

        if (_rng.Chance(0.1))
        {
            // A client listed with no structs (yrs writes these).
            clients.Add((13, []));
        }

        _rng.Shuffle(clients);
        return new FUpdate(clients, DeleteSet());
    }

    private FStruct.Item NewItem(long client, long clock)
    {
        // Where: a root or a nested type; with origins (any element of that parent, also inside a run) or without.
        var nested = _types.Count > 0 && _rng.Chance(0.4);
        var (parentRoot, parentId, kind) = nested
            ? PickType()
            : _rng.Pick(Roots) switch
            {
                "t" => ("t", ((long, long)?)null, 2),
                "a" => ("a", null, 0),
                "m" => ("m", null, 1),
                _ => ("x", null, 4),
            };
        var key = parentRoot != null ? "root:" + parentRoot : $"id:{parentId.Value.Item1}:{parentId.Value.Item2}";
        var content = ContentFor(kind);
        var isMap = kind is 1 or 5;
        var elements = _elements.TryGetValue(key, out var e) ? e : _elements[key] = new List<(long, long)>();
        (long, long)? origin = null;
        (long, long)? right = null;
        string sub = null;
        if (isMap && _keyed.TryGetValue(key, out var values) && _rng.Chance(0.15))
        {
            // An origin (or right origin) inside a map value: the item takes its key, and the value is split there.
            var (vc, vk, vl) = _rng.Pick(values);
            var at = (vc, vk + _rng.Int((int)vl));
            if (_rng.Chance(0.7))
            {
                origin = at;
            }
            else
            {
                right = at;
            }
        }
        else if (isMap || (kind is 3 && _rng.Chance(0.4)))
        {
            sub = _rng.Pick(new[] { "k", "l", "m" });
        }
        else if (elements.Count > 0 && _rng.Chance(0.8))
        {
            if (_rng.Chance(0.8))
            {
                origin = _rng.Pick(elements);
            }

            if (_rng.Chance(0.4))
            {
                right = _rng.Pick(elements);
            }
        }

        var item = new FStruct.Item(client, clock, origin, right, origin == null && right == null ? parentRoot : null, origin == null && right == null ? parentId : null, sub, content);
        if (sub != null || (isMap && (origin != null || right != null)))
        {
            (_keyed.TryGetValue(key, out var list) ? list : _keyed[key] = new List<(long, long, long)>()).Add((client, clock, content.Length));
        }
        else
        {
            for (var k = 0; k < content.Length; k++)
            {
                elements.Add((client, clock + k));
            }
        }

        if (content is FContent.Type t)
        {
            _types.Add(((client, clock), t.TypeRef));
        }

        return item;
    }

    private (string, (long, long)?, int) PickType()
    {
        var (id, typeRef) = _rng.Pick(_types);
        return (null, id, typeRef);
    }

    private FContent ContentFor(int kind)
    {
        var deep = _types.Count < 230 && _rng.Chance(0.02);
        switch (kind)
        {
            case 2: // text
            case 6: // xml text
                return _rng.Int(10) switch
                {
                    < 6 => new FContent.Str(_rng.Pick(new[] { "a", "bc", "def", "xé", "中" })),
                    6 => new FContent.Format(_rng.Pick(new[] { "bold", "italic" }), _rng.Pick(new[] { "true", "null", "1" })),
                    7 => new FContent.Embed("{\"image\":\"i.png\"}"),
                    8 => new FContent.Deleted(_rng.Range(1, 3)),
                    _ => new FContent.Type(_rng.Pick(new[] { 0, 1, 2 })),
                };
            case 4: // xml fragment / element children
            case 3:
                return _rng.Chance(0.5) ? new FContent.Type(3, _rng.Pick(new[] { "p", "div" })) : new FContent.Type(6);
            default: // array or map
                if (deep)
                {
                    return new FContent.Type(0);
                }

                return _rng.Int(10) switch
                {
                    < 5 => new FContent.Any(Enumerable.Range(0, _rng.Range(1, 3)).Select(_ => _rng.Pick(new object[] { 1, -7, "s", true, null, 1.5, new List<object> { 1, "x" } })).ToList()),
                    5 => new FContent.Binary([1, 2, 3]),
                    6 => new FContent.Json(["1", "\"j\""]),
                    7 => new FContent.Deleted(_rng.Range(1, 2)),
                    _ => new FContent.Type(_rng.Pick(new[] { 0, 1, 2, 4 })),
                };
        }
    }

    private IReadOnlyList<(long, IReadOnlyList<(long, long)>)> DeleteSet()
    {
        if (_all.Count == 0 || _rng.Chance(0.4))
        {
            return [];
        }

        var byClient = new Dictionary<long, List<(long, long)>>();
        for (var i = _rng.Range(1, 4); i > 0; i--)
        {
            var (client, clock, len) = _rng.Pick(_all);
            var start = clock + _rng.Int((int)Math.Min(len, 8));
            // Sometimes past what exists yet: the deletion waits.
            var length = _rng.Range(1, 3) + (_rng.Chance(0.1) ? 5 : 0);
            var ranges = byClient.TryGetValue(client, out var r) ? r : byClient[client] = new List<(long, long)>();
            ranges.Add((start, length));
            if (_rng.Chance(0.2))
            {
                ranges.Add((start, length)); // duplicated
            }
        }

        var longValues = _keyed.Where(kv => kv.Key.StartsWith("id:", StringComparison.Ordinal)).SelectMany(kv => kv.Value.Select(v => (Parent: kv.Key, Value: v))).Where(x => x.Value.Len > 1).ToList();
        if (longValues.Count > 0 && _rng.Chance(0.2))
        {
            // The tail of a map value of a nested type, and that type: the value's first part would stay a second live value
            // of its key, which the garbage collection of the deleted type throws on (KnownFailures: yjs-gc-throws-after-integrating).
            var (parent, (client, clock, len)) = _rng.Pick(longValues);
            (byClient.TryGetValue(client, out var r) ? r : byClient[client] = new List<(long, long)>()).Add((clock + 1, len - 1));
            var parts = parent.Split(':');
            var (typeClient, typeClock) = (long.Parse(parts[1], CultureInfo.InvariantCulture), long.Parse(parts[2], CultureInfo.InvariantCulture));
            (byClient.TryGetValue(typeClient, out var t) ? t : byClient[typeClient] = new List<(long, long)>()).Add((typeClock, 1));
        }

        var result = byClient.Select(kv => (kv.Key, (IReadOnlyList<(long, long)>)(_sortedDeleteSets ? Disjoint(kv.Value) : kv.Value.OrderBy(_ => _rng.Next()).ToList()))).ToList();
        if (_rng.Chance(0.1))
        {
            result.Add((14, []));
        }

        return result;
    }

    /// <summary>The ranges sorted, with overlapping and repeated ones merged.</summary>
    private static List<(long, long)> Disjoint(List<(long, long)> ranges)
    {
        var merged = new List<(long Clock, long Len)>();
        foreach (var (clock, len) in ranges.OrderBy(r => r.Item1))
        {
            if (merged.Count > 0 && merged[^1].Clock + merged[^1].Len >= clock)
            {
                merged[^1] = (merged[^1].Clock, Math.Max(merged[^1].Len, clock + len - merged[^1].Clock));
            }
            else
            {
                merged.Add((clock, len));
            }
        }

        return merged.Select(r => (r.Clock, r.Len)).ToList();
    }

    /// <summary>A chain of nested arrays <paramref name="depth"/> deep, in one update (nesting is limited, UpdateLimits.MaxDepth).</summary>
    public static FUpdate DeepNesting(int depth)
    {
        var structs = new List<FStruct>();
        for (var i = 0; i < depth; i++)
        {
            structs.Add(new FStruct.Item(20, i, null, null, i == 0 ? "a" : null, i == 0 ? null : (20, i - 1), null, new FContent.Type(0)));
        }

        structs.Add(new FStruct.Item(20, depth, null, null, null, (20, depth - 1), null, new FContent.Any([1])));
        return new FUpdate([(20, structs)], []);
    }
}
