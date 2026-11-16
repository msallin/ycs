using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Concurrent edits of nested maps and arrays, differential against real Yjs 13.6.32: the steps and
/// what Yjs showed after each are pinned in Fixtures/event-traces.json.gz, written by
/// interop/corpus/events.mjs. After every step the document's JSON, the size of every type, the v1
/// state bytes and the log of observer calls (an observer on every type, a deep observer on every
/// third, registered in the order of their paths) must equal Yjs'.
/// </summary>
[TestClass]
public class EventTraceTests
{
    private static readonly Lazy<JsonDocument> Fixture = new(() =>
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "event-traces.json.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return JsonDocument.Parse(gzip);
    });

    private static readonly JsonSerializerOptions StringOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static IEnumerable<object[]> Traces => Enumerable.Range(0, Fixture.Value.RootElement.GetArrayLength()).Select(i => new object[] { i });

    [TestMethod]
    [DynamicData(nameof(Traces))]
    public void Replay_Trace_EqualsYjsAfterEveryStep(int index)
    {
        var trace = Fixture.Value.RootElement[index];
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var log = new List<object>();
        var seen = new HashSet<YType>();
        var nextId = 0;

        void Register()
        {
            foreach (var (type, _) in Walk(doc).Where(x => !seen.Contains(x.Type)).OrderBy(x => x.Path, StringComparer.Ordinal).ToList())
            {
                var id = nextId++;
                seen.Add(type);
                type.ObserveAny(e => log.Add(new List<object> { "obs", id, Describe(e) }));
                if (id % 3 == 0)
                {
                    type.ObserveDeep((deepEvents, _) => log.Add(new List<object> { "deep", id, deepEvents.Select(Describe).ToList() }));
                }
            }
        }

        Register();
        var stepIndex = 0;
        foreach (var step in trace.GetProperty("steps").EnumerateArray())
        {
            stepIndex++;
            log.Clear();
            Apply(doc, step);
            var expect = step.GetProperty("expect");
            var at = $"trace {index}, step {stepIndex}";

            Assert.AreEqual(Canon(Convert(expect.GetProperty("m"))), Canon(doc.GetMap("m").ToJson()), $"{at}: map");
            Assert.AreEqual(Canon(Convert(expect.GetProperty("a"))), Canon(doc.GetArray("a").ToJson()), $"{at}: array");
            Assert.AreEqual(
                Canon(Convert(expect.GetProperty("sizes"))),
                Canon(Walk(doc).Select(w => (object)(w.Type is YMap m ? m.Count : w.Type.Length)).ToList()),
                $"{at}: sizes");
            Assert.AreEqual(expect.GetProperty("state").GetString(), System.Convert.ToHexString(doc.EncodeStateAsUpdate()).ToLowerInvariant(), $"{at}: state");
            Assert.AreEqual(
                string.Join("\n", expect.GetProperty("log").EnumerateArray().Select(e => Canon(Convert(e)))),
                string.Join("\n", log.Select(Canon)),
                $"{at}: observer calls");
            Register();
        }
    }

    private static void Apply(YDoc doc, JsonElement step)
    {
        if (step.GetProperty("kind").GetString() == "apply")
        {
            doc.ApplyUpdate(System.Convert.FromHexString(step.GetProperty("update").GetString()));
            return;
        }

        doc.Transact(_ =>
        {
            foreach (var op in step.GetProperty("ops").EnumerateArray())
            {
                var type = Resolve(doc, op[1]);
                switch (op[0].GetString())
                {
                    case "mset":
                        ((YMap)type).Set(op[2].GetString(), Value(op[3]));
                        break;
                    case "mdel":
                        ((YMap)type).Delete(op[2].GetString());
                        break;
                    case "ains":
                        ((YArray)type).Insert(op[2].GetInt32(), op[3].EnumerateArray().Select(Value).ToList());
                        break;
                    case "adel":
                        ((YArray)type).Delete(op[2].GetInt32(), op[3].GetInt32());
                        break;
                    default:
                        throw new InvalidOperationException(op[0].GetString());
                }
            }
        });
    }

    private static object Convert(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt32(out var i) ? i : e.GetDouble(),
        JsonValueKind.Array => e.EnumerateArray().Select(Convert).ToList(),
        _ => e.EnumerateObject().ToDictionary(p => p.Name, p => Convert(p.Value)),
    };

    private static object Value(JsonElement v)
    {
        if (v.TryGetProperty("p", out var p))
        {
            return Convert(p);
        }

        var hasInit = v.TryGetProperty("init", out var init);
        if (v.GetProperty("t").GetString() == "map")
        {
            return hasInit ? new YMap((IDictionary<string, object>)Convert(init)) : new YMap();
        }

        return hasInit ? YArray.From((List<object>)Convert(init)) : new YArray();
    }

    private static YType Resolve(YDoc doc, JsonElement path)
    {
        YType type = path[0].GetString() == "m" ? doc.GetMap("m") : doc.GetArray("a");
        for (var i = 1; i < path.GetArrayLength(); i++)
        {
            type = (YType)(type is YMap m ? m.Get(path[i].GetString()) : ((YArray)type).Get(path[i].GetInt32()));
        }

        return type;
    }

    private static List<(YType Type, string Path)> Walk(YDoc doc)
    {
        var result = new List<(YType, string)>();
        Walk(doc.GetMap("m"), new List<object> { "m" }, result);
        Walk(doc.GetArray("a"), new List<object> { "a" }, result);
        return result;
    }

    private static void Walk(YType type, List<object> path, List<(YType, string)> result)
    {
        result.Add((type, JsJson(path)));
        if (type is YMap map)
        {
            foreach (var kv in map)
            {
                if (kv.Value is YType child)
                {
                    path.Add(kv.Key);
                    Walk(child, path, result);
                    path.RemoveAt(path.Count - 1);
                }
            }
        }
        else
        {
            var i = 0;
            foreach (var v in ((YArray)type).ToArray())
            {
                if (v is YType child)
                {
                    path.Add(i);
                    Walk(child, path, result);
                    path.RemoveAt(path.Count - 1);
                }

                i++;
            }
        }
    }

    // JSON.stringify of a path, as the generator sorts by it.
    private static string JsJson(List<object> path) =>
        "[" + string.Join(",", path.Select(p => p is string s ? "\"" + s + "\"" : ((int)p).ToString(CultureInfo.InvariantCulture))) + "]";

    private static object Describe(YEvent e) => new Dictionary<string, object>
    {
        ["path"] = e.Path.Select(p => p.Key ?? (object)p.Index).ToList(),
        ["keys"] = e.Keys.Select(kv => (object)new List<object> { kv.Key, kv.Value.Action.ToString().ToLowerInvariant(), kv.Value.OldValue }).ToList(),
        ["delta"] = e.Changes.Delta,
        ["added"] = e.Changes.Added.Count,
        ["deleted"] = e.Changes.Deleted.Count,
    };

    // Canonical JSON: object keys sorted, integral numbers without a fraction, types as "<type>".
    private static string Canon(object value)
    {
        var sb = new StringBuilder();
        Write(sb, value);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, object value)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                break;
            case string s:
                sb.Append(JsonSerializer.Serialize(s, StringOptions));
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case YType:
                sb.Append("\"<type>\"");
                break;
            case Delta d:
                Write(sb, d.ToPlain());
                break;
            case IDictionary<string, object> map:
                sb.Append('{');
                var first = true;
                foreach (var key in map.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }

                    first = false;
                    sb.Append(JsonSerializer.Serialize(key, StringOptions)).Append(':');
                    Write(sb, map[key]);
                }

                sb.Append('}');
                break;
            case IEnumerable list:
                sb.Append('[');
                var firstItem = true;
                foreach (var item in list)
                {
                    if (!firstItem)
                    {
                        sb.Append(',');
                    }

                    firstItem = false;
                    Write(sb, item);
                }

                sb.Append(']');
                break;
            case double dd:
                sb.Append(dd == Math.Floor(dd) && Math.Abs(dd) < 1e15 ? ((long)dd).ToString(CultureInfo.InvariantCulture) : dd.ToString("R", CultureInfo.InvariantCulture));
                break;
            case IFormattable f:
                sb.Append(f.ToString(null, CultureInfo.InvariantCulture));
                break;
            default:
                throw new InvalidOperationException(value.GetType().Name);
        }
    }
}
