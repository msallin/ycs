namespace Ycs.Layout;

/// <summary>
/// What a small document holds, today and in the prototype under two page policies (<see cref="Paging.Fixed"/>,
/// <see cref="Paging.Geometric"/>), and what a document of many clients with one struct each holds per client. Many
/// documents of each shape are kept alive at once and the managed heap is divided by their number. The prototype
/// replaces only the struct storage, so a designed document is today's document of the same root types without structs
/// (the "empty" row) plus what the prototype store holds beyond the store of that empty document. Each side is built
/// on its own, so the keys and values the documents create count on each.
/// </summary>
internal static class Small
{
    private sealed record Shape(string Name, int Docs, string Root, Action<YDoc, Random> Fill);

    public static void Run(Action<bool, string> check)
    {
        var shapes = new List<Shape>
        {
            new("empty, a root text", 1000, "text", (d, r) => d.GetText("text")),
            new("text \"hello\" (one insert)", 1000, "text", (d, r) => d.GetText("text").Insert(0, "hello")),
        };
        foreach (var n in new[] { 20, 200, 1000, 5000, 20000 })
        {
            shapes.Add(new($"text, {n:N0} characters typed at random positions, {n / 10:N0} deleted", Math.Clamp(200_000 / n, 10, 1000), "text", (d, r) => Type(d, r, n)));
        }

        shapes.Add(new("empty, a root map", 1000, "map", (d, r) => d.GetMap("map")));
        shapes.Add(new("map, 20 keys (an integer each)", 1000, "map", (d, r) => SetKeys(d, 20)));
        shapes.Add(new("map, 1,000 keys", 100, "map", (d, r) => SetKeys(d, 1000)));

        Console.WriteLine();
        Console.WriteLine("| document | documents | structs each | today, B each | designed, fixed pages | designed, geometric pages | geometric / today |");
        Console.WriteLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: |");
        var empty = new Dictionary<string, (double Today, double Fixed, double Geometric)>();
        foreach (var s in shapes)
        {
            var m = Measure(s.Docs, s.Root, d => s.Fill(d, new Random(s.Docs)), check, s.Name, out var structs);
            if (!empty.TryGetValue(s.Root, out var e))
            {
                empty[s.Root] = e = m;
            }

            var fixedPages = e.Today + m.Fixed - e.Fixed;
            var geometric = e.Today + m.Geometric - e.Geometric;
            Console.WriteLine($"| {s.Name} | {s.Docs:N0} | {structs:N0} | {m.Today:N0} | {fixedPages:N0} | {geometric:N0} | {geometric / m.Today:F2} |");
        }

        // Many clients, one struct each: one document that applied the updates of 20,000 others (a key each).
        const int clients = 20_000;
        var updates = new byte[clients][];
        for (var i = 0; i < clients; i++)
        {
            var other = new YDoc(new YDocOptions { ClientId = i + 1 });
            other.GetMap("map").Set("k" + i, (long)i);
            updates[i] = other.EncodeStateAsUpdateV1();
        }

        var c = Measure(1, "map", d =>
        {
            foreach (var u in updates)
            {
                d.ApplyUpdateV1(u);
            }
        }, check, "many clients", out _);
        var (et, ef, eg) = empty["map"];
        Console.WriteLine();
        Console.WriteLine($"Many clients ({clients:N0}, one map entry each, in one document), per client: today {c.Today / clients:N0} B, " +
            $"designed {(et + c.Fixed - ef) / clients:N0} B (fixed pages), {(et + c.Geometric - eg) / clients:N0} B (geometric pages).");
        System.GC.KeepAlive(updates);
    }

    private static void Type(YDoc doc, Random rng, int n)
    {
        var text = doc.GetText("text");
        for (var i = 0; i < n; i++)
        {
            text.Insert(rng.Next(text.Length + 1), ((char)('a' + rng.Next(26))).ToString());
        }

        for (var i = 0; i < n / 10; i++)
        {
            text.Remove(rng.Next(text.Length), 1);
        }
    }

    private static void SetKeys(YDoc doc, int n)
    {
        var map = doc.GetMap("map");
        for (var i = 0; i < n; i++)
        {
            map.Set("k" + i, (long)i);
        }
    }

    private static (double Today, double Fixed, double Geometric) Measure(int count, string root, Action<YDoc> fill, Action<bool, string> check, string name, out long structs)
    {
        var m0 = Held();
        var docs = Build(count, fill);
        var today = (Held() - m0) / (double)count;
        structs = docs[0].Store.Clients.Values.Sum(l => (long)l.Count);
        docs = null;
        return (today, Prototype(count, root, fill, Paging.Fixed, check, name), Prototype(count, root, fill, Paging.Geometric, check, name));
    }

    private static YDoc[] Build(int count, Action<YDoc> fill)
    {
        var docs = new YDoc[count];
        for (var i = 0; i < count; i++)
        {
            docs[i] = new YDoc(new YDocOptions { ClientId = 1 });
            fill(docs[i]);
        }

        return docs;
    }

    /// <summary>What dropping the stores frees, after their documents were dropped (so the keys and values, which both share, count here).</summary>
    private static double Prototype(int count, string root, Action<YDoc> fill, Paging paging, Action<bool, string> check, string name)
    {
        var docs = Build(count, fill);
        var stores = docs.Select(d => Builder.Build(d, paging)).ToArray();
        for (var i = 0; i < count; i++)
        {
            check(Builder.Check(stores[i]) == null, $"small, {name}, {paging.Name}: {Builder.Check(stores[i])}");
            if (stores[i].Count > 0 && root == "text")
            {
                check(docs[i].GetText("text").ToString() == Builder.Text(stores[i], "text"), $"small, {name}, {paging.Name}: the prototype's text differs");
            }
        }

        var weak = new WeakReference(docs[0]);
        docs = null;
        var m0 = Held();
        check(!weak.IsAlive, $"small, {name}: the document was still alive when the prototype was measured");
        System.GC.KeepAlive(stores);
        stores = null;
        return (m0 - Held()) / (double)count;
    }

    private static long Held()
    {
        System.GC.Collect(System.GC.MaxGeneration, System.GCCollectionMode.Forced, blocking: true, compacting: true);
        System.GC.WaitForPendingFinalizers();
        System.GC.Collect(System.GC.MaxGeneration, System.GCCollectionMode.Forced, blocking: true, compacting: true);
        return System.GC.GetTotalMemory(forceFullCollection: true);
    }
}
