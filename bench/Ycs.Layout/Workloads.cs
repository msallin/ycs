using Ycs.Compare;

namespace Ycs.Layout;

/// <summary>
/// A workload of the comparison: <see cref="Build"/> repeats the setup and body of the <see cref="YcsCases"/> method of
/// the same name, keeping the documents; <see cref="Reference"/> runs that method itself for the v1 state to compare.
/// </summary>
internal sealed record Workload(string Name, Action Warm, Func<YDoc[]> Build, Func<byte[]> Reference, Func<YDoc, LayoutStore, bool> Same);

internal static class Workloads
{
    private static YDoc NewDoc(long id = Inputs.ClientId) => new(new YDocOptions { ClientId = id });

    private static byte[] Run(Prepared p)
    {
        p.Body();
        return p.StateV1();
    }

    private static bool SameText(YDoc doc, LayoutStore store) => doc.GetText("text").ToString() == Builder.Text(store, "text");

    private static bool SameArray(YDoc doc, LayoutStore store) => doc.GetArray("array").SequenceEqual(Builder.Array(store, "array"));

    private static bool SameMap(YDoc doc, LayoutStore store)
    {
        var a = Builder.Map(store, "map");
        var map = doc.GetMap("map");
        return a.Count == map.Count && map.All(kv => a.TryGetValue(kv.Key, out var v) && Equals(v, kv.Value));
    }

    public static IEnumerable<Workload> All(Fixtures f)
    {
        yield return new("text-random-100k", () => _ = Inputs.RandomTyping, () =>
        {
            var doc = NewDoc();
            var text = doc.GetText("text");
            foreach (var e in Inputs.RandomTyping)
            {
                text.Insert(e.Pos, e.Insert);
            }

            return [doc];
        }, () => Run(YcsCases.TextRandom(f, 100_000)), SameText);

        yield return new("trace-replay", () => _ = f.Trace, () => [TraceDoc(f)], () => Run(YcsCases.TraceReplay(f)), SameText);

        yield return new("concurrent-20", () => _ = Inputs.ClientEdits, () => Concurrent(), () => Run(YcsCases.Concurrent(f)), SameText);

        yield return new("map-set-distinct", () => _ = Inputs.Keys, () =>
        {
            var doc = NewDoc();
            var map = doc.GetMap("map");
            var keys = Inputs.Keys;
            for (var i = 0; i < keys.Length; i++)
            {
                map.Set(keys[i], (long)i);
            }

            return [doc];
        }, () => Run(YcsCases.MapSetDistinct(f, false)), SameMap);

        yield return new("array-insert-random", () => _ = Inputs.ArrayInserts, () =>
        {
            var doc = NewDoc();
            var array = doc.GetArray("array");
            var positions = Inputs.ArrayInserts;
            for (var i = 0; i < positions.Length; i++)
            {
                array.Insert(positions[i], [(long)i]);
            }

            return [doc];
        }, () => Run(YcsCases.ArrayInsertRandom(f)), SameArray);

        // About a minute (the marker walk this prototype measures); its reference would take another, so the check
        // against the comparison's document runs only with LAYOUT_FULL_CHECK=1.
        yield return new("array-delete-random", () => _ = Inputs.ArrayDeletes, () => [DeleteDoc()],
            Environment.GetEnvironmentVariable("LAYOUT_FULL_CHECK") == "1" ? () => Run(YcsCases.ArrayDeleteRandom(f)) : null, SameArray);
    }

    public static YDoc TraceDoc(Fixtures f)
    {
        var trace = f.Trace;
        var doc = NewDoc();
        var text = doc.GetText("text");
        for (var t = 0; t < trace.Transactions; t++)
        {
            var (from, to) = (trace.TxnStarts[t], trace.TxnStarts[t + 1]);
            doc.Transact(_ =>
            {
                for (var i = from; i < to; i++)
                {
                    var e = trace.Edits[i];
                    if (e.Delete > 0)
                    {
                        text.Delete(e.Pos, e.Delete);
                    }

                    if (e.Insert.Length > 0)
                    {
                        text.Insert(e.Pos, e.Insert);
                    }
                }
            });
        }

        return doc;
    }

    public static YDoc DeleteDoc()
    {
        var doc = NewDoc();
        var array = doc.GetArray("array");
        array.Add([.. Enumerable.Range(0, Inputs.ArrayN).Select(i => (object)(long)i)]);
        foreach (var p in Inputs.ArrayDeletes)
        {
            array.Delete(p, 1);
        }

        return doc;
    }

    public static YDoc[] Concurrent()
    {
        var baseDoc = NewDoc(100);
        baseDoc.GetText("text").Insert(0, Inputs.BaseText);
        var baseUpdate = baseDoc.EncodeStateAsUpdateV1();
        var clients = new YDoc[Inputs.Clients];
        var texts = new YText[Inputs.Clients];
        for (var c = 0; c < clients.Length; c++)
        {
            clients[c] = NewDoc(c + 1);
            texts[c] = clients[c].GetText("text");
            clients[c].ApplyUpdate(baseUpdate);
        }

        var baseVector = clients[0].EncodeStateVector();
        for (var c = 0; c < clients.Length; c++)
        {
            foreach (var e in Inputs.ClientEdits[c])
            {
                if (e.Delete > 0)
                {
                    texts[c].Delete(e.Pos, e.Delete);
                }
                else
                {
                    texts[c].Insert(e.Pos, e.Insert);
                }
            }
        }

        var diffs = new byte[clients.Length][];
        for (var c = 0; c < clients.Length; c++)
        {
            diffs[c] = clients[c].EncodeStateAsUpdateV1(baseVector);
        }

        for (var i = 0; i < clients.Length; i++)
        {
            for (var j = 0; j < clients.Length; j++)
            {
                if (i != j)
                {
                    clients[i].ApplyUpdate(diffs[j]);
                }
            }
        }

        return clients;
    }
}
