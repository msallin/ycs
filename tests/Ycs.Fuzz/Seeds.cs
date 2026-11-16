using System.Text.Json;

namespace Ycs.Fuzz;

/// <summary>
/// A valid update and the document it applies to: the starting point of every mutation.
/// <see cref="BaseState"/> is the v1 state of the document before the update (empty: a fresh document).
/// </summary>
internal sealed record Seed(string Name, bool Gc, bool V2, byte[] BaseState, byte[] Update, IReadOnlyList<string> TextRoots)
{
    public override string ToString() => $"{Name} ({(V2 ? "v2" : "v1")})";
}

/// <summary>
/// The seeds: every update of the recorded Yjs corpus (interop/corpus/cases, applied on top of the
/// updates before it), updates generated here for what the corpus covers thinly, and crafted shapes
/// Yjs never writes. Both formats.
/// </summary>
internal static class Seeds
{
    private static readonly Lazy<IReadOnlyList<Seed>> LazyAll = new(() => [.. Corpus(), .. Generated(), .. Crafted()]);

    public static IReadOnlyList<Seed> All => LazyAll.Value;

    private static IEnumerable<Seed> Corpus()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "cases");
        foreach (var path in Directory.GetFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = json.RootElement;
            var name = root.GetProperty("name").GetString()!;
            var gc = root.GetProperty("gc").GetBoolean();
            var textRoots = root.GetProperty("roots").EnumerateArray()
                .Where(r => r.GetProperty("type").GetString() == "text")
                .Select(r => r.GetProperty("name").GetString()!)
                .ToList();
            var updates = root.GetProperty("updates").EnumerateArray()
                .Select(u => (V1: Convert.FromBase64String(u.GetProperty("v1").GetString()!), V2: Convert.FromBase64String(u.GetProperty("v2").GetString()!)))
                .ToList();

            var doc = new YDoc(new YDocOptions { Gc = gc, ClientId = 1 });
            for (var i = 0; i < updates.Count; i++)
            {
                var baseState = i == 0 ? [] : doc.EncodeStateAsUpdateV1();
                yield return new Seed($"corpus/{name}#{i}", gc, false, baseState, updates[i].V1, textRoots);
                yield return new Seed($"corpus/{name}#{i}", gc, true, baseState, updates[i].V2, textRoots);
                doc.ApplyUpdateV1(updates[i].V1);
            }
        }
    }

    private static IEnumerable<Seed> Generated()
    {
        foreach (var gc in new[] { true, false })
        {
            foreach (var (name, roots, edits) in Scenarios())
            {
                var a = new YDoc(new YDocOptions { Gc = gc, ClientId = 10 });
                var b = new YDoc(new YDocOptions { Gc = gc, ClientId = 20 });
                var recorded = new List<(byte[] V1, byte[] V2)>();
                byte[]? v1 = null;
                a.Updated += (_, e) => v1 = e.Update.ToArray();
                a.UpdatedV2 += (_, e) => recorded.Add((v1!, e.Update.ToArray()));
                b.Updated += (_, e) => a.ApplyUpdateV1(e.Update.ToArray(), "b");
                edits(a, b);

                // One seed per incremental update, and one for the whole document as a single update.
                var replica = new YDoc(new YDocOptions { Gc = gc, ClientId = 1 });
                for (var i = 0; i < recorded.Count; i++)
                {
                    var baseState = i == 0 ? [] : replica.EncodeStateAsUpdateV1();
                    var seedName = $"gen/{name}{(gc ? "" : "-nogc")}#{i}";
                    yield return new Seed(seedName, gc, false, baseState, recorded[i].V1, roots);
                    yield return new Seed(seedName, gc, true, baseState, recorded[i].V2, roots);
                    replica.ApplyUpdateV1(recorded[i].V1);
                }

                yield return new Seed($"gen/{name}{(gc ? "" : "-nogc")}#all", gc, false, [], a.EncodeStateAsUpdateV1(), roots);
                yield return new Seed($"gen/{name}{(gc ? "" : "-nogc")}#all", gc, true, [], a.EncodeStateAsUpdateV2(), roots);
            }
        }
    }

    /// <summary>
    /// A nested map's key with a value of three items, then an update that splits it with a deleted range and deletes
    /// the map (the property engine's gc-cleanup-throws-on-crafted-update). With garbage collection the v1 one is
    /// rejected (the collection would meet a second live item of the key), so it is a seed only without; its v2
    /// conversion writes the map's deletion first, which deletes the value before the range could split it.
    /// </summary>
    private static IEnumerable<Seed> Crafted()
    {
        var value = Convert.FromBase64String("AwKsAgAIAQFhA353AXN9AQQBAXQD5LitAvj6//8HAIesAgIBhKwCAwJiYwPwogQAh6wCAAEoAPCiBAABbQF9RygA+Pr//wcAAWwDfUd4eAA=");
        var splitAndDelete = Convert.FromBase64String("AQUKACgA+Pr//wcAAWsCeH0BxPj6//8HAfj6//8HAQJiYygA8KIEAAFsAnh9R4fwogQAAQoCA/CiBAEDAfj6//8HAQADCgIBAwQC");
        foreach (var gc in new[] { true, false })
        {
            var name = $"crafted/map-value-split{(gc ? "" : "-nogc")}";
            yield return new Seed(name + "#0", gc, false, [], value, ["t"]);
            yield return new Seed(name + "#0", gc, true, [], UpdateOperations.ConvertUpdateFormatV1ToV2(value), ["t"]);
            if (!gc)
            {
                yield return new Seed(name + "#1", gc, false, value, splitAndDelete, ["t"]);
            }

            yield return new Seed(name + "#1", gc, true, value, UpdateOperations.ConvertUpdateFormatV1ToV2(splitAndDelete), ["t"]);
        }
    }

    private static IEnumerable<(string Name, IReadOnlyList<string> TextRoots, Action<YDoc, YDoc> Edits)> Scenarios()
    {
        yield return ("text", ["t"], TextEdits);
        yield return ("map", [], MapEdits);
        yield return ("array", [], ArrayEdits);
        yield return ("xml", [], XmlEdits);
        yield return ("subdoc", [], SubdocEdits);
    }

    private static void TextEdits(YDoc a, YDoc b)
    {
        var t = a.GetText("t");
        t.Insert(0, "hello world");
        t.Insert(5, ",");
        t.Remove(0, 2);
        t.Format(1, 4, new Dictionary<string, YValue> { ["bold"] = true });
        t.InsertEmbed(3, YValue.Object(new KeyValuePair<string, YValue>("image", "x.png")));
        b.ApplyUpdateV1(a.EncodeStateAsUpdateV1());
        b.GetText("t").Insert(2, "\u00fc\U0001F600");
        t.Insert(1, "abc");
        t.Remove(4, 3);
    }

    private static void MapEdits(YDoc a, YDoc b)
    {
        var m = a.GetMap("m");
        m.Set("s", "str");
        m.Set("i", 42);
        m.Set("l", 1L << 40);
        m.Set("d", 0.5);
        m.Set("b", true);
        m.Set("n", null!);
        m.Set("bin", new byte[] { 1, 2, 255 });
        m.Set("list", new List<object> { 1, "two", new Dictionary<string, object> { ["three"] = 3 } });
        m.Set("nested", new YMap(new Dictionary<string, object> { ["x"] = 1 }));
        m.Set("s", "overwritten");
        m.Delete("i");
        b.ApplyUpdateV1(a.EncodeStateAsUpdateV1());
        b.GetMap("m").Set("from-b", "b");
    }

    private static void ArrayEdits(YDoc a, YDoc b)
    {
        var arr = a.GetArray("a");
        arr.Insert(0, new object[] { 1, 2, 3, "four" });
        arr.Insert(2, new object[] { new YArray(new object[] { "inner" }), new YText("text inside") });
        arr.Delete(1, 2);
        b.ApplyUpdateV1(a.EncodeStateAsUpdateV1());
        b.GetArray("a").Insert(0, new object[] { true });
        arr.Add(new object[] { new byte[] { 9 } });
    }

    private static void XmlEdits(YDoc a, YDoc b)
    {
        var f = a.GetXmlFragment("x");
        var p = new YXmlElement("p");
        f.Insert(0, new object[] { p, new YXmlText("tail") });
        p.SetAttribute("class", "c");
        p.Insert(0, new object[] { new YXmlText("inside"), new YXmlElement("br") });
        b.ApplyUpdateV1(a.EncodeStateAsUpdateV1());
        ((YXmlElement)b.GetXmlFragment("x").Get(0)).RemoveAttribute("class");
        f.Delete(1);
    }

    private static void SubdocEdits(YDoc a, YDoc b)
    {
        var m = a.GetMap("docs");
        m.Set("sub", new YDoc(new YDocOptions { Guid = "sub-guid" }));
        b.ApplyUpdateV1(a.EncodeStateAsUpdateV1());

        // Not m.Delete("sub"): with Gc = false the destroyed subdocument's item keeps a ContentDoc
        // whose Doc is null, and encoding the document throws NullReferenceException.
        b.GetMap("docs").Set("sub2", new YDoc(new YDocOptions { Guid = "sub-guid-2", AutoLoad = true }));
    }
}
