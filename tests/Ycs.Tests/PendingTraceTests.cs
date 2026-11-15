using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Out-of-order delivery, differential against real Yjs 13.6.32: every transaction's update of three
/// clients delivered to one receiver in a shuffled order, pinned in Fixtures/pending-traces.json.gz
/// (written by interop/corpus/pending.mjs). After every delivery the text, the map and the v1 and v2
/// encodings of the state (which include what is pending) must equal Yjs'.
/// </summary>
[TestClass]
public class PendingTraceTests
{
    private static readonly Lazy<JsonDocument> Fixture = new(() =>
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pending-traces.json.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return JsonDocument.Parse(gzip);
    });

    public static IEnumerable<object[]> Traces => Enumerable.Range(0, Fixture.Value.RootElement.GetArrayLength()).Select(i => new object[] { i });

    [TestMethod]
    [DynamicData(nameof(Traces))]
    public void Replay_ShuffledDelivery_EqualsYjsAfterEveryUpdate(int index)
    {
        var doc = new YDoc(new YDocOptions { ClientId = 99 });
        var step = 0;
        foreach (var s in Fixture.Value.RootElement[index].GetProperty("steps").EnumerateArray())
        {
            step++;
            doc.ApplyUpdate(Convert.FromHexString(s.GetProperty("update").GetString()));
            var at = $"trace {index}, step {step}";

            Assert.AreEqual(s.GetProperty("text").GetString(), doc.GetText("t").ToString(), $"{at}: text");
            Assert.AreEqual(Canonical(s.GetProperty("map").GetString()), Canonical(YJson.Stringify(doc.GetMap("m").ToJson())), $"{at}: map");
            Assert.AreEqual(s.GetProperty("v1").GetString(), Convert.ToHexString(doc.EncodeStateAsUpdate()).ToLowerInvariant(), $"{at}: v1");
            Assert.AreEqual(s.GetProperty("v2").GetString(), Convert.ToHexString(doc.EncodeStateAsUpdateV2()).ToLowerInvariant(), $"{at}: v2");
        }
    }

    // Object keys sorted: Yjs and Ycs list a map's keys in different orders.
    private static string Canonical(string json) => Write(JsonDocument.Parse(json).RootElement);

    private static string Write(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => YJson.Stringify(p.Name) + ":" + Write(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", e.EnumerateArray().Select(Write)) + "]",
        _ => e.GetRawText(),
    };
}
