using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Scripts in which Yjs' search markers are stale when an index edit looks them up (an update applied inside a local
/// transaction; an observer editing a list during a remote transaction's cleanup), run in Yjs 13.6.32 by
/// interop/markers/gen.mjs: the content after every step and the final state must be Yjs'. Where a stale marker sits
/// decides where an edit lands, so this holds only while Ycs keeps every marker where Yjs has it.
/// </summary>
/// <remarks>
/// Fixtures/stale-markers.jsonl.gz holds seeds 1 to 300 of each mode and every script a refused version of the run
/// extension failed (171 of 3,000 "apply", 547 of 20,000 "observer", 1,211 of 60,000 "small"). With
/// <c>YCS_STALE_MARKER_SCRIPTS</c> set to a file the generator wrote, that file is replayed instead
/// (interop/markers/soak.sh). Each script runs three times: with the marker walks as by default (the run-head index
/// after 32 steps), asking the index at once (<c>eager</c>), and item by item as Yjs (<c>plain</c>).
/// </remarks>
[TestClass]
public class StaleMarkerScriptTests
{
    [TestMethod]
    [DataRow("default")]
    [DataRow("eager")]
    [DataRow("plain")]
    public void Scripts_ContentAndStateEqualYjs(string walk)
    {
        var failures = new List<string>();
        var total = 0;

        foreach (var line in Lines())
        {
            if (!line.StartsWith('{'))
            {
                continue;
            }

            total++;
            using var json = JsonDocument.Parse(line);
            if (Replay(json.RootElement, walk) is { } problem && failures.Count < 6)
            {
                failures.Add(problem);
            }
        }

        Assert.AreEqual(0, failures.Count, $"{total} scripts:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    private static IEnumerable<string> Lines()
    {
        var path = Environment.GetEnvironmentVariable("YCS_STALE_MARKER_SCRIPTS");
        if (!string.IsNullOrEmpty(path))
        {
            return File.ReadLines(path);
        }

        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "stale-markers.jsonl.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    /// <summary>What differs from Yjs in the script, or null.</summary>
    private static string Replay(JsonElement script, string walk)
    {
        var doc = new YDoc(new YDocOptions { ClientId = script.GetProperty("client").GetInt64() });
        var peer = new YDoc(new YDocOptions { ClientId = 2 });
        foreach (var d in new[] { doc, peer })
        {
            d.LiteralWalkStepsForTests = walk == "eager" ? 1 : d.LiteralWalkStepsForTests;
            d.RunHeadIndexDisabledForTests = walk == "plain";
        }

        var text = script.GetProperty("kind").GetString() == "text";
        YType Root(YDoc d) => text ? d.GetText("t") : d.GetArray("t");
        var t = Root(doc);
        var pt = Root(peer);
        if (script.GetProperty("obs").GetBoolean())
        {
            t.ObserveAny(_ => { });
        }

        JsonElement? armed = null;
        doc.GetMap("m").Observe(_ =>
        {
            if (armed is JsonElement ops)
            {
                armed = null;
                foreach (var op in ops.EnumerateArray())
                {
                    Run(op);
                }
            }
        });

        var steps = script.GetProperty("steps");
        var expected = script.GetProperty("out");
        var name = $"seed {script.GetProperty("seed")} ({script.GetProperty("mode")}, {(text ? "text" : "array")}, observed {script.GetProperty("obs")})";
        for (var i = 0; i < steps.GetArrayLength(); i++)
        {
            var step = steps[i];
            string error = null;
            try
            {
                switch (step[0].GetString())
                {
                    case "cid":
                        doc.ClientId = 7;
                        break;
                    case "applyobs":
                        armed = step[1];
                        doc.ApplyUpdateV1(peer.EncodeStateAsUpdateV1(doc.EncodeStateVectorV1()));
                        break;
                    case "pm":
                        peer.GetMap("m").Set("k", 1);
                        break;
                    case "sync":
                        peer.ApplyUpdateV1(doc.EncodeStateAsUpdateV1(peer.EncodeStateVectorV1()));
                        break;
                    case "txn":
                        doc.Transact(_ =>
                        {
                            foreach (var op in step[1].EnumerateArray())
                            {
                                Run(op);
                            }
                        });
                        break;
                    default:
                        Run(step);
                        break;
                }
            }
            catch (Exception e)
            {
                error = e.Message;
            }

            var yjs = expected[i];
            var yjsThrew = yjs.GetProperty("err").ValueKind != JsonValueKind.Null;
            if (yjsThrew != (error != null))
            {
                return $"{name}, step {i} {step.GetRawText()}: Yjs {(yjsThrew ? "threw " + yjs.GetProperty("err").GetString() : "did not throw")}, Ycs {error ?? "did not throw"}";
            }

            if (yjs.GetProperty("c").GetString() != Content(t))
            {
                return $"{name}, step {i} {step.GetRawText()}: Yjs {yjs.GetProperty("c").GetString()}, Ycs {Content(t)}";
            }
        }

        return script.GetProperty("v1").GetString() == Convert.ToHexString(doc.EncodeStateAsUpdateV1()) ? null : $"{name}: the final state differs";

        void Run(JsonElement op)
        {
            if (op[0].GetString() == "apply")
            {
                doc.ApplyUpdateV1(peer.EncodeStateAsUpdateV1(doc.EncodeStateVectorV1()));
                return;
            }

            var type = op[1].GetInt32() == 0 ? t : pt;
            var at = op[2].GetInt32();
            switch (op[0].GetString(), type)
            {
                case ("ins", YText yt):
                    yt.Insert(at, op[3].GetString());
                    break;
                case ("ins", YArray ya):
                    ya.Insert(at, new object[] { op[3].GetInt32() });
                    break;
                case (_, YText yt):
                    yt.Remove(at, op[3].GetInt32());
                    break;
                case (_, YArray ya):
                    ya.Delete(at, op[3].GetInt32());
                    break;
            }
        }
    }

    // As the generator renders it: a text's string, an array as JSON of its integers.
    private static string Content(YType type) => type is YText text
        ? text.ToString()
        : "[" + string.Join(",", ((YArray)type).ToArray().Select(v => Convert.ToString(v, CultureInfo.InvariantCulture))) + "]";
}
