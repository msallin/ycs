using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Documents written by Yjs v14 (@y/y 14.0.0-rc.30; Fixtures/v14-interop.json, interop/v14/gen.mjs): root and
/// nested nodes, attributes, formatted text, list content, deletions, and client ids above 2^32 and at 2^53 - 1.
/// Ycs reads them as Yjs 13.6.32 does (the recorded <c>v13</c> view), re-encodes them byte for byte, and encodes
/// state vectors, snapshots, relative positions, diffs and merges as v14 does. What Ycs answers is fed back into
/// v14 by interop/v14/roundtrip.sh (<see cref="Export_WhenRequested_WritesYcsAnswers"/>).
/// </summary>
[TestClass]
public class V14InteropTests
{
    private const long MaxClient = (1L << 53) - 1;

    private static readonly Lazy<JsonElement> Fixture = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "v14-interop.json"))).RootElement);

    private static IEnumerable<JsonElement> Cases => Fixture.Value.GetProperty("cases").EnumerateArray();

    private static JsonElement Case(string name) => Cases.Single(c => c.GetProperty("name").GetString() == name);

    public static IEnumerable<object[]> CaseNames => Cases.Select(c => new object[] { c.GetProperty("name").GetString() });

    public static IEnumerable<object[]> CaseNamesAndFormats =>
        CaseNames.SelectMany(c => new[] { new object[] { c[0], "v1" }, new object[] { c[0], "v2" } });

    // Ycs' own client: 53 bits, and just above 32; neither is a client of the fixtures.
    public static IEnumerable<object[]> CaseNamesAndOwnClients =>
        CaseNames.SelectMany(c => new[] { new object[] { c[0], MaxClient - 3 }, new object[] { c[0], (1L << 32) + 9 } });

    private static byte[] Bytes(JsonElement e, string property) => Convert.FromBase64String(e.GetProperty(property).GetString());

    private static YDoc Read(JsonElement c, string format, YDocOptions options = null)
    {
        var doc = new YDoc(options ?? new YDocOptions());
        if (format == "v1")
        {
            doc.ApplyUpdate(Bytes(c, "updateV1"));
        }
        else
        {
            doc.ApplyUpdateV2(Bytes(c, "updateV2"));
        }

        return doc;
    }

    private static List<(string Name, string Kind)> Roots(JsonElement c) =>
        c.GetProperty("roots").EnumerateArray().Select(r => (r.GetProperty("name").GetString(), r.GetProperty("kind").GetString())).ToList();

    [TestMethod]
    [DynamicData(nameof(CaseNamesAndFormats))]
    public void Update_ReadByYcs_ContentAsYjs13ReadsIt(string name, string format)
    {
        var c = Case(name);

        var doc = Read(c, format);

        Assert.AreEqual(Canonical(c.GetProperty("v13")), View(doc, Roots(c)));
    }

    [TestMethod]
    [DynamicData(nameof(CaseNamesAndFormats))]
    public void Update_ReencodedByYcs_IsByteIdentical(string name, string format)
    {
        var c = Case(name);
        var doc = Read(c, format);

        var reencoded = format == "v1" ? doc.EncodeStateAsUpdate() : doc.EncodeStateAsUpdateV2();

        // Yjs 13 re-encodes it byte for byte too (recorded); Ycs must as well.
        var expected = format == "v1" ? "updateV1" : "updateV2";
        Assert.AreEqual(
            $"{c.GetProperty("yjs13Reencodes").GetProperty(format).GetBoolean()} {c.GetProperty(expected).GetString()}",
            $"True {Convert.ToBase64String(reencoded)}");
    }

    [TestMethod]
    [DynamicData(nameof(CaseNames))]
    public void StateVector_OfTheDocument_AsV14WritesItWithTheSameClients(string name)
    {
        var c = Case(name);
        var doc = Read(c, "v1");

        var decoded = string.Join(",", doc.Store.GetStateVector().OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}:{kv.Value}"));

        var expected = string.Join(",", c.GetProperty("stateVectorDecoded").EnumerateObject().Select(p => $"{p.Name}:{p.Value.GetInt64()}"));
        Assert.AreEqual($"{c.GetProperty("stateVector").GetString()} {expected}", $"{Convert.ToBase64String(doc.EncodeStateVector())} {decoded}");
    }

    [TestMethod]
    [DynamicData(nameof(CaseNames))]
    public void Snapshot_OfTheDocumentAndV14sReencoded_AreV14sBytes(string name)
    {
        var c = Case(name);
        var doc = Read(c, "v1");
        var v1 = Bytes(c, "snapshotV1");
        var v2 = Bytes(c, "snapshotV2");

        var actual = new[]
        {
            doc.CreateSnapshot().EncodeSnapshot(),
            doc.CreateSnapshot().EncodeSnapshotV2(),
            Snapshot.DecodeSnapshot(v1).EncodeSnapshot(),
            Snapshot.DecodeSnapshotV2(v2).EncodeSnapshotV2(),
        };

        Assert.AreEqual(
            string.Join(" ", new[] { v1, v2, v1, v2 }.Select(Convert.ToBase64String)),
            string.Join(" ", actual.Select(Convert.ToBase64String)));
    }

    [TestMethod]
    [DynamicData(nameof(CaseNames))]
    public void RelativePositions_OfV14_ResolveAndReencodeAsV14s(string name)
    {
        var c = Case(name);
        var doc = Read(c, "v1");
        var kinds = Roots(c).ToDictionary(r => r.Name, r => r.Kind);
        var expected = new List<string>();
        var actual = new List<string>();

        foreach (var p in c.GetProperty("positions").EnumerateArray())
        {
            var root = p.GetProperty("root").GetString();
            var index = p.GetProperty("index").GetInt32();
            var assoc = p.GetProperty("assoc").GetInt32();
            var encoded = p.GetProperty("encoded").GetString();
            var rpos = RelativePosition.Decode(Convert.FromBase64String(encoded));
            var own = RelativePosition.FromIndex(RootOf(doc, root, kinds[root]), index, assoc);
            var resolved = rpos.TryResolve(doc, out var absolute) ? absolute.Index : (int?)null;

            expected.Add($"{root}@{index},{assoc}: {encoded} {encoded} {p.GetProperty("resolved").GetInt32()}");
            actual.Add($"{root}@{index},{assoc}: {Convert.ToBase64String(rpos.Encode())} " +
                $"{Convert.ToBase64String(own.Encode())} {resolved}");
        }

        Assert.AreEqual(string.Join("\n", expected), string.Join("\n", actual));
    }

    [TestMethod]
    [DynamicData(nameof(CaseNames))]
    public void DiffAndMerge_OfV14sUpdates_AreV14sBytes(string name)
    {
        var c = Case(name);
        var diff = c.GetProperty("diff");
        var sv = Bytes(diff, "stateVector");
        var updates = c.GetProperty("updatesV1").EnumerateArray().Select(u => Convert.FromBase64String(u.GetString())).ToList();
        var doc = Read(c, "v1");

        var actual = new[]
        {
            UpdateOperations.DiffUpdate(Bytes(c, "updateV1"), sv),
            UpdateOperations.DiffUpdateV2(Bytes(c, "updateV2"), sv),
            doc.EncodeStateAsUpdate(sv),
            UpdateOperations.MergeUpdates(updates),
            UpdateOperations.MergeUpdatesV2(updates.Select(u => UpdateOperations.ConvertUpdateFormatV1ToV2(u)).ToList()),
        };

        var merged = c.GetProperty("merged");
        Assert.AreEqual(
            string.Join(" ", diff.GetProperty("v1").GetString(), diff.GetProperty("v2").GetString(), diff.GetProperty("fromDoc").GetString(),
                merged.GetProperty("v1").GetString(), merged.GetProperty("v2").GetString()),
            string.Join(" ", actual.Select(Convert.ToBase64String)));
    }

    [TestMethod]
    [DynamicData(nameof(CaseNamesAndOwnClients))]
    public void Peer_WithOwnLargeClientId_EditsAndTakesV14sConcurrentChange(string name, long clientId)
    {
        var c = Case(name);

        var (doc, edits) = Answer(c, clientId);

        // Its client id is its own (no clash, so not replaced), its edits are in its state vector, and a
        // fresh document that gets v14's state, v14's change and Ycs' edits converges with it.
        var fresh = new YDoc();
        fresh.ApplyUpdate(Bytes(c, "updateV1"));
        fresh.ApplyUpdate(Bytes(c.GetProperty("concurrent"), "v1"));
        foreach (var edit in edits)
        {
            fresh.ApplyUpdate(edit);
        }

        var sv = doc.Store.GetStateVector();
        Assert.AreEqual(
            $"client: {clientId}, own clock > 0: True, converged: True, same content: True",
            $"client: {doc.ClientId}, own clock > 0: {sv.TryGetValue(clientId, out var clock) && clock > 0}, " +
            $"converged: {fresh.EncodeStateVector().AsSpan().SequenceEqual(doc.EncodeStateVector())}, " +
            $"same content: {View(fresh, Roots(c)) == View(doc, Roots(c))}");
    }

    /// <summary>
    /// Ycs' answer to a case: a document with client id <paramref name="clientId"/> that read v14's update,
    /// edited every root through the type it arrived as, and then applied v14's concurrent change; and the
    /// updates its edits produced.
    /// </summary>
    private static (YDoc Doc, List<byte[]> Edits) Answer(JsonElement c, long clientId)
    {
        var doc = Read(c, "v1", new YDocOptions { ClientId = clientId });
        var edits = new List<byte[]>();
        doc.Updated += (_, e) => edits.Add(e.Update.ToArray());
        foreach (var (name, kind) in Roots(c))
        {
            switch (kind)
            {
                case "text":
                    var text = doc.GetText(name);
                    text.Insert(0, "ycs ", new Dictionary<string, object> { ["italic"] = true });
                    text.Format(4, 2, new Dictionary<string, object> { ["bold"] = null, ["code"] = true });
                    text.Delete(text.Length - 1, 1);
                    break;
                case "map":
                    var map = doc.GetMap(name);
                    map.Set("ycs", "c#");
                    map.Set("max", MaxClient);
                    map.Delete(map.Keys().First(k => k != "ycs" && k != "max"));
                    break;
                case "array":
                    var array = doc.GetArray(name);
                    array.Insert(1, new List<object> { "ycs", 2.5, new Dictionary<string, object> { ["k"] = new List<object> { 1L } } });
                    array.Delete(0, 1);
                    break;
                case "xml":
                    var xml = doc.GetXmlFragment(name);
                    var element = new YXmlElement("ycs");
                    xml.Insert(0, new List<object> { element });
                    element.SetAttribute("from", "c#");
                    element.Insert(0, new List<object> { new YXmlText("text") });
                    var nested = (YXmlElement)xml.Get(1);
                    nested.SetAttribute("class", "changed");
                    nested.Insert(0, new List<object> { new YXmlElement("em") });
                    xml.Delete(xml.Length - 1, 1);
                    break;
            }
        }

        doc.ApplyUpdate(Bytes(c.GetProperty("concurrent"), "v1"));
        return (doc, edits);
    }

    [TestMethod]
    public void Export_WhenRequested_WritesYcsAnswers()
    {
        var dir = Environment.GetEnvironmentVariable("YCS_V14_EXPORT");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        foreach (var c in Cases)
        {
            foreach (var clientId in new[] { MaxClient - 3, (1L << 32) + 9 })
            {
                var (doc, edits) = Answer(c, clientId);
                var concurrentSv = Bytes(c.GetProperty("concurrent"), "stateVector");
                var content = new Dictionary<string, object>
                {
                    ["case"] = c.GetProperty("name").GetString(),
                    ["clientId"] = clientId,
                    ["edits"] = edits.Select(Convert.ToBase64String).ToList(),
                    ["diffV1"] = Convert.ToBase64String(doc.EncodeStateAsUpdate(concurrentSv)),
                    ["diffV2"] = Convert.ToBase64String(doc.EncodeStateAsUpdateV2(concurrentSv)),
                    ["stateV1"] = Convert.ToBase64String(doc.EncodeStateAsUpdate()),
                    ["stateV2"] = Convert.ToBase64String(doc.EncodeStateAsUpdateV2()),
                    ["stateVector"] = Convert.ToBase64String(doc.EncodeStateVector()),
                    ["snapshotV1"] = Convert.ToBase64String(doc.CreateSnapshot().EncodeSnapshot()),
                    ["view"] = View(doc, Roots(c)),
                };
                File.WriteAllText(Path.Combine(dir, $"{c.GetProperty("name").GetString()}-{clientId}.json"), YJson.Stringify(content));
            }
        }
    }

    private static YType RootOf(YDoc doc, string name, string kind) => kind switch
    {
        "text" => doc.GetText(name),
        "map" => doc.GetMap(name),
        "array" => doc.GetArray(name),
        "xml" => doc.GetXmlFragment(name),
        _ => throw new ArgumentException(kind),
    };

    /// <summary>The roots in interop/v14/lib.mjs' canonical form, as JSON.</summary>
    internal static string View(YDoc doc, IEnumerable<(string Name, string Kind)> roots)
    {
        var view = new Dictionary<string, object>();
        foreach (var (name, kind) in roots)
        {
            var type = RootOf(doc, name, kind);
            var root = (Dictionary<string, object>)Node(type);
            switch (type)
            {
                case YText text:
                    root["length"] = text.Length;
                    break;
                case YArrayBase list:
                    root["length"] = list.Length;
                    break;
            }

            if (kind == "xml")
            {
                root["string"] = doc.GetXmlFragment(name).ToString();
            }

            view[name] = root;
        }

        return YJson.Stringify(Sorted(view));
    }

    private static string Canonical(JsonElement e) => YJson.Stringify(Sorted(YJson.Parse(e.GetRawText())));

    // Objects with their keys sorted (lib.mjs sorts the keys of values and attributes; the rest are written in this order).
    private static object Sorted(object v) => v switch
    {
        IDictionary<string, object> d => d.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => Sorted(kv.Value)),
        IList l => l.Cast<object>().Select(Sorted).ToList(),
        _ => v,
    };

    private static object Value(object v) => v switch
    {
        byte[] b => new Dictionary<string, object> { ["$b"] = Convert.ToBase64String(b) },
        YType t => Node(t),
        IDictionary<string, object> d => d.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => Value(kv.Value)),
        IList l => l.Cast<object>().Select(Value).ToList(),
        _ => v,
    };

    private static List<object> Delta(YText text) => text.ToDelta().Select(d =>
    {
        var op = new Dictionary<string, object> { ["insert"] = Value(d.Insert) };
        if (d.Attributes?.Count > 0)
        {
            op["attributes"] = Value(d.Attributes);
        }

        return (object)op;
    }).ToList();

    private static object Node(YType t)
    {
        var node = t switch
        {
            YXmlElement e => new Dictionary<string, object> { ["$xml"] = e.NodeName, ["attrs"] = Value(e.GetAttributes()), ["children"] = Value(e.ToArray()) },
            YXmlText x => new Dictionary<string, object> { ["$xmltext"] = Delta(x) },
            YXmlFragment f => new Dictionary<string, object> { ["$fragment"] = Value(f.ToArray()) },
            YText x => new Dictionary<string, object> { ["$text"] = Delta(x) },
            YMap m => new Dictionary<string, object> { ["$map"] = Value(m.Entries().ToDictionary(kv => kv.Key, kv => kv.Value)) },
            YArray a => new Dictionary<string, object> { ["$array"] = Value(a.ToArray()) },
            _ => throw new ArgumentException(t.GetType().Name),
        };
        return Sorted(node);
    }
}
