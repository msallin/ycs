using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Complete sets of updates that Yjs 13.6.33 leaves partly pending in some orders: a pending struct records only
/// what the last struct of its dependency walk waits for, so when its own dependency arrives nothing retries it, and
/// the update that fills the other gap waits on it in turn (interop/yjs-pending/README.md). Fixtures written by
/// interop/yjs-pending/gen.mjs.
/// </summary>
[TestClass]
public class PendingRetryParityTests
{
    private static readonly Lazy<JsonElement> Scenarios = new(() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pending-retry.json"))).RootElement);

    private static readonly Lazy<JsonElement> Random = new(() =>
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pending-retry-random.json.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return JsonDocument.Parse(gzip).RootElement;
    });

    public static IEnumerable<object[]> Sessions => Enumerable.Range(0, Random.Value.GetArrayLength()).Select(i => new object[] { i });

    [TestMethod]
    [DataRow("minimal", "stuckOrder", "stuck")]
    [DataRow("minimal", "creationOrder", "creation")]
    [DataRow("repro", "stuckOrder", "stuck")]
    [DataRow("repro", "creationOrder", "creation")]
    public void Apply_CompleteSetInOrder_LeavesWhatYjsLeavesPending(string scenario, string order, string expected)
    {
        var s = Scenarios.Value.GetProperty(scenario);

        var doc = Apply(Updates(s), s.GetProperty(order).EnumerateArray().Select(i => i.GetInt32()));

        Assert.AreEqual(Expected(s.GetProperty(expected)), Observe(doc), $"{scenario}, {order}");
    }

    [TestMethod]
    [DataRow("minimal", "empty")]
    [DataRow("minimal", "unrelated")]
    [DataRow("minimal", "pendingUpdateApplied")]
    [DataRow("minimal", "resent")]
    [DataRow("repro", "empty")]
    [DataRow("repro", "unrelated")]
    [DataRow("repro", "pendingUpdateApplied")]
    [DataRow("repro", "resent")]
    public void Apply_UpdateAfterStuckOrder_HealsWhereYjsHeals(string scenario, string healing)
    {
        var s = Scenarios.Value.GetProperty(scenario);
        var updates = Updates(s);
        var doc = Apply(updates, s.GetProperty("stuckOrder").EnumerateArray().Select(i => i.GetInt32()));

        var extra = healing switch
        {
            "empty" => new YDoc().EncodeStateAsUpdate(),
            "unrelated" => Unrelated(),
            "pendingUpdateApplied" => UpdateOperations.ConvertUpdateFormatV2ToV1(Convert.FromBase64String(s.GetProperty("stuck").GetProperty("pendingStructsUpdate").GetString())),
            _ => updates[2],
        };
        doc.ApplyUpdate(extra);

        Assert.AreEqual(Expected(s.GetProperty("healing").GetProperty(healing)), Observe(doc), $"{scenario}, {healing}");
    }

    [TestMethod]
    [DynamicData(nameof(Sessions))]
    public void Apply_RandomSessionInRandomOrders_EqualsYjs(int index)
    {
        var session = Random.Value[index];
        var updates = session.GetProperty("updates").EnumerateArray().Select(u => Convert.FromBase64String(u.GetString())).ToList();
        var full = session.GetProperty("stateV2").GetString();

        var mismatches = session.GetProperty("runs").EnumerateArray()
            .Select(run => (Run: run, Doc: Apply(updates, run.GetProperty("order").GetString().Split(',').Select(int.Parse))))
            .Where(r => (r.Run.GetProperty("stuck").GetBoolean(), r.Run.TryGetProperty("stateV2", out var v) ? v.GetString() : full)
                != (r.Doc.HasPending, Convert.ToBase64String(r.Doc.EncodeStateAsUpdateV2())))
            .Select(r => r.Run.GetProperty("order").GetString())
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

    // The update gen.mjs applies as the unrelated one: client 999 types "u" into the text "unrelated".
    private static byte[] Unrelated()
    {
        var other = new YDoc(new YDocOptions { ClientId = 999 });
        other.GetText("unrelated").Insert(0, "u");
        return other.EncodeStateAsUpdate();
    }

    // Yjs' pendingStructs and pendingDs are both "pending" here: Ycs reports them together.
    private static (bool Pending, string StateVector, string StateV2, string Text, string Array, string Map) Expected(JsonElement e) =>
        (e.GetProperty("pendingStructs").GetBoolean() || e.GetProperty("pendingDs").GetBoolean(), e.GetProperty("stateVector").GetString(),
            e.GetProperty("stateV2").GetString(), e.GetProperty("text").GetString(), Canonical(e.GetProperty("array").GetString()), Canonical(e.GetProperty("map").GetString()));

    private static (bool Pending, string StateVector, string StateV2, string Text, string Array, string Map) Observe(YDoc doc) =>
        (doc.HasPending, Convert.ToBase64String(doc.EncodeStateVector()), Convert.ToBase64String(doc.EncodeStateAsUpdateV2()), doc.GetText("text").ToString(),
            Canonical(YJson.Stringify(doc.GetArray("array").ToJson())), Canonical(YJson.Stringify(doc.GetMap("map").ToJson())));

    // Object keys sorted: Yjs and Ycs list a map's keys in different orders.
    private static string Canonical(string json) => Write(JsonDocument.Parse(json).RootElement);

    private static string Write(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => YJson.Stringify(p.Name) + ":" + Write(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", e.EnumerateArray().Select(Write)) + "]",
        _ => e.GetRawText(),
    };
}
