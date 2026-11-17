using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Complete sets of updates complete in every order. Yjs 13.6.33 leaves some orders partly pending (yjs/yjs#824;
/// interop/yjs-pending/README.md): a struct that waits for a client whose structs are in the same update records
/// nothing, so its dependency arriving retries nothing. Ycs records that client's state, as Yjs does when the
/// walk stops there. Fixtures written by interop/yjs-pending/gen.mjs.
/// </summary>
[TestClass]
public class PendingRetryTests
{
    private static readonly Lazy<JsonElement> Scenarios = new(() =>
        JsonElement.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pending-retry.json"))));

    private static readonly Lazy<JsonElement> Random = new(() =>
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pending-retry-random.json.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return JsonDocument.Parse(gzip).RootElement;
    });

    public static IEnumerable<object[]> Sessions => Enumerable.Range(0, Random.Value.GetArrayLength()).Select(i => new object[] { i });

    [TestMethod]
    [DataRow("minimal", "stuckOrder")]
    [DataRow("minimal", "creationOrder")]
    [DataRow("repro", "stuckOrder")]
    [DataRow("repro", "creationOrder")]
    public void Apply_CompleteSetInOrder_EqualsTheCreationOrder(string scenario, string order)
    {
        var s = Scenarios.Value.GetProperty(scenario);

        var doc = Apply(Updates(s), s.GetProperty(order).EnumerateArray().Select(i => i.GetInt32()));

        Assert.AreEqual(Expected(s.GetProperty("creation")), Observe(doc), $"{scenario}, {order}");
    }

    [TestMethod]
    [DynamicData(nameof(Sessions))]
    public void Apply_RandomSessionInRandomOrders_EqualsTheFullState(int index)
    {
        var session = Random.Value[index];
        var updates = session.GetProperty("updates").EnumerateArray().Select(u => Convert.FromBase64String(u.GetString())).ToList();
        var full = session.GetProperty("stateV2").GetString();

        var mismatches = session.GetProperty("runs").EnumerateArray()
            .Select(run => (Order: run.GetProperty("order").GetString(), Doc: Apply(updates, run.GetProperty("order").GetString().Split(',').Select(int.Parse))))
            .Where(r => (false, full) != (r.Doc.HasPending, Convert.ToBase64String(r.Doc.EncodeStateAsUpdate(format: UpdateFormat.V2))))
            .Select(r => r.Order)
            .ToList();

        Assert.AreEqual(string.Empty, string.Join(" | ", mismatches), $"session {index}");
    }

    private static List<byte[]> Updates(JsonElement scenario) =>
        scenario.GetProperty("updates").EnumerateArray().Select(u => Convert.FromBase64String(u.GetString())).ToList();

    private static YDoc Apply(IReadOnlyList<byte[]> updates, IEnumerable<int> order)
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1_000_000 });
        foreach (var i in order)
        {
            doc.ApplyUpdate(updates[i]);
        }

        return doc;
    }

    // Yjs' pendingStructs and pendingDs are both "pending" here: Ycs reports them together.
    private static (bool Pending, string StateVector, string StateV2, string Text, string Array, string Map) Expected(JsonElement e) =>
        (e.GetProperty("pendingStructs").GetBoolean() || e.GetProperty("pendingDs").GetBoolean(), e.GetProperty("stateVector").GetString(),
            e.GetProperty("stateV2").GetString(), e.GetProperty("text").GetString(), Canonical(e.GetProperty("array").GetString()), Canonical(e.GetProperty("map").GetString()));

    private static (bool Pending, string StateVector, string StateV2, string Text, string Array, string Map) Observe(YDoc doc) =>
        (doc.HasPending, Convert.ToBase64String(doc.EncodeStateVector()), Convert.ToBase64String(doc.EncodeStateAsUpdate(format: UpdateFormat.V2)), doc.GetText("text").ToString(),
            Canonical(doc.GetArray("array").ToJsonString()), Canonical(doc.GetMap("map").ToJsonString()));

    // Object keys sorted: Yjs and Ycs list a map's keys in different orders.
    private static string Canonical(string json) => Write(JsonElement.Parse(json));

    private static string Write(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => YJson.Stringify(p.Name) + ":" + Write(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", e.EnumerateArray().Select(Write)) + "]",
        _ => e.GetRawText(),
    };
}
