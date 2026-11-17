using System.Diagnostics;
using Ycs.Compare;

namespace Ycs.Layout;

/// <summary>
/// Editing, today against the prototype (<see cref="Integrator"/>): the integration of a recorded sequence of updates'
/// structs, and the application of their delete sets. Both sides get the same structs in the same order, decoded
/// beforehand (ycs' <c>UpdateReader</c>; the prototype's staged form is converted from the same decode), and start from
/// the same document. Today's side runs ycs' own <c>ItemRef.Adopt</c> (what
/// <c>IntegrationPlan.Execute</c> runs per struct) and a copy of <c>StructStore.ApplyDeleteSet</c>'s loop on ycs'
/// primitives, inside one transaction whose cleanup is not timed. Before a time counts, the list (every struct's id,
/// length and deleted flag in list order), the map entries' chains, the types' lengths and map counts and each client's
/// clock order are compared between the two sides, and the content after the deletes against the document that
/// recorded the updates.
/// </summary>
internal static class Editing
{
    private const int Rounds = 5;
    private const long ReceiverId = 999_999;
    private const int LeafCapacity = 128; // ycs' ClientStructs.LeafCapacity

    /// <summary>Updates recorded on one document, applied to a receiver that starts from <see cref="Start"/>.</summary>
    private sealed record Recording(string Name, string Root, char RootKind, byte[] Start, byte[][] Updates, string Content);

    private sealed record Decoded(List<(StagingTable Table, int Index)> Staged, List<(long Client, (long Clock, long Length)[] Ranges)> Deletes);

    public static void Run(Fixtures f, Action<bool, string> check)
    {
        var trace = YcsCases.Record(f.Trace);
        var traceRec = new Recording("trace-replay", "text", 't', null, [.. trace.Updates], trace.Text);

        var (_, baseUpdate, diffs) = Workloads.ConcurrentUpdates();
        var concurrentRec = new Recording("concurrent-20", "text", 't', baseUpdate, diffs, Workloads.Concurrent()[0].GetText("text").ToString());

        var randomRec = RandomSync();

        var mapRec = Record("map-set-same", "map", 'm', d =>
        {
            var map = d.GetMap("map");
            for (var i = 0; i < Inputs.MapN; i++)
            {
                map["key"] = (long)i;
            }
        });

        var arrayRec = Record("array-delete-random", "array", 'a', d =>
        {
            var array = d.GetArray("array");
            array.Add(Enumerable.Range(0, Inputs.ArrayN).Select(i => (YValue)(long)i).ToArray());
            foreach (var p in Inputs.ArrayDeletes)
            {
                array.RemoveRange(p, 1);
            }
        });

        // array-delete-random's first update (the 100,000 values) is its receiver's start; the rest are deletes only.
        arrayRec = arrayRec with { Start = arrayRec.Updates[0], Updates = arrayRec.Updates[1..] };

        Console.WriteLine();
        Console.WriteLine("| operation | workload | count | today | prototype, leaves of 128 | prototype, one flat order | today / leaves | today, B each | leaves, B each |");
        Console.WriteLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var r in new[] { traceRec, concurrentRec, randomRec, mapRec })
        {
            Integrate(r, check);
        }

        foreach (var r in new[] { traceRec, concurrentRec, randomRec, arrayRec })
        {
            Delete(r, check);
        }
    }

    /// <summary>
    /// Five clients typing and deleting at random positions of one text, a random pair syncing both ways at every tenth
    /// step, half of the inserts near the start: the local updates in the order they were made (each depends only on
    /// earlier ones). Unlike concurrent-20, items land between other clients' concurrent items, so the conflict loop
    /// takes its every branch. Its content is that of a document that applied the updates through ycs' public
    /// ApplyUpdate.
    /// </summary>
    private static Recording RandomSync()
    {
        const int steps = 20_000;
        var rng = new Rng(Inputs.Seed + 200);
        var updates = new List<byte[]>();
        var docs = new YDoc[5];
        for (var c = 0; c < docs.Length; c++)
        {
            docs[c] = new YDoc(new YDocOptions { ClientId = c + 1 });
            docs[c].Updated += (_, e) =>
            {
                if (e.Transaction.Local)
                {
                    updates.Add(e.Update.ToArray());
                }
            };
        }

        for (var i = 0; i < steps; i++)
        {
            var a = docs[rng.Below(docs.Length)];
            var text = a.GetText("text");
            if (rng.Below(10) == 0)
            {
                var b = docs[rng.Below(docs.Length)];
                b.ApplyUpdate(a.EncodeStateAsUpdate(b.EncodeStateVector()));
                a.ApplyUpdate(b.EncodeStateAsUpdate(a.EncodeStateVector()));
            }
            else if (text.Length > 0 && rng.Below(4) == 0)
            {
                var pos = rng.Below(text.Length);
                text.Remove(pos, Math.Min(1 + rng.Below(3), text.Length - pos));
            }
            else
            {
                // Half of the inserts near the start, where the clients' concurrent items meet.
                var pos = rng.Below(2) == 0 ? rng.Below(Math.Min(text.Length, 4) + 1) : rng.Below(text.Length + 1);
                text.Insert(pos, new string((char)('a' + rng.Below(26)), 1 + rng.Below(3)));
            }
        }

        var reference = new YDoc(new YDocOptions { ClientId = ReceiverId });
        foreach (var u in updates)
        {
            reference.ApplyUpdate(u);
        }

        return new("random-sync", "text", 't', null, [.. updates], reference.GetText("text").ToString());
    }

    private static Recording Record(string name, string root, char kind, Action<YDoc> edit)
    {
        var doc = new YDoc(new YDocOptions { ClientId = Inputs.ClientId });
        var updates = new List<byte[]>();
        doc.Updated += (_, e) => updates.Add(e.Update.ToArray());
        edit(doc);
        return new(name, root, kind, null, [.. updates], Content(doc, root, kind));
    }

    private static YDoc Receiver(Recording r)
    {
        var doc = new YDoc(new YDocOptions { ClientId = ReceiverId });
        _ = r.RootKind switch
        {
            't' => (YType)doc.GetText(r.Root),
            'm' => doc.GetMap(r.Root),
            _ => doc.GetArray(r.Root),
        };
        if (r.Start != null)
        {
            doc.ApplyUpdate(r.Start, null, limits: UpdateLimits.Unlimited);
        }

        return doc;
    }

    private static Decoded Decode(Recording r, YDoc receiver, Action<bool, string> check)
    {
        var staged = new List<(StagingTable, int)>();
        var deletes = new List<(long, (long, long)[])>();
        var state = new Dictionary<long, long>();
        foreach (var u in r.Updates)
        {
            // A table of its own per update: a lease's table is reset when it is disposed.
            var table = new StagingTable();
            var decoder = new UpdateDecoderV1(new Lib0Reader(u) { MaxDepth = int.MaxValue }, long.MaxValue);
            var cursor = new EncodingUtils.StructCursor(decoder, table);
            while (cursor.NextStaged() is >= 0 and var i)
            {
                // Every struct starts at its client's state: integrated in this order, each at offset 0.
                var (id, s) = (table.IdOf(i), table.At(i));
                var expected = state.TryGetValue(id.Client, out var c) ? c : receiver.Store.GetState(id.Client);
                if (s.Kind == StagedKind.Skip || id.Clock != expected)
                {
                    check(false, $"{r.Name}: a struct out of order, or a skip");
                }

                state[id.Client] = id.Clock + s.Length;
                staged.Add((table, i));
            }

            foreach (var (client, items) in DeleteSet.Read(decoder).Clients)
            {
                deletes.Add((client, [.. items.Select(i => (i.Clock, i.Length))]));
            }
        }

        return new(staged, deletes);
    }

    private static Staged[] Stage(List<(StagingTable Table, int Index)> structs) => [.. structs.Select(p =>
    {
        var (table, i) = p;
        var id = table.IdOf(i);
        var staged = table.At(i);
        var s = new Staged { Client = id.Client, Clock = id.Clock, Length = staged.Length, Kind = ContentKind.Gc };
        if (staged.Kind is StagedKind.GC or StagedKind.Skip)
        {
            return s;
        }

        if (table.LeftOrigin(i) is ItemId lo)
        {
            (s.HasOrigin, s.OriginClient, s.OriginClock) = (true, lo.Client, lo.Clock);
        }

        if (table.RightOrigin(i) is ItemId ro)
        {
            (s.HasROrigin, s.ROriginClient, s.ROriginClock) = (true, ro.Client, ro.Clock);
        }

        switch (staged.ParentKind)
        {
            case StagedParent.Root:
                s.ParentRoot = table.ParentName(i);
                break;
            case StagedParent.Id:
                var pid = table.ParentId(i);
                (s.HasParentId, s.ParentClient, s.ParentClock) = (true, pid.Client, pid.Clock);
                break;
        }

        s.Key = table.ParentSub(i);
        s.Countable = staged.Countable;
        switch (staged.Kind)
        {
            case StagedKind.String:
                s.Kind = ContentKind.String;
                s.Chars = table.Chars(i).ToArray();
                break;
            case StagedKind.Any:
                s.Kind = ContentKind.Any;
                s.Values = table.Values(i).ToArray();
                break;
            case StagedKind.Deleted:
                s.Kind = ContentKind.Deleted;
                break;
            case StagedKind.Type:
                s.Kind = ContentKind.Type;
                break;
            default:
                s.Kind = ContentKind.Cold;
                s.Cold = table.Cold(i);
                break;
        }

        return s;
    })];

    private static void Integrate(Recording r, Action<bool, string> check)
    {
        var probe = Receiver(r);
        var staged = Stage(Decode(r, probe, check).Staged);
        probe = null;
        var n = staged.Length;
        var today = new Best();
        var leaves = new Best();
        var flat = new Best();
        for (var round = 0; round < Rounds; round++)
        {
            var doc = Receiver(r);
            var structs = Decode(r, doc, check).Staged;
            var (leafStore, flatStore) = (Builder.Build(doc), Builder.Build(doc));
            var sw = new Stopwatch();
            long alloc = 0;
            List<Entry> todayShape = null;
            doc.Transact(tr =>
            {
                var s = doc.Store;
                alloc = System.GC.GetAllocatedBytesForCurrentThread();
                sw.Start();
                foreach (var (table, index) in structs)
                {
                    ItemRef.Adopt(table, index, tr, 0, true);
                }

                sw.Stop();
                alloc = System.GC.GetAllocatedBytesForCurrentThread() - alloc;
                todayShape = Shape(doc, r);
            }, local: false);
            today.Add(sw.Elapsed.TotalNanoseconds / n, alloc / (double)n);

            // At most two splits per struct (its origins): the slab is reserved for them (see the README).
            void Body(Integrator integrator)
            {
                for (var i = 0; i < staged.Length; i++)
                {
                    integrator.Integrate(in staged[i]);
                }
            }

            var content = Content(doc, r.Root, r.RootKind);
            foreach (var (store, capacity, best) in new[] { (leafStore, LeafCapacity, leaves), (flatStore, int.MaxValue, flat) })
            {
                Prototype(store, capacity, 3 * n, Body, n, best);
                check(Builder.Check(store) == null && todayShape.SequenceEqual(Shape(store, r)) && content == Content(store, r),
                    $"integrate {r.Name}: the prototype's store differs from the document's");
            }
        }

        Console.WriteLine($"| integrate | `{r.Name}` | {n:N0} structs | {Ns(today.Ns)} | {Ns(leaves.Ns)} | {Ns(flat.Ns)} | {today.Ns / leaves.Ns:F1}× | {today.Bytes:N0} | {leaves.Bytes:N0} |");
        Console.WriteLine($"| (today: decoding the structs, not timed) | `{r.Name}` | | | | | | {DecodeBytes(r) / n:N0} | |");
    }

    private static void Delete(Recording r, Action<bool, string> check)
    {
        var today = new Best();
        var leaves = new Best();
        var flat = new Best();
        var ranges = 0;
        for (var round = 0; round < Rounds; round++)
        {
            // The start: the receiver with every struct integrated, in a transaction of its own (its cleanup merges).
            var doc = Receiver(r);
            var (structs, deletes) = Decode(r, doc, check);
            doc.Transact(tr =>
            {
                foreach (var (table, index) in structs)
                {
                    ItemRef.Adopt(table, index, tr, 0, true);
                }
            }, local: false);
            ranges = deletes.Sum(d => d.Ranges.Length);
            check(deletes.All(d => d.Ranges.All(x => x.Clock + x.Length <= doc.Store.GetState(d.Client))), $"delete {r.Name}: a range the document does not hold");
            var (leafStore, flatStore) = (Builder.Build(doc), Builder.Build(doc));

            var sw = new Stopwatch();
            long alloc = 0;
            List<Entry> todayShape = null;
            doc.Transact(tr =>
            {
                alloc = System.GC.GetAllocatedBytesForCurrentThread();
                sw.Start();
                foreach (var (client, list) in deletes)
                {
                    ApplyDeleteSet(tr, doc.Store, client, list);
                }

                sw.Stop();
                alloc = System.GC.GetAllocatedBytesForCurrentThread() - alloc;
                todayShape = Shape(doc, r);
            }, local: false);
            today.Add(sw.Elapsed.TotalNanoseconds / ranges, alloc / (double)ranges);

            // At most two splits per range.
            var content = Content(doc, r.Root, r.RootKind);
            foreach (var (store, capacity, best) in new[] { (leafStore, LeafCapacity, leaves), (flatStore, int.MaxValue, flat) })
            {
                Prototype(store, capacity, 2 * ranges, i => i.ApplyDeleteSet(deletes), ranges, best);
                check(Builder.Check(store) == null && todayShape.SequenceEqual(Shape(store, r)) && content == Content(store, r) && content == r.Content,
                    $"delete {r.Name}: the prototype's store differs from the document's, or the document from the recording's");
            }
        }

        Console.WriteLine($"| delete | `{r.Name}` | {ranges:N0} ranges | {Ns(today.Ns)} | {Ns(leaves.Ns)} | {Ns(flat.Ns)} | {today.Ns / leaves.Ns:F1}× | {today.Bytes:N0} | {leaves.Bytes:N0} |");
    }

    /// <summary>The best time of the rounds, and the allocation of the last (the same in every round).</summary>
    private sealed class Best
    {
        public double Ns = double.MaxValue;
        public double Bytes;

        public void Add(double ns, double bytes)
        {
            Ns = Math.Min(Ns, ns);
            Bytes = bytes;
        }
    }

    // One prototype run on a store built from the same document as today's side: the slab reserved for the new slots
    // beforehand, then the body timed in a transaction of the integrator's (Begin), the orders written back for the checks.
    private static void Prototype(LayoutStore store, int capacity, int newSlots, Action<Integrator> body, int ops, Best best)
    {
        store.Reserve(store.Count + newSlots);
        var integrator = new Integrator(store, capacity);
        integrator.Begin();
        var alloc = System.GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        body(integrator);
        sw.Stop();
        alloc = System.GC.GetAllocatedBytesForCurrentThread() - alloc;
        best.Add(sw.Elapsed.TotalNanoseconds / ops, alloc / (double)ops);
        integrator.WriteBack();
    }

    // What today's decoder allocates for the recording's updates (the items and their content are made there).
    private static long DecodeBytes(Recording r)
    {
        var alloc = System.GC.GetAllocatedBytesForCurrentThread();
        foreach (var u in r.Updates)
        {
            using var lease = UpdateReader.Read(u, 0, u.Length, v2: false, long.MaxValue, int.MaxValue);
        }

        return System.GC.GetAllocatedBytesForCurrentThread() - alloc;
    }

    // StructStore.ApplyDeleteSet's loop for ranges the store holds (the pending branches left out), on ycs' primitives;
    // StructStore.SplitItem's bookkeeping (Version, MaySplitMapValues) is two field writes not repeated here.
    private static void ApplyDeleteSet(Transaction transaction, StructStore store, long client, (long Clock, long Length)[] ranges)
    {
        var structs = store.Clients[client];
        var hint = 0;
        foreach (var (clock, length) in ranges)
        {
            var clockEnd = clock + length;
            var index = structs.FindIndex(clock, ref hint);
            var str = structs[index];
            if (!str.Deleted && str.Id.Clock < clock)
            {
                structs.InsertSplit(index + 1, str.SplitItem(transaction, (int)(clock - str.Id.Clock)));
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
                        structs.InsertSplit(index, str.SplitItem(transaction, (int)(clockEnd - str.Id.Clock)));
                    }

                    str.Delete(transaction);
                }
            }
        }
    }

    /// <summary>One struct as both sides are compared: a list position, or a map entry's chain (Key set).</summary>
    private readonly record struct Entry(string Key, long Client, long Clock, int Length, bool Deleted);

    // The root's list, its map entries' chains (key order), its length and map count, and each client's clock order.
    private static List<Entry> Shape(YDoc doc, Recording r)
    {
        doc.TryGetRoot(r.Root, out var type);
        var list = new List<Entry>();
        for (var s = type._start; !s.IsNull; s = s.AsItem.Right)
        {
            list.Add(new(null, s.Id.Client, s.Id.Clock, s.Length, s.Deleted));
        }

        foreach (var (key, held) in type._map.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            for (var s = type.MapItem(held); !s.IsNull; s = s.AsItem.Left)
            {
                list.Add(new(key, s.Id.Client, s.Id.Clock, s.Length, s.Deleted));
            }
        }

        list.Add(new("#", type.Length, type._mapCount, 0, false));
        foreach (var (client, structs) in doc.Store.Clients.OrderBy(kv => kv.Key))
        {
            list.AddRange(structs.Select(s => new Entry("@", client, s.Id.Clock, s.Length, s.Deleted)));
        }

        return list;
    }

    private static List<Entry> Shape(LayoutStore store, Recording r)
    {
        var type = Builder.Root(store, r.Root);
        var list = new List<Entry>();
        Entry Of(string key, int h)
        {
            ref var s = ref store.Slots[h];
            return new(key, store.ClientIds[s.Client], s.Clock, s.Length, s.Deleted || s.Kind == ContentKind.Gc);
        }

        for (var h = type.Start; h >= 0; h = store.Slots[h].Right)
        {
            list.Add(Of(null, h));
        }

        foreach (var (key, last) in (type.Map ?? []).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            for (var h = last; h >= 0; h = store.Slots[h].Left)
            {
                list.Add(Of(key, h));
            }
        }

        list.Add(new("#", type.Length, type.MapCount, 0, false));
        foreach (var c in Enumerable.Range(0, store.ClientIds.Length).OrderBy(c => store.ClientIds[c]))
        {
            var order = store.Orders[c];
            for (var i = 0; i < order.Count; i++)
            {
                list.Add(Of("@", order.Handles[i]) with { Key = "@" });
            }
        }

        return list;
    }

    private static string Content(YDoc doc, string root, char kind) => kind switch
    {
        't' => doc.GetText(root).ToString(),
        'm' => Canonical.Map(doc.GetMap(root).Select(kv => (kv.Key, Convert.ToInt64(kv.Value)))),
        _ => Canonical.Array(doc.GetArray(root).Select(v => v.GetInt64())),
    };

    private static string Content(LayoutStore store, Recording r) => r.RootKind switch
    {
        't' => Builder.Text(store, r.Root),
        'm' => Canonical.Map(Builder.Map(store, r.Root).Select(kv => (kv.Key, Convert.ToInt64(kv.Value)))),
        _ => Canonical.Array(Builder.Array(store, r.Root).Select(Convert.ToInt64)),
    };

    private static string Ns(double ns) => ns >= 1e6 ? $"{ns / 1e6:F2} ms" : ns >= 1e3 ? $"{ns / 1e3:F2} µs" : $"{ns:F1} ns";
}
