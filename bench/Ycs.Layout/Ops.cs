using System.Diagnostics;
using Ycs.Compare;

namespace Ycs.Layout;

/// <summary>
/// The three operations the design claims to make cheaper, on the same documents, today versus the prototype: the
/// lookup by (client, clock), Yjs' marker walk-left over a client's chain, and a split in the middle of a client's
/// structs. Each result is compared between the two sides before its time counts. Times: the best of 5 rounds.
/// </summary>
internal static class Ops
{
    private const int Rounds = 5;

    public static void Run(Fixtures f, Action<bool, string> check)
    {
        Console.WriteLine();
        Console.WriteLine("| operation | document | today | prototype | today / prototype |");
        Console.WriteLine("| --- | --- | ---: | ---: | ---: |");

        const string find = "find by (client, clock)";
        Find(find, "trace-replay", Workloads.TraceDoc(f), check);
        Find(find, "concurrent-20 (client 0's document)", Workloads.Concurrent()[0], check);
        var random = Workloads.All(f).First(w => w.Name == "text-random-100k").Build()[0];
        Find(find, "text-random-100k", random, check);

        const string split = "split in the middle of a client's structs";
        var traceState = Workloads.TraceDoc(f).EncodeStateAsUpdate();
        Split(split, "trace-replay", () => Load(traceState), (d, s) => d.GetText("text").ToString() == Builder.Text(s, "text"), check);

        var sw = Stopwatch.StartNew();
        var delete = Workloads.DeleteDoc();
        Console.Error.WriteLine($"array-delete-random built in {sw.Elapsed.TotalSeconds:F0} s");
        var deleteState = delete.EncodeStateAsUpdate();
        Split(split, "array-delete-random", () => Load(deleteState), (d, s) => d.GetArray("array").Select(v => (object)v.GetInt64()).SequenceEqual(Builder.Array(s, "array")), check);
        WalkLeft(delete, check);
    }

    // A document loaded from a state has the struct boundaries of the one that wrote it.
    private static YDoc Load(byte[] state)
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        doc.ApplyUpdate(state, null, limits: UpdateLimits.Unlimited);
        return doc;
    }

    private static string Ns(double ns) => ns >= 1e6 ? $"{ns / 1e6:F2} ms" : ns >= 1e3 ? $"{ns / 1e3:F2} µs" : $"{ns:F1} ns";

    private static double Best(Action body, int ops)
    {
        var best = double.MaxValue;
        for (var r = 0; r < Rounds; r++)
        {
            var sw = Stopwatch.StartNew();
            body();
            best = Math.Min(best, sw.Elapsed.TotalNanoseconds / ops);
        }

        return best;
    }

    private static void Find(string op, string name, YDoc doc, Action<bool, string> check)
    {
        var store = Builder.Build(doc);
        var rng = new Rng(Inputs.Seed + 100);
        const int n = 1_000_000;
        var clients = new int[n];
        var clocks = new long[n];
        var lists = doc.Store.Clients;
        for (var i = 0; i < n; i++)
        {
            clients[i] = rng.Below(store.ClientIds.Length);
            clocks[i] = rng.Below((int)doc.Store.GetState(store.ClientIds[clients[i]]));
        }

        long a = 0, b = 0;
        var today = Best(() =>
        {
            a = 0;
            for (var i = 0; i < n; i++)
            {
                a += lists[store.ClientIds[clients[i]]].FindIndex(clocks[i]);
            }
        }, n);
        var proto = Best(() =>
        {
            b = 0;
            for (var i = 0; i < n; i++)
            {
                b += LayoutStore.FindIndex(store.Orders[clients[i]], (uint)clocks[i]);
            }
        }, n);
        check(a == b, $"{op} on {name}: different positions");
        var structs = lists.Values.Sum(l => l.Count);
        Console.WriteLine($"| {op} ({n:N0} random ids; {structs:N0} structs, {lists.Count} clients) | `{name}` | {Ns(today)} | {Ns(proto)} | {today / proto:F1}× |");
    }

    private static void Split(string op, string name, Func<YDoc> make, Func<YDoc, LayoutStore, bool> same, Action<bool, string> check)
    {
        // Ids inside structs longer than one, at most one per struct, in random order: a split each.
        var probe = make();
        var list = probe.Store.Clients[Inputs.ClientId];
        var rng = new Rng(Inputs.Seed + 101);
        var ids = list.Where(s => s.Length > 1).Select(s => s.Id.Clock + 1 + rng.Below(s.Length - 1)).ToList();
        var order = ids.OrderBy(_ => rng.Next()).Take(20_000).ToArray();
        probe = null;
        var structs = list.Count;
        list = null;

        // Each round on fresh documents: a split changes them.
        var today = double.MaxValue;
        var proto = double.MaxValue;
        for (var r = 0; r < Rounds; r++)
        {
            var doc = make();
            var store = Builder.Build(doc);
            var c = store.ClientIndex[Inputs.ClientId];
            var sw = new Stopwatch();
            doc.Transact(tr =>
            {
                sw.Start();
                foreach (var clock in order)
                {
                    doc.Store.GetItemCleanStart(tr, new ItemId(Inputs.ClientId, clock));
                }

                sw.Stop();
            });
            today = Math.Min(today, sw.Elapsed.TotalNanoseconds / order.Length);

            // The transaction's cleanup merged the halves again; the prototype has no cleanup (none is timed today).
            sw.Restart();
            foreach (var clock in order)
            {
                store.SplitAt(c, (uint)clock);
            }

            sw.Stop();
            proto = Math.Min(proto, sw.Elapsed.TotalNanoseconds / order.Length);
            check(store.Orders[c].Count == structs + order.Length && Builder.Check(store) == null && same(doc, store),
                $"{op}: the prototype's store is wrong after the splits");
        }

        Console.WriteLine($"| {op} ({order.Length:N0} splits at random ids of a {structs:N0}-struct client; with the lookup) | `{name}` | {Ns(today)} | {Ns(proto)} | {today / proto:F1}× |");
    }

    private static void WalkLeft(YDoc doc, Action<bool, string> check)
    {
        var store = Builder.Build(doc);
        var items = new List<ItemRef>();
        for (var it = doc.GetArray("array")._start.AsItem; !it.IsNull; it = it.Right.AsItem)
        {
            items.Add(it);
        }

        var c = store.ClientIndex[Inputs.ClientId];
        var rng = new Rng(Inputs.Seed + 102);
        const int n = 2_000;
        var starts = Enumerable.Range(0, n).Select(_ => items[rng.Below(items.Count)]).ToArray();
        var handles = starts.Select(it => store.Orders[c].Handles[LayoutStore.FindIndex(store.Orders[c], (uint)it.Id.Clock)]).ToArray();

        // Yjs' findMarker, its last loop: on to the left while the left neighbour is the struct before by clock.
        long steps = 0;
        var todayEnds = new (long Clock, long Delta)[n];
        var today = Best(() =>
        {
            steps = 0;
            for (var i = 0; i < n; i++)
            {
                var p = starts[i];
                long delta = 0;
                while (!p.Left.IsNull && p.Left.Id.Client == p.Id.Client && p.Left.Id.Clock + p.Left.Length == p.Id.Clock)
                {
                    p = p.Left.AsItem;
                    if (!p.Deleted && p.Countable)
                    {
                        delta += p.Length;
                    }

                    steps++;
                }

                todayEnds[i] = (p.Id.Clock, delta);
            }
        }, n);

        var slots = store.Slots;
        var literalEnds = new (long Clock, long Delta)[n];
        var literal = Best(() =>
        {
            for (var i = 0; i < n; i++)
            {
                var h = handles[i];
                long delta = 0;
                while (true)
                {
                    ref var s = ref slots[h];
                    if (s.Left < 0)
                    {
                        break;
                    }

                    ref var l = ref slots[s.Left];
                    if (l.Client != s.Client || l.Clock + (uint)l.Length != s.Clock)
                    {
                        break;
                    }

                    h = s.Left;
                    if (!l.Deleted && l.Countable)
                    {
                        delta += l.Length;
                    }
                }

                literalEnds[i] = (slots[h].Clock, delta);
            }
        }, n);

        var index = new ChainIndex(store, c);
        var order = store.Orders[c];
        var indexedEnds = new (long Clock, long Delta)[n];
        var indexed = Best(() =>
        {
            for (var i = 0; i < n; i++)
            {
                var pos = LayoutStore.FindIndex(order, slots[handles[i]].Clock);
                var head = index.Head(pos);
                indexedEnds[i] = (order.Starts[head], index.Prefix(pos) - index.Prefix(head));
            }
        }, n);

        check(todayEnds.SequenceEqual(literalEnds) && todayEnds.SequenceEqual(indexedEnds), "walk-left: the prototype ends elsewhere than Yjs' loop");
        Console.WriteLine($"| marker walk-left, Yjs' loop ({n:N0} walks from random items, {steps / n:N0} steps each; {items.Count:N0} items) | `array-delete-random` | {Ns(today)} | {Ns(literal)} | {today / literal:F1}× |");
        Console.WriteLine($"| marker walk-left, run-head index (the same walks; result identical) | `array-delete-random` | {Ns(today)} | {Ns(indexed)} | {today / indexed:F0}× |");
    }
}
