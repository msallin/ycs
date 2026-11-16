using static Ycs.Properties.Json;

namespace Ycs.Properties;

/// <summary>A small deterministic PRNG (SplitMix64), the same sequence on every platform and runtime.</summary>
internal sealed class Rng(ulong seed)
{
    private ulong _state = seed;

    public ulong Next()
    {
        var z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public int Int(int n) => n <= 0 ? 0 : (int)(Next() % (ulong)n);

    public int Range(int min, int max) => min + Int(max - min + 1);

    public bool Chance(double p) => (Next() >> 11) * (1.0 / (1UL << 53)) < p;

    public T Pick<T>(IReadOnlyList<T> items) => items[Int(items.Count)];

    public void Shuffle<T>(IList<T> items)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = Int(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}

/// <summary>The kinds of operation of the language, as the generator and the coverage table name them.</summary>
internal static class OpKinds
{
    public const string TextInsert = "text.insert";
    public const string TextDelete = "text.delete";
    public const string TextFormat = "text.format";
    public const string TextEmbed = "text.embed";
    public const string TextDelta = "text.delta";
    public const string TextAttr = "text.attr";
    public const string ArrayInsert = "array.insert";
    public const string ArrayDelete = "array.delete";
    public const string MapSet = "map.set";
    public const string MapDelete = "map.delete";
    public const string XmlInsert = "xml.insert";
    public const string XmlDelete = "xml.delete";
    public const string XmlAttr = "xml.attr";
    public const string XmlText = "xml.text";
    public const string Nested = "nested.create";
    public const string Transact = "transact";
    public const string Undo = "undo";
    public const string Snapshot = "snapshot";
    public const string RelPos = "relpos";
    public const string Observe = "observe";

    /// <summary>An observer that makes operations when its type first changes (<see cref="GenOptions.Windows"/>).</summary>
    public const string React = "react";

    /// <summary>A transaction typing (or pushing) on at one place, with an update applied or a snapshot read in between (<see cref="GenOptions.Windows"/>).</summary>
    public const string Typing = "typing";

    /// <summary>The kinds the pairwise fixtures interleave (every unordered pair, and each with itself).</summary>
    public static readonly string[] Paired =
    [
        TextInsert, TextDelete, TextFormat, TextEmbed, TextDelta, TextAttr, ArrayInsert, ArrayDelete, MapSet, MapDelete,
        XmlInsert, XmlDelete, XmlAttr, XmlText, Nested, Transact, Undo, Snapshot, RelPos,
    ];
}

/// <summary>What a generated script may contain.</summary>
internal sealed record GenOptions
{
    public int MinClients { get; init; } = 2;
    public int MaxClients { get; init; } = 5;
    public int MinSteps { get; init; } = 4;
    public int MaxSteps { get; init; } = 40;
    public bool Compact { get; init; }
    /// <summary>Lone surrogates in strings (Yjs keeps them locally and writes U+FFFD).</summary>
    public bool LoneSurrogates { get; init; } = true;
    /// <summary>Characters outside the BMP (surrogate pairs), which an insert can split: Yjs then replaces both halves with U+FFFD in place.</summary>
    public bool Astral { get; init; } = true;
    /// <summary>End with a full sync of every client (most scripts), so that convergence is observed.</summary>
    public double FinalSync { get; init; } = 0.7;
    /// <summary>Only local changes on one client and no network (for the undo-all-restores property).</summary>
    public bool SingleClientLocal { get; init; }
    /// <summary>Restrict to these kinds (null: the full mix).</summary>
    public IReadOnlyList<string> Kinds { get; init; }
    public double NetworkShare { get; init; } = 0.4;
    /// <summary>Subdocuments as values (in one seed of ten: the pinned subdocs-event-removed-instead-of-loaded shows in nearly every such script).</summary>
    public bool Subdocs { get; init; }

    /// <summary>
    /// The windows in which a run Ycs extended in place could differ from Yjs' items: quiet clients (no observers, no
    /// transaction handlers, at least one per script: there runs are extended), garbage collection filters, typing
    /// runs with an update applied or a snapshot taken and read in the middle of the transaction, and observers that
    /// edit (in a transaction of their own, or during another transaction's cleanup). Search markers can then be stale,
    /// as in Yjs, so the markers-invisible property does not use it.
    /// </summary>
    public bool Windows { get; init; }

    /// <summary>
    /// With <see cref="Windows"/>: reacting handlers are five times as frequent, and three of four sit on the document's
    /// <c>afterTransactionCleanup</c>, <c>update</c> or <c>afterTransaction</c> instead of a type, so that nested
    /// transactions split and merge structs while the cleanup that called them still has merging to do.
    /// </summary>
    public bool Hooks { get; init; }
}

/// <summary>
/// Generates scripts of the operation language (docs/testing.md), seeded and deterministic. It
/// executes each step on Ycs as it goes (ScriptRun), only to choose targets that exist; a script
/// stays valid when steps are removed, because every operation is total (indexes are clamped and an
/// operation whose target does not resolve is a recorded no-op on both sides).
/// </summary>
internal sealed class Generator
{
    private static readonly string[] Words = ["a", "b", "c", "x", "yz", " ", "\n", "é", "😀", "👍🏽", "中文", "𝄞", "ab"];
    private static readonly string[] BmpWords = Words.Where(w => !w.Any(char.IsSurrogate)).ToArray();
    private static readonly string[] Keys = ["a", "b", "c", "k1", "k2"];
    private static readonly string[] AttrKeys = ["bold", "italic", "color", "size", "font"];
    private static readonly string[] Origins = ["o1", "o2"];

    // With GenOptions.Windows: the full mix, typing runs eight times as often as another kind, and reacting observers.
    private static readonly string[] WindowKinds =
        [.. OpKinds.Paired, OpKinds.Observe, OpKinds.React, .. Enumerable.Repeat(OpKinds.Typing, 8)];

    private static readonly string[] HookKinds = [.. WindowKinds, .. Enumerable.Repeat(OpKinds.React, 5)];
    private static readonly string[] HookTriggers = ["cleanup", "update", "after"];

    private readonly Rng _rng;
    private readonly GenOptions _options;
    private readonly List<object> _steps = new();
    private ScriptRun _run;
    private Dictionary<string, object> _script;
    private readonly List<HashSet<(int Client, int K)>> _has = new();
    private int _ids;

    private Generator(ulong seed, GenOptions options)
    {
        _rng = new Rng(seed);
        _options = options;
    }

    public static Dictionary<string, object> Generate(ulong seed, GenOptions options = null)
    {
        var g = new Generator(seed, options ?? new GenOptions());
        g.Header();
        var n = g._rng.Range(g._options.MinSteps, g._options.MaxSteps);
        for (var i = 0; i < n; i++)
        {
            g.RandomStep();
        }

        if (!g._options.SingleClientLocal && g._rng.Chance(g._options.FinalSync))
        {
            g.FullSync();
        }

        return g._script;
    }

    /// <summary>
    /// Two (or three) clients that share a starting structure, then make operations of kind
    /// <paramref name="a"/> and <paramref name="b"/> concurrently, then exchange everything in a
    /// random order, with duplicates and mixed formats.
    /// </summary>
    public static Dictionary<string, object> GeneratePair(ulong seed, string a, string b, bool compact)
    {
        var g = new Generator(seed, new GenOptions { MinClients = 2, MaxClients = 3, Compact = compact });
        g.Header(all: true);
        g.Setup();
        g.FullSync();
        var rounds = g._rng.Range(1, 2);
        for (var r = 0; r < rounds; r++)
        {
            g.LocalStep(0, a);
            g.LocalStep(1, b);
            if (g._run.Clients.Count > 2 && g._rng.Chance(0.5))
            {
                g.LocalStep(2, g._rng.Chance(0.5) ? a : b);
            }
        }

        g.FullSync();
        // A check after convergence: undo or read what was created, on every client.
        for (var c = 0; c < g._run.Clients.Count; c++)
        {
            if (g._run.UndoManagers(c).Count > 0 || g._run.Snapshots(c).Count > 0 || g._rng.Chance(0.3))
            {
                g.LocalStep(c, g._run.UndoManagers(c).Count > 0 ? OpKinds.Undo : g._run.Snapshots(c).Count > 0 ? OpKinds.Snapshot : OpKinds.RelPos);
            }
        }

        // And every snapshot taken, as a document.
        for (var c = 0; c < g._run.Clients.Count; c++)
        {
            foreach (var id in g._run.Snapshots(c).ToList())
            {
                g.Emit(Obj(("op", "docFromSnap"), ("c", (long)c), ("id", id)));
            }
        }

        g.FullSync();
        return g._script;
    }

    private void Header(bool all = false)
    {
        var n = _options.SingleClientLocal ? 1 : _rng.Range(_options.MinClients, _options.MaxClients);
        var ids = new HashSet<long>();
        var clients = new List<object>();
        while (clients.Count < n)
        {
            long id = _rng.Chance(0.5) ? _rng.Range(1, 20) : _rng.Chance(0.5) ? _rng.Range(1000, 1_000_000) : _rng.Chance(0.5) ? int.MaxValue - _rng.Int(1000) : uint.MaxValue - (long)_rng.Int(1000);
            if (ids.Add(id))
            {
                var client = Obj(("id", new JsonNumber(id.ToString(System.Globalization.CultureInfo.InvariantCulture))), ("gc", _rng.Chance(0.7)));
                if (_options.Windows)
                {
                    if (_rng.Chance(0.6))
                    {
                        client["quiet"] = true;
                    }

                    if (client["gc"] is true && _rng.Chance(0.25))
                    {
                        client["gcf"] = (long)_rng.Range(2, 4);
                    }
                }

                clients.Add(client);
            }
        }

        if (_options.Windows && !clients.Cast<Dictionary<string, object>>().Any(cl => cl.ContainsKey("quiet")))
        {
            ((Dictionary<string, object>)clients[0])["quiet"] = true;
        }

        var roots = new List<object>();
        var available = new List<(string, string)> { ("t", "text"), ("a", "array"), ("m", "map"), ("x", "xml") };
        foreach (var (name, kind) in available)
        {
            if (all || _rng.Chance(0.6))
            {
                roots.Add(Obj(("name", name), ("kind", kind)));
            }
        }

        if (roots.Count == 0)
        {
            roots.Add(Obj(("name", "t"), ("kind", "text")));
        }

        if (_rng.Chance(0.15))
        {
            roots.Add(Obj(("name", "t2"), ("kind", "text")));
        }

        _script = Obj(("clients", clients), ("roots", roots), ("steps", _steps));
        if (_options.Compact)
        {
            _script["compact"] = true;
        }

        _run = new ScriptRun(_script);
        for (var i = 0; i < n; i++)
        {
            _has.Add(new HashSet<(int, int)>());
        }
    }

    private void Emit(Dictionary<string, object> step)
    {
        // Normalize numbers to JsonNumber so the script reads back as it was written.
        var normalized = (Dictionary<string, object>)Parse(Canonical(step));
        _steps.Add(normalized);
        _run.Step(normalized);
        // Every update a client made is one it has.
        for (var c = 0; c < _run.Clients.Count; c++)
        {
            for (var k = 0; k < _run.LogCount(c); k++)
            {
                _has[c].Add((c, k));
            }
        }
    }

    private void RandomStep()
    {
        var clients = _run.Clients.Count;
        if (clients > 1 && _rng.Chance(_options.NetworkShare))
        {
            NetworkStep();
            return;
        }

        var kinds = _options.Kinds ?? (_options.Hooks ? HookKinds : _options.Windows ? WindowKinds : OpKinds.Paired.Append(OpKinds.Observe).ToArray());
        LocalStep(_rng.Int(clients), _rng.Pick(kinds));
    }

    // ---------------------------------------------------------------- network

    private List<(int Client, int K)> Missing(int to)
    {
        var missing = new List<(int, int)>();
        for (var c = 0; c < _run.Clients.Count; c++)
        {
            for (var k = 0; k < _run.LogCount(c); k++)
            {
                if (!_has[to].Contains((c, k)))
                {
                    missing.Add((c, k));
                }
            }
        }

        return missing;
    }

    private string Via() => _rng.Int(10) switch
    {
        < 5 => "v1",
        < 8 => "v2",
        8 => "v1to2",
        _ => "v2to1",
    };

    private void NetworkStep()
    {
        var to = _rng.Int(_run.Clients.Count);
        var roll = _rng.Int(10);
        var missing = Missing(to);
        if (roll < 6)
        {
            // One update: usually one it lacks (in any order), sometimes a duplicate or its own.
            (int, int) u;
            if (missing.Count > 0 && _rng.Chance(0.85))
            {
                u = _rng.Pick(missing);
            }
            else
            {
                var from = _rng.Int(_run.Clients.Count);
                if (_run.LogCount(from) == 0)
                {
                    return;
                }

                u = (from, _rng.Int(_run.LogCount(from)));
            }

            Deliver(u.Item1, u.Item2, to);
        }
        else if (roll < 8)
        {
            var from = _rng.Int(_run.Clients.Count);
            if (from != to)
            {
                Sync(from, to);
            }
        }
        else
        {
            var all = new List<(int, int)>();
            for (var c = 0; c < _run.Clients.Count; c++)
            {
                for (var k = 0; k < _run.LogCount(c); k++)
                {
                    all.Add((c, k));
                }
            }

            if (all.Count == 0)
            {
                return;
            }

            _rng.Shuffle(all);
            var pick = all.Take(_rng.Range(1, Math.Min(all.Count, 6))).ToList();
            Emit(Obj(
                ("op", "merged"),
                ("from", pick.Select(p => (object)new List<object> { (long)p.Item1, (long)p.Item2 }).ToList()),
                ("to", (long)to),
                ("via", _rng.Chance(0.5) ? "v1" : "v2"),
                ("diff", _rng.Chance(0.4))));
            MarkAllKnownTo(to, pick);
        }
    }

    private void Deliver(int from, int k, int to)
    {
        Emit(Obj(("op", "deliver"), ("from", (long)from), ("k", (long)k), ("to", (long)to), ("via", Via())));
        _has[to].Add((from, k));
    }

    private void MarkAllKnownTo(int to, IEnumerable<(int, int)> updates)
    {
        foreach (var u in updates)
        {
            _has[to].Add(u);
        }
    }

    private void Sync(int from, int to)
    {
        Emit(Obj(("op", "sync"), ("from", (long)from), ("to", (long)to), ("via", _rng.Chance(0.5) ? "v1" : "v2")));
        MarkAllKnownTo(to, _has[from].ToList());
    }

    /// <summary>Every client gets every update, in a random order or by sync, some twice.</summary>
    private void FullSync()
    {
        var clients = _run.Clients.Count;
        if (clients < 2)
        {
            return;
        }

        if (_rng.Chance(0.3))
        {
            // Through client 0, by state vector sync.
            for (var c = 1; c < clients; c++)
            {
                Sync(c, 0);
            }

            for (var c = 1; c < clients; c++)
            {
                Sync(0, c);
            }

            return;
        }

        for (var round = 0; round < 3; round++)
        {
            for (var to = 0; to < clients; to++)
            {
                var missing = Missing(to);
                _rng.Shuffle(missing);
                foreach (var (from, k) in missing)
                {
                    Deliver(from, k, to);
                    if (_rng.Chance(0.05))
                    {
                        Deliver(from, k, to);
                    }
                }
            }

            // Cleanups after remote formatting make new updates; deliver those too.
            if (Enumerable.Range(0, clients).All(c => Missing(c).Count == 0))
            {
                return;
            }
        }
    }

    // ---------------------------------------------------------------- local

    private void Setup()
    {
        foreach (var t in _run.Targets(0).ToList())
        {
            switch (t.Kind)
            {
                case "text":
                    Emit(Obj(("op", "insert"), ("c", 0L), ("path", t.Path), ("at", 0L), ("text", "hello world")));
                    break;
                case "array":
                    Emit(Obj(("op", "ains"), ("c", 0L), ("path", t.Path), ("at", 0L), ("values", new List<object> { 1L, "two", Obj(("$t", "text"), ("s", "in")), Obj(("$t", "map"), ("v", new List<object> { new List<object> { "k", 1L } })), Obj(("$t", "array")) })));
                    break;
                case "map":
                    Emit(Obj(("op", "set"), ("c", 0L), ("path", t.Path), ("key", "a"), ("value", 1L)));
                    Emit(Obj(("op", "set"), ("c", 0L), ("path", t.Path), ("key", "n"), ("value", Obj(("$t", "map")))));
                    break;
                case "xmlf":
                    Emit(Obj(("op", "ains"), ("c", 0L), ("path", t.Path), ("at", 0L), ("values", new List<object> { XmlElement(1), Obj(("$t", "xmlt"), ("s", "tail")) })));
                    break;
            }
        }
    }

    private List<Target> TargetsOf(int c, params string[] kinds) => _run.Targets(c).Where(t => kinds.Contains(t.Kind)).ToList();

    private void LocalStep(int c, string kind)
    {
        var op = LocalOp(c, kind, allowCompound: true);
        if (op != null)
        {
            op["c"] = (long)c;
            Emit(op);
        }
    }

    /// <summary>One local operation of a kind (null if client <paramref name="c"/> has nothing it applies to).</summary>
    private Dictionary<string, object> LocalOp(int c, string kind, bool allowCompound)
    {
        switch (kind)
        {
            case OpKinds.TextInsert:
            case OpKinds.TextDelete:
            case OpKinds.TextFormat:
            case OpKinds.TextEmbed:
            case OpKinds.TextDelta:
            case OpKinds.TextAttr:
                {
                    var texts = TargetsOf(c, "text", "xmlt");
                    return texts.Count == 0 ? null : TextOp(_rng.Pick(texts), kind);
                }
            case OpKinds.XmlText:
                {
                    var texts = TargetsOf(c, "xmlt");
                    return texts.Count == 0 ? null : TextOp(_rng.Pick(texts), _rng.Pick(new[] { OpKinds.TextInsert, OpKinds.TextDelete, OpKinds.TextFormat, OpKinds.TextDelta }));
                }
            case OpKinds.ArrayInsert:
                {
                    var arrays = TargetsOf(c, "array");
                    if (arrays.Count == 0)
                    {
                        return null;
                    }

                    var t = _rng.Pick(arrays);
                    var values = Enumerable.Range(0, _rng.Range(1, 3)).Select(_ => ArrayValue()).ToList();
                    return _rng.Int(4) switch
                    {
                        0 => Obj(("op", "push"), ("path", t.Path), ("values", values)),
                        1 => Obj(("op", "unshift"), ("path", t.Path), ("values", values)),
                        _ => Obj(("op", "ains"), ("path", t.Path), ("at", (long)_rng.Int(t.Length + 1)), ("values", values)),
                    };
                }
            case OpKinds.ArrayDelete:
            case OpKinds.XmlDelete:
                {
                    var seqs = TargetsOf(c, kind == OpKinds.ArrayDelete ? ["array"] : ["xmlf", "xmle"]).Where(t => t.Length > 0).ToList();
                    if (seqs.Count == 0)
                    {
                        return null;
                    }

                    var t = _rng.Pick(seqs);
                    var at = _rng.Int(t.Length);
                    return Obj(("op", "delete"), ("path", t.Path), ("at", (long)at), ("len", (long)_rng.Range(1, Math.Min(3, t.Length - at))));
                }
            case OpKinds.MapSet:
                {
                    var maps = TargetsOf(c, "map");
                    return maps.Count == 0 ? null : Obj(("op", "set"), ("path", _rng.Pick(maps).Path), ("key", Key()), ("value", MapValue()));
                }
            case OpKinds.MapDelete:
                {
                    var maps = TargetsOf(c, "map");
                    if (maps.Count == 0)
                    {
                        return null;
                    }

                    var t = _rng.Pick(maps);
                    if (_rng.Chance(0.1))
                    {
                        return Obj(("op", "clear"), ("path", t.Path));
                    }

                    return Obj(("op", "mdel"), ("path", t.Path), ("key", t.Keys.Count > 0 && _rng.Chance(0.8) ? _rng.Pick(t.Keys) : Key()));
                }
            case OpKinds.XmlInsert:
                {
                    var xs = TargetsOf(c, "xmlf", "xmle");
                    if (xs.Count == 0)
                    {
                        return null;
                    }

                    var t = _rng.Pick(xs);
                    var values = Enumerable.Range(0, _rng.Range(1, 2)).Select(_ => _rng.Chance(0.6) ? XmlElement(_rng.Int(2)) : Obj(("$t", "xmlt"), ("s", Text()))).ToList<object>();
                    return _rng.Int(5) switch
                    {
                        0 => Obj(("op", "xafter"), ("path", t.Path), ("ref", t.Length == 0 || _rng.Chance(0.3) ? null : (long)_rng.Int(t.Length)), ("values", values)),
                        1 => Obj(("op", "push"), ("path", t.Path), ("values", values)),
                        2 => Obj(("op", "unshift"), ("path", t.Path), ("values", values)),
                        _ => Obj(("op", "ains"), ("path", t.Path), ("at", (long)_rng.Int(t.Length + 1)), ("values", values)),
                    };
                }
            case OpKinds.XmlAttr:
                {
                    var es = TargetsOf(c, "xmle");
                    if (es.Count == 0)
                    {
                        return null;
                    }

                    var t = _rng.Pick(es);
                    return _rng.Chance(0.25)
                        ? Obj(("op", "rmAttr"), ("path", t.Path), ("key", _rng.Pick(new[] { "class", "id", "href" })))
                        : Obj(("op", "setAttr"), ("path", t.Path), ("key", _rng.Pick(new[] { "class", "id", "href" })), ("value", _rng.Chance(0.8) ? Text() : PlainValue(1)));
                }
            case OpKinds.Nested:
                {
                    var hosts = TargetsOf(c, "array", "map");
                    if (hosts.Count == 0)
                    {
                        return null;
                    }

                    var t = _rng.Pick(hosts);
                    var value = NestedType(2);
                    return t.Kind == "map"
                        ? Obj(("op", "set"), ("path", t.Path), ("key", Key()), ("value", value))
                        : Obj(("op", "ains"), ("path", t.Path), ("at", (long)_rng.Int(t.Length + 1)), ("values", new List<object> { value }));
                }
            case OpKinds.Transact:
                {
                    if (!allowCompound)
                    {
                        return null;
                    }

                    var ops = new List<object>();
                    var count = _rng.Range(2, 4);
                    for (var i = 0; i < count; i++)
                    {
                        var inner = LocalOp(c, _rng.Pick(new[] { OpKinds.TextInsert, OpKinds.TextDelete, OpKinds.TextFormat, OpKinds.ArrayInsert, OpKinds.ArrayDelete, OpKinds.MapSet, OpKinds.MapDelete, OpKinds.XmlInsert, OpKinds.XmlAttr, OpKinds.Nested }), allowCompound: false);
                        if (inner != null)
                        {
                            ops.Add(inner);
                        }
                    }

                    return ops.Count == 0 ? null : Obj(("op", "transact"), ("origin", _rng.Chance(0.4) ? null : _rng.Pick(Origins)), ("ops", ops));
                }
            case OpKinds.Undo:
                {
                    var ums = _run.UndoManagers(c).ToList();
                    if (ums.Count == 0 || _rng.Chance(0.15))
                    {
                        if (!allowCompound)
                        {
                            return null;
                        }

                        var candidates = _run.Targets(c);
                        var scope = new List<object>();
                        if (_rng.Chance(0.15))
                        {
                            scope.Add("$doc");
                        }
                        else
                        {
                            for (var i = _rng.Range(1, 2); i > 0; i--)
                            {
                                scope.Add(_rng.Pick(candidates).Path);
                            }
                        }

                        var um = Obj(("op", "um"), ("id", "u" + _ids++), ("scope", scope), ("timeout", _rng.Chance(0.5) ? "inf" : "0"));
                        switch (_rng.Int(4))
                        {
                            case 0: um["origins"] = new List<object> { null, "o1" }; break;
                            case 1: um["origins"] = new List<object> { "o1" }; break;
                            case 2: um["origins"] = new List<object> { null, ScriptRun.Net }; break;
                        }

                        return um;
                    }

                    var id = _rng.Pick(ums);
                    var r = _rng.Int(10);
                    return Obj(("op", r < 5 ? "undo" : r < 8 ? "redo" : r < 9 ? "stop" : "umclear"), ("id", id));
                }
            case OpKinds.Snapshot:
                {
                    var snaps = _run.Snapshots(c).ToList();
                    if (snaps.Count == 0 || _rng.Chance(0.3))
                    {
                        // Snapshots are only meaningful without garbage collection; Yjs reads garbage otherwise.
                        return _run.Clients[c].Replica.Gc ? null : Obj(("op", "snap"), ("id", "s" + _ids++));
                    }

                    if (_rng.Chance(0.35))
                    {
                        return Obj(("op", "docFromSnap"), ("id", _rng.Pick(snaps)));
                    }

                    var t = _rng.Pick(_run.Targets(c).Where(x => x.Kind != "hook").ToList());
                    var read = Obj(("op", "readSnap"), ("id", _rng.Pick(snaps)), ("path", t.Path));
                    if (t.Kind is "text" or "xmlt" && snaps.Count > 1 && _rng.Chance(0.3))
                    {
                        read["prev"] = _rng.Pick(snaps);
                    }

                    return read;
                }
            case OpKinds.RelPos:
                {
                    if (_run.RelativePositions.Count == 0 || _rng.Chance(0.4))
                    {
                        var seqs = TargetsOf(c, "text", "xmlt", "array", "xmlf", "xmle");
                        if (seqs.Count == 0)
                        {
                            return null;
                        }

                        var t = _rng.Pick(seqs);
                        return Obj(("op", "rpos"), ("id", "r" + _ids++), ("path", t.Path), ("at", (long)_rng.Int(t.Length + 1)), ("assoc", _rng.Chance(0.5) ? 0L : -1L));
                    }

                    return Obj(("op", "rres"), ("id", _rng.Pick(_run.RelativePositions.Keys.ToList())));
                }
            case OpKinds.Observe:
                {
                    var nested = _run.Targets(c).Where(t => t.Path.Count > 1).ToList();
                    return nested.Count == 0 ? null : Obj(("op", "observe"), ("path", _rng.Pick(nested).Path));
                }
            case OpKinds.React:
                {
                    if (!allowCompound)
                    {
                        return null;
                    }

                    var ops = new List<object>();
                    for (var i = _rng.Range(1, 4); i > 0; i--)
                    {
                        if (LocalOp(c, _rng.Pick(new[] { OpKinds.TextInsert, OpKinds.TextInsert, OpKinds.TextDelete, OpKinds.ArrayInsert, OpKinds.ArrayDelete, OpKinds.MapSet }), allowCompound: false) is { } op)
                        {
                            ops.Add(op);
                        }
                    }

                    var targets = _run.Targets(c).Where(t => t.Kind != "hook").ToList();
                    if (ops.Count == 0 || targets.Count == 0)
                    {
                        return null;
                    }

                    var react = Obj(("op", "react"), ("id", "x" + _ids++), ("path", _rng.Pick(targets).Path), ("txn", _rng.Chance(0.5)), ("ops", ops));
                    if (_options.Hooks && _rng.Chance(0.75))
                    {
                        react["on"] = _rng.Pick(HookTriggers);
                    }

                    return react;
                }
            case OpKinds.Typing:
                return allowCompound ? Typing(c) : null;
            default:
                throw new ArgumentException($"unknown kind {kind}", nameof(kind));
        }
    }

    /// <summary>
    /// A transaction that types characters (or pushes or inserts values) one after the other at one place, with,
    /// in the middle, an update this client lacks applied, or a snapshot taken and read (without garbage collection).
    /// </summary>
    private Dictionary<string, object> Typing(int c)
    {
        var lists = TargetsOf(c, "text", "xmlt", "array");
        if (lists.Count == 0)
        {
            return null;
        }

        var t = _rng.Pick(lists);
        var at = _rng.Chance(0.5) ? t.Length : _rng.Int(t.Length + 1);
        var ops = new List<object>();
        void Run(int n)
        {
            for (var i = 0; i < n; i++)
            {
                if (t.Kind == "array")
                {
                    var values = new List<object> { ArrayValue() };
                    ops.Add(_rng.Chance(0.3) ? Obj(("op", "push"), ("path", t.Path), ("values", values)) : Obj(("op", "ains"), ("path", t.Path), ("at", (long)at), ("values", values)));
                }
                else
                {
                    ops.Add(Obj(("op", "insert"), ("path", t.Path), ("at", (long)at), ("text", _rng.Pick(new[] { "a", "b", "c", "x", "é", "😀" }))));
                }

                at++;
            }
        }

        Run(_rng.Range(1, 6));
        var missing = Missing(c);
        if (missing.Count > 0 && _rng.Chance(0.5))
        {
            var (from, k) = _rng.Pick(missing);
            ops.Add(Obj(("op", "deliver"), ("from", (long)from), ("k", (long)k), ("to", (long)c), ("via", Via())));
            _has[c].Add((from, k));
        }
        else if (!_run.Clients[c].Replica.Gc && _rng.Chance(0.6))
        {
            var id = "s" + _ids++;
            ops.Add(Obj(("op", "snap"), ("id", id)));
            Run(_rng.Range(1, 4));
            var snaps = _run.Snapshots(c).ToList();
            var read = Obj(("op", "readSnap"), ("id", _rng.Chance(0.5) || snaps.Count == 0 ? id : _rng.Pick(snaps)), ("path", t.Path));
            if (t.Kind != "array" && snaps.Count > 0 && _rng.Chance(0.5))
            {
                read["prev"] = _rng.Chance(0.5) ? id : _rng.Pick(snaps);
            }

            ops.Add(read);
        }

        if (_rng.Chance(0.3))
        {
            ops.Add(Obj(("op", "delete"), ("path", t.Path), ("at", (long)Math.Max(0, at - _rng.Range(1, 3))), ("len", (long)_rng.Range(1, 2))));
        }

        Run(_rng.Range(1, 6));
        return Obj(("op", "transact"), ("origin", _rng.Chance(0.6) ? null : _rng.Pick(Origins)), ("ops", ops));
    }

    private Dictionary<string, object> TextOp(Target t, string kind)
    {
        var len = t.Length;
        switch (kind)
        {
            case OpKinds.TextInsert:
                {
                    var op = Obj(("op", "insert"), ("path", t.Path), ("at", (long)_rng.Int(len + 1)), ("text", Text()));
                    if (_rng.Chance(0.3))
                    {
                        op["attrs"] = Attrs();
                    }

                    return op;
                }
            case OpKinds.TextDelete:
                {
                    if (len == 0)
                    {
                        return TextOp(t, OpKinds.TextInsert);
                    }

                    var at = _rng.Int(len);
                    return Obj(("op", "delete"), ("path", t.Path), ("at", (long)at), ("len", (long)_rng.Range(1, Math.Min(5, len - at))));
                }
            case OpKinds.TextFormat:
                {
                    var at = _rng.Int(len + 1);
                    // Now and then past the end: Yjs then appends newlines.
                    var flen = _rng.Chance(0.1) ? len - at + _rng.Range(1, 2) : _rng.Range(1, Math.Max(1, len - at));
                    return Obj(("op", "format"), ("path", t.Path), ("at", (long)at), ("len", (long)flen), ("attrs", Attrs()));
                }
            case OpKinds.TextEmbed:
                {
                    var embed = _rng.Chance(0.25)
                        ? NestedType(1)
                        : Obj(("image", _rng.Pick(new[] { "a.png", "b.png" })), ("w", (long)_rng.Int(100)));
                    var op = Obj(("op", "embed"), ("path", t.Path), ("at", (long)_rng.Int(len + 1)), ("value", embed));
                    if (_rng.Chance(0.3))
                    {
                        op["attrs"] = Attrs();
                    }

                    return op;
                }
            case OpKinds.TextDelta:
                {
                    var delta = new List<object>();
                    var remaining = len;
                    for (var i = _rng.Range(1, 4); i > 0; i--)
                    {
                        switch (_rng.Int(3))
                        {
                            case 0 when _rng.Chance(0.15):
                                {
                                    // Formats beyond the end: Yjs appends a newline per missing character.
                                    delta.Add(Obj(("retain", (long)(remaining + _rng.Range(1, 3))), ("attrs", Attrs())));
                                    remaining = 0;
                                    break;
                                }
                            case 0 when remaining > 0:
                                {
                                    var n = _rng.Range(1, remaining);
                                    remaining -= n;
                                    var retain = Obj(("retain", (long)n));
                                    if (_rng.Chance(0.5))
                                    {
                                        retain["attrs"] = Attrs();
                                    }

                                    delta.Add(retain);
                                    break;
                                }
                            case 1 when remaining > 0:
                                {
                                    var n = _rng.Range(1, Math.Min(3, remaining));
                                    remaining -= n;
                                    delta.Add(Obj(("delete", (long)n)));
                                    break;
                                }
                            default:
                                {
                                    var ins = Obj(("insert", _rng.Int(20) switch
                                    {
                                        < 15 => Text(),
                                        < 17 => Obj(("image", "c.png")),
                                        _ => NestedType(1),
                                    }));
                                    if (_rng.Chance(0.4))
                                    {
                                        ins["attrs"] = Attrs();
                                    }

                                    delta.Add(ins);
                                    break;
                                }
                        }
                    }

                    var op = Obj(("op", "delta"), ("path", t.Path), ("delta", delta));
                    if (_rng.Chance(0.2))
                    {
                        op["sanitize"] = false;
                    }

                    return op;
                }
            case OpKinds.TextAttr:
                return _rng.Chance(0.3)
                    ? Obj(("op", "rmAttr"), ("path", t.Path), ("key", _rng.Pick(new[] { "lang", "dir" })))
                    : Obj(("op", "setAttr"), ("path", t.Path), ("key", _rng.Pick(new[] { "lang", "dir" })), ("value", PlainValue(1)));
            default:
                throw new ArgumentException(kind, nameof(kind));
        }
    }

    // ---------------------------------------------------------------- values

    private string Key() => _rng.Pick(Keys);

    private string Text()
    {
        var s = string.Concat(Enumerable.Range(0, _rng.Range(1, 3)).Select(_ => _rng.Pick(_options.Astral ? Words : BmpWords)));
        if (_options.LoneSurrogates && _rng.Chance(0.03))
        {
            s += _rng.Chance(0.5) ? "\ud800" : "\udc00";
        }

        return s;
    }

    /// <summary>A new text, sometimes with a delta applied before it is in a document (Yjs keeps it as pending).</summary>
    private Dictionary<string, object> PrelimText(Dictionary<string, object> text)
    {
        if (!_rng.Chance(0.3))
        {
            return text;
        }

        var length = text.Str("s")?.Length ?? 0;
        var delta = new List<object>();
        if (length > 0 && _rng.Chance(0.5))
        {
            delta.Add(Obj(("retain", (long)_rng.Range(1, length + 1)), ("attrs", Attrs())));
        }

        var ins = Obj(("insert", _rng.Chance(0.8) ? Text() : Obj(("image", "c.png"))));
        if (_rng.Chance(0.5))
        {
            ins["attrs"] = Attrs();
        }

        delta.Add(ins);
        if (length > 1 && _rng.Chance(0.3))
        {
            delta.Add(Obj(("delete", 1L)));
        }

        text["dl"] = delta;
        if (_rng.Chance(0.2))
        {
            text["dls"] = false;
        }

        return text;
    }

    private Dictionary<string, object> Attrs()
    {
        var attrs = new Dictionary<string, object>();
        for (var i = _rng.Range(1, 2); i > 0; i--)
        {
            var key = _rng.Pick(AttrKeys);
            attrs[key] = _rng.Int(8) switch
            {
                0 => null,
                1 => "#f00",
                2 => 12L,
                3 => Obj(("family", "serif"), ("w", 1.5)),
                4 => new List<object> { 1L, "a" },
                5 => false,
                6 => 1.5,
                _ => true,
            };
        }

        return attrs;
    }

    /// <summary>A value Yjs accepts at the top level of an array insert (no BigInt there: Yjs throws).</summary>
    private object ArrayValue() => _rng.Chance(0.15) ? NestedType(2) : _options.Subdocs && _rng.Chance(0.05) ? Subdoc() : PlainValue(0, bigint: false);

    private object MapValue() => _rng.Chance(0.15) ? NestedType(2) : _options.Subdocs && _rng.Chance(0.04) ? Subdoc() : PlainValue(0, bigint: true);

    private object Subdoc() => Obj(("$doc", Obj(("g", "doc-" + _ids++), ("autoLoad", _rng.Chance(0.3)))));

    private object PlainValue(int depth, bool bigint = true)
    {
        switch (_rng.Int(depth > 1 ? 14 : 16))
        {
            case 0: return null;
            case 1: return _rng.Chance(0.5);
            case 2: return (long)_rng.Range(-5, 100);
            case 3: return Obj(("$i", _rng.Pick(new[] { "2147483647", "2147483648", "-2147483649", "4294967296", "9007199254740991", "-9007199254740991" })));
            case 4: return _rng.Pick(new object[] { 1.5, -0.25, 1e300, 3.14 });
            case 5: return Obj(("$f", _rng.Pick(new[] { "NaN", "Infinity", "-Infinity", "-0", "4340000000000001", "3ff0000000000001" })));
            case 6: return Text();
            case 7: return Obj(("$b", Convert.ToBase64String(Enumerable.Range(0, _rng.Int(4)).Select(_ => (byte)_rng.Int(256)).ToArray())));
            case 8: return bigint ? Obj(("$n", _rng.Pick(new[] { "0", "-1", "12345678901234567", "9223372036854775807" }))) : "n";
            case 9:
            case 10: return "s" + _rng.Int(10);
            case 11: return (long)_rng.Int(10);
            case 12: return true;
            case 13: return "";
            case 14: return Enumerable.Range(0, _rng.Int(3)).Select(_ => PlainValue(depth + 1)).ToList();
            default:
                {
                    var o = new Dictionary<string, object>();
                    for (var i = _rng.Int(3); i > 0; i--)
                    {
                        o[Key()] = PlainValue(depth + 1);
                    }

                    return o;
                }
        }
    }

    private Dictionary<string, object> NestedType(int depth)
    {
        switch (_rng.Int(depth > 0 ? 4 : 2))
        {
            case 0: return PrelimText(_rng.Chance(0.5) ? Obj(("$t", "text"), ("s", Text())) : Obj(("$t", "text")));
            case 1: return Obj(("$t", "map"), ("v", Enumerable.Range(0, _rng.Int(3)).Select(_ => (object)new List<object> { Key(), PlainValue(1) }).ToList()));
            case 2: return Obj(("$t", "array"), ("v", Enumerable.Range(0, _rng.Int(3)).Select(_ => _rng.Chance(0.3) ? NestedType(depth - 1) : PlainValue(1, bigint: false)).ToList()));
            default: return _rng.Chance(0.5) ? XmlElement(1) : Obj(("$t", "xmlf"), ("v", new List<object> { XmlElement(0) }));
        }
    }

    private Dictionary<string, object> XmlElement(int depth)
    {
        var e = Obj(("$t", "xmle"), ("n", _rng.Pick(new[] { "p", "div", "span" })));
        if (_rng.Chance(0.5))
        {
            e["a"] = new List<object> { new List<object> { "class", "c" + _rng.Int(3) } };
        }

        if (depth > 0 && _rng.Chance(0.6))
        {
            e["v"] = new List<object> { _rng.Chance(0.5) ? Obj(("$t", "xmlt"), ("s", Text())) : XmlElement(depth - 1) };
        }

        return e;
    }
}
