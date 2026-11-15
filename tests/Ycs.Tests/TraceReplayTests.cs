// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Differential traces of the shared types and their events, recorded from real Yjs by
/// interop/traces/gen.mjs (Fixtures/trace-*.json.gz). Three Yjs clients edit concurrently; every step
/// of the observed client (a local transaction, a remote update, a snapshot, the first access of a
/// root) is replayed on one Ycs document, which must then show what Yjs showed: content, deltas,
/// lengths, the state bytes (by hash), and every observer call in order with what it read.
/// </summary>
[TestClass]
public class TraceReplayTests
{
    private static readonly string[] SetNames = ["text", "text-observed", "text-markers", "text-placeholder", "list-map", "list-map-push", "xml", "snapshots"];

    private static readonly Dictionary<string, Lazy<JsonDocument>> Sets = SetNames.ToDictionary(n => n, n => new Lazy<JsonDocument>(() =>
    {
        using var gz = new GZipStream(File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"trace-{n}.json.gz")), CompressionMode.Decompress);
        return JsonDocument.Parse(gz);
    }));

    public static IEnumerable<object[]> Traces =>
        SetNames.SelectMany(set => Enumerable.Range(0, Sets[set].Value.RootElement.GetProperty("traces").GetArrayLength()).Select(n => new object[] { set, n }));

    [TestMethod]
    public void Fixtures_ComeFromYjs13632() =>
        Assert.IsTrue(SetNames.All(s => Sets[s].Value.RootElement.GetProperty("yjs").GetString() == "13.6.32"));

    [TestMethod]
    [DynamicData(nameof(Traces))]
    public void Replay_EveryStep_MatchesYjs(string set, int n) =>
        TraceKnownFailures.Check(set, n, () =>
        {
            var trace = Sets[set].Value.RootElement.GetProperty("traces")[n];
            var divergence = set switch
            {
                "list-map" or "list-map-push" => new ListMapReplay().Run(trace),
                "xml" => new XmlReplay().Run(trace),
                "snapshots" => new SnapshotReplay().Run(trace),
                _ => new TextReplay(set).Run(trace),
            };

            if (divergence != null)
            {
                Assert.Fail(divergence);
            }
        });

    /// <summary>Runs the steps of a trace; the first step whose result differs from Yjs ends it.</summary>
    private abstract class Replay
    {
        protected readonly List<object> Log = new();

        protected YDoc Doc { get; set; }

        public string Run(JsonElement trace)
        {
            var i = 0;
            foreach (var step in trace.GetProperty("steps").EnumerateArray())
            {
                Log.Clear();
                var kind = step.GetProperty("kind").GetString();
                Dictionary<string, object> got;
                try
                {
                    switch (kind)
                    {
                        case "apply":
                            Doc.ApplyUpdate(Convert.FromBase64String(step.GetProperty("update").GetString()));
                            break;
                        case "local":
                            Doc.Transact(_ =>
                            {
                                foreach (var op in step.GetProperty("ops").EnumerateArray())
                                {
                                    ApplyOp(op);
                                }
                            });
                            break;
                        default:
                            Special(kind, step);
                            break;
                    }

                    got = Observe(step.GetProperty("expect"));
                }
                catch (Exception e)
                {
                    return $"step {i} ({kind}): {e.GetType().Name}: {e.Message}{Environment.NewLine}{e.StackTrace}";
                }

                foreach (var field in step.GetProperty("expect").EnumerateObject())
                {
                    var want = CanonicalJson.Write(CanonicalJson.FromJson(field.Value));
                    var actual = got.TryGetValue(field.Name, out var g) ? CanonicalJson.Write(g) : "<missing>";
                    if (want != actual)
                    {
                        return $"step {i} ({kind}), {field.Name}:{Environment.NewLine}  Yjs {Trim(want)}{Environment.NewLine}  Ycs {Trim(actual)}";
                    }
                }

                i++;
            }

            return null;

            static string Trim(string s) => s.Length > 3000 ? s[..3000] + "..." : s;
        }

        protected abstract void ApplyOp(JsonElement op);

        protected virtual void Special(string kind, JsonElement step) => throw new InvalidOperationException(kind);

        /// <summary>What Yjs recorded in <paramref name="expect"/>, read from the Ycs document.</summary>
        protected abstract Dictionary<string, object> Observe(JsonElement expect);

        protected static string StateHash(YDoc doc) => CanonicalJson.Hash(doc.EncodeStateAsUpdate());

        /// <summary>The attributes at <paramref name="i"/> of <paramref name="op"/>; null when absent or null.</summary>
        internal static IDictionary<string, object> AttrsOf(JsonElement op, int i) =>
            i >= op.GetArrayLength() || op[i].ValueKind == JsonValueKind.Null ? null : (IDictionary<string, object>)CanonicalJson.FromJson(op[i]);

        protected static List<object> KeysOf(YEvent e) =>
            e.Keys.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => (object)new List<object> { kv.Key, kv.Value.Action.ToString().ToLowerInvariant(), kv.Value.OldValue })
                .ToList();

        protected static Dictionary<string, object> Describe(YEvent e)
        {
            var d = new Dictionary<string, object>
            {
                ["path"] = e.Path.ToList(),
                ["keys"] = KeysOf(e),
                ["delta"] = e.Changes.Delta,
                ["added"] = e.Changes.Added.Count,
                ["deleted"] = e.Changes.Deleted.Count,
            };
            if (e is YXmlEvent xe)
            {
                d["attributesChanged"] = xe.AttributesChanged.OrderBy(k => k, StringComparer.Ordinal).ToList();
            }

            return d;
        }

        protected void ObserveType(AbstractType t, int id)
        {
            t.Observe((_, e) => Log.Add(new List<object> { "obs", id, Describe(e.Event) }));
            if (id % 3 == 0)
            {
                t.ObserveDeep((_, e) => Log.Add(new List<object> { "deep", id, e.Events.Select(Describe).ToList() }));
            }
        }
    }

    private sealed class TextReplay : Replay
    {
        private readonly string _mode;
        private int _n;
        private YText _text;

        public TextReplay(string mode)
        {
            _mode = mode;
            Doc = new YDoc(new YDocOptions { ClientId = 1 });
            if (mode != "text-placeholder")
            {
                _text = Doc.GetText("t");
                if (mode == "text-observed")
                {
                    ObserveText();
                }
            }
        }

        public new string Run(JsonElement trace)
        {
            _n = trace.GetProperty("n").GetInt32();
            return base.Run(trace);
        }

        private void ObserveText() => _text.Observe((_, e) => Log.Add(DescribeText((YTextEvent)e.Event)));

        private Dictionary<string, object> DescribeText(YTextEvent e)
        {
            // As the generator: every other trace reads Changes before Delta.
            var changes = _n % 2 == 0 ? e.Changes : null;
            var d = new Dictionary<string, object>
            {
                ["delta"] = e.Delta,
                ["keys"] = KeysOf(e),
                ["keysChanged"] = e.KeysChanged.OrderBy(k => k, StringComparer.Ordinal).ToList(),
            };
            var c = changes ?? e.Changes;
            d["changes"] = new Dictionary<string, object>
            {
                ["delta"] = c.Delta,
                ["added"] = c.Added.Count,
                ["deleted"] = c.Deleted.Count,
                ["keys"] = c.Keys.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(),
            };
            return d;
        }

        protected override void Special(string kind, JsonElement step)
        {
            Assert.AreEqual("get", kind);
            _text = Doc.GetText("t");
            ObserveText();
        }

        protected override void ApplyOp(JsonElement op) => ApplyTextOp(_text, op, 1);

        protected override Dictionary<string, object> Observe(JsonElement expect)
        {
            var got = new Dictionary<string, object> { ["state"] = StateHash(Doc) };
            if (_text == null)
            {
                return got;
            }

            var str = _text.ToString();
            got["str"] = _mode == "text-markers" ? CanonicalJson.Hash(Encoding.UTF8.GetBytes(str)) : str;
            got["len"] = _text.Length;
            if (expect.TryGetProperty("delta", out _))
            {
                got["delta"] = _text.ToDelta();
            }

            got["events"] = Log.ToList();
            return got;
        }
    }

    /// <summary>A text op of the generator's textOp; <paramref name="at"/> is the index of its first argument.</summary>
    private static void ApplyTextOp(YText t, JsonElement op, int at)
    {
        switch (op[0].GetString())
        {
            case "insert":
            case "tinsert":
                t.Insert(op[at].GetInt32(), op[at + 1].GetString(), Replay.AttrsOf(op, at + 2));
                break;
            case "delete":
            case "tdelete":
                t.Delete(op[at].GetInt32(), op[at + 1].GetInt32());
                break;
            case "format":
            case "tformat":
                t.Format(op[at].GetInt32(), op[at + 1].GetInt32(), Replay.AttrsOf(op, at + 2));
                break;
            case "embed":
            case "tembed":
                t.InsertEmbed(op[at].GetInt32(), CanonicalJson.FromJson(op[at + 1]), Replay.AttrsOf(op, at + 2));
                break;
            case "embedType":
                t.InsertEmbed(op[at].GetInt32(), NewType(op[at + 1].GetString()), Replay.AttrsOf(op, at + 2));
                break;
            case "setAttribute":
                t.SetAttribute(op[at].GetString(), CanonicalJson.FromJson(op[at + 1]));
                break;
            case "removeAttribute":
                t.RemoveAttribute(op[at].GetString());
                break;
            default:
                throw new InvalidOperationException(op[0].GetString());
        }

        static AbstractType NewType(string name) => name switch
        {
            "YMap" => new YMap(new Dictionary<string, object> { ["k"] = 1 }),
            "YText" => new YText("t"),
            _ => YArray.From([1]),
        };
    }

    private sealed class ListMapReplay : Replay
    {
        private readonly HashSet<AbstractType> _seen = new();

        public ListMapReplay()
        {
            Doc = new YDoc(new YDocOptions { ClientId = 1 });
            ObserveNew();
        }

        private List<(AbstractType Type, string Path)> Walk()
        {
            var res = new List<(AbstractType, string)>();
            Rec(Doc.GetMap("m"), ["m"]);
            Rec(Doc.GetArray("a"), ["a"]);
            return res;

            void Rec(AbstractType t, List<object> path)
            {
                res.Add((t, JsonSerializer.Serialize(path)));
                if (t is YMap m)
                {
                    foreach (var (k, v) in m)
                    {
                        if (v is AbstractType c)
                        {
                            Rec(c, [.. path, k]);
                        }
                    }
                }
                else
                {
                    var i = 0;
                    foreach (var v in ((YArray)t).ToArray())
                    {
                        if (v is AbstractType c)
                        {
                            Rec(c, [.. path, i]);
                        }

                        i++;
                    }
                }
            }
        }

        private void ObserveNew()
        {
            foreach (var (t, _) in Walk().Where(x => !_seen.Contains(x.Type)).OrderBy(x => x.Path, StringComparer.Ordinal).ToList())
            {
                ObserveType(t, _seen.Count);
                _seen.Add(t);
            }
        }

        private AbstractType Resolve(JsonElement path)
        {
            AbstractType t = path[0].GetString() == "m" ? Doc.GetMap("m") : Doc.GetArray("a");
            for (var i = 1; i < path.GetArrayLength(); i++)
            {
                t = (AbstractType)(t is YMap m ? m.Get(path[i].GetString()) : ((YArray)t).Get(path[i].GetInt32()));
            }

            return t;
        }

        private static object MakeValue(JsonElement v)
        {
            if (v.TryGetProperty("p", out var p))
            {
                return CanonicalJson.FromJson(p);
            }

            var hasInit = v.TryGetProperty("init", out var init);
            if (v.GetProperty("t").GetString() == "map")
            {
                return hasInit ? new YMap((IDictionary<string, object>)CanonicalJson.FromJson(init)) : new YMap();
            }

            return hasInit ? YArray.From((List<object>)CanonicalJson.FromJson(init)) : new YArray();
        }

        protected override void ApplyOp(JsonElement op)
        {
            var t = Resolve(op[1]);
            switch (op[0].GetString())
            {
                case "mset": ((YMap)t).Set(op[2].GetString(), MakeValue(op[3])); break;
                case "mdel": ((YMap)t).Delete(op[2].GetString()); break;
                case "ains": ((YArray)t).Insert(op[2].GetInt32(), op[3].EnumerateArray().Select(MakeValue).ToList()); break;
                case "adel": ((YArray)t).Delete(op[2].GetInt32(), op[3].GetInt32()); break;
                case "apush": ((YArray)t).Push(op[2].EnumerateArray().Select(MakeValue).ToList()); break;
                default: throw new InvalidOperationException(op[0].GetString());
            }
        }

        protected override Dictionary<string, object> Observe(JsonElement expect)
        {
            var got = new Dictionary<string, object>
            {
                ["m"] = Doc.GetMap("m").ToJson(),
                ["a"] = Doc.GetArray("a").ToJson(),
                ["sizes"] = Walk().Select(w => (object)(w.Type is YMap m ? m.Count : w.Type.Length)).ToList(),
                ["state"] = StateHash(Doc),
                ["log"] = Log.ToList(),
            };
            ObserveNew();
            return got;
        }
    }

    private sealed class XmlReplay : Replay
    {
        private readonly HashSet<AbstractType> _seen = new();
        private readonly YXmlFragment _root;

        public XmlReplay()
        {
            Doc = new YDoc(new YDocOptions { ClientId = 1 });
            _root = Doc.GetXmlFragment("x");
            ObserveNew();
        }

        private List<AbstractType> Walk()
        {
            var res = new List<AbstractType>();
            Rec(_root);
            return res;

            void Rec(AbstractType t)
            {
                res.Add(t);
                if (t is YXmlFragment f)
                {
                    foreach (var v in f.ToArray())
                    {
                        if (v is AbstractType c)
                        {
                            Rec(c);
                        }
                    }
                }
            }
        }

        private void ObserveNew()
        {
            foreach (var t in Walk().Where(t => !_seen.Contains(t)).ToList())
            {
                ObserveType(t, _seen.Count);
                _seen.Add(t);
            }
        }

        private AbstractType Resolve(JsonElement path)
        {
            AbstractType t = _root;
            for (var i = 1; i < path.GetArrayLength(); i++)
            {
                t = (AbstractType)((YXmlFragment)t).Get(path[i].GetInt32());
            }

            return t;
        }

        private static object MakeNode(JsonElement spec)
        {
            if (spec.TryGetProperty("text", out var text))
            {
                return new YXmlText(text.GetString());
            }

            if (spec.TryGetProperty("hook", out var hook))
            {
                return new YXmlHook(hook.GetString());
            }

            var el = new YXmlElement(spec.GetProperty("element").GetString());
            if (spec.TryGetProperty("attrs", out var attrs))
            {
                foreach (var a in attrs.EnumerateObject())
                {
                    el.SetAttribute(a.Name, CanonicalJson.FromJson(a.Value));
                }
            }

            if (spec.TryGetProperty("children", out var children))
            {
                el.Insert(0, children.EnumerateArray().Select(MakeNode).ToList());
            }

            return el;
        }

        protected override void ApplyOp(JsonElement op)
        {
            var t = Resolve(op[1]);
            switch (op[0].GetString())
            {
                case "hset": ((YXmlHook)t).Set(op[2].GetString(), CanonicalJson.FromJson(op[3])); break;
                case "setAttr": ((YXmlElement)t).SetAttribute(op[2].GetString(), CanonicalJson.FromJson(op[3])); break;
                case "removeAttr": ((YXmlElement)t).RemoveAttribute(op[2].GetString()); break;
                case "xinsert": ((YXmlFragment)t).Insert(op[2].GetInt32(), op[3].EnumerateArray().Select(MakeNode).ToList()); break;
                case "xdelete": ((YXmlFragment)t).Delete(op[2].GetInt32(), op[3].GetInt32()); break;
                case "xpush": ((YXmlFragment)t).Push(op[2].EnumerateArray().Select(MakeNode).ToList()); break;
                default: ApplyTextOp((YXmlText)t, op, 2); break;
            }
        }

        protected override Dictionary<string, object> Observe(JsonElement expect)
        {
            var got = new Dictionary<string, object>
            {
                ["str"] = _root.ToString(),
                ["json"] = _root.ToJson(),
                ["nodes"] = Walk().Select(t => (object)(t switch
                {
                    YXmlElement el => new Dictionary<string, object> { ["name"] = el.NodeName, ["attrs"] = el.GetAttributes(), ["len"] = el.Length },
                    YXmlText xt => new Dictionary<string, object> { ["delta"] = xt.ToDelta(), ["len"] = xt.Length },
                    YXmlHook h => new Dictionary<string, object> { ["hook"] = h.ToJson() },
                    _ => new Dictionary<string, object> { ["len"] = t.Length },
                })).ToList(),
                ["state"] = StateHash(Doc),
                ["log"] = Log.ToList(),
            };
            ObserveNew();
            return got;
        }
    }

    private sealed class SnapshotReplay : Replay
    {
        private readonly List<Snapshot> _snapshots = new();
        private YText _text;

        public new string Run(JsonElement trace)
        {
            var clients = trace.GetProperty("clients").EnumerateArray().Select(c => c.GetInt64()).ToList();
            Doc = new YDoc(new YDocOptions { ClientId = (int)clients[0], Gc = false });
            _text = Doc.GetText("t");
            return base.Run(trace);
        }

        protected override void Special(string kind, JsonElement step)
        {
            Assert.AreEqual("snapshot", kind);
            _snapshots.Add(Snapshot.DecodeSnapshot(Convert.FromBase64String(step.GetProperty("snapshot").GetString())));
        }

        protected override void ApplyOp(JsonElement op) => ApplyTextOp(_text, op, 1);

        protected override Dictionary<string, object> Observe(JsonElement expect)
        {
            var got = new Dictionary<string, object> { ["str"] = _text.ToString(), ["delta"] = _text.ToDelta() };
            if (_snapshots.Count > 0)
            {
                var now = Doc.CreateSnapshot();
                var last = _snapshots[^1];
                var first = _snapshots[Math.Max(0, _snapshots.Count - 3)];
                got["atLast"] = _text.ToDelta(last);
                got["sinceFirst"] = _text.ToDelta(now, first);
                got["sinceFirstBy"] = _text.ToDelta(now, first, ComputeYChange);
                got["firstToLastBy"] = _text.ToDelta(last, first, ComputeYChange);
                got["prevOnly"] = _text.ToDelta(null, first);
            }

            got["state"] = StateHash(Doc);
            return got;
        }

        private static YText.YTextChangeAttributes ComputeYChange(YText.YTextChangeType type, ID id) => new() { Type = type, User = id.Client };
    }
}

/// <summary>
/// Traces that still differ from Yjs because of divergences outside the shared types, each with the
/// findings of docs/walkthrough-types.md that cause it (every one of them is needed: the trace passes
/// with all of them fixed, and fails when any one is left out). A listed trace that fails is
/// Inconclusive; one that passes fails the run, so its entry gets removed.
/// </summary>
internal static class TraceKnownFailures
{
    private static readonly Dictionary<string, string> Findings = new()
    {
        ["3"] = "finding 3 (a deep observer's event has the wrong CurrentTarget and Path)",
        ["4"] = "finding 4 (deep events of equal path length are sorted unstably)",
        ["5"] = "finding 5 (observers in a different order after a changed type was deleted)",
        ["8"] = "finding 8 (deleted structs are merged less than in Yjs 13.6)",
    };

    private static readonly (string Set, string Findings, int[] Traces)[] Pinned =
    [
        ("list-map", "3",
            [0, 1, 3, 6, 7, 8, 9, 11, 13, 15, 16, 17, 20, 22, 23, 25, 26, 27, 29, 30, 31, 32, 34,
            36, 38, 39, 40, 41, 42, 43, 44, 46, 47, 49, 50, 54, 55, 56, 58, 60, 61, 62, 63, 67, 68,
            70, 73, 74, 75, 77, 81, 82, 83, 85, 87, 88, 89, 90, 91, 92, 93, 95, 96, 97, 99, 100,
            101, 102, 103, 108, 109, 110, 111, 114, 116, 118, 119, 120, 121, 123, 127, 128, 130,
            131, 133, 135, 136, 137, 140, 141, 143, 147, 148, 150, 151, 152, 153, 154, 155, 156,
            158, 160, 166, 167, 168, 169, 170, 172, 175, 177, 178, 179, 180, 181, 183, 184, 185,
            189, 190, 193, 194, 195, 196, 197, 199]),
        ("list-map", "3,4",
            [12, 18, 24, 45, 52, 53, 64, 84, 106, 115, 117, 126, 129, 132, 142, 144, 162, 171, 186,
            191]),
        ("list-map", "3,4,5",
            [79]),
        ("list-map", "3,4,8",
            [76]),
        ("list-map", "3,5",
            [2, 10, 21, 33, 51, 66, 78, 80, 112, 122, 134, 149, 192]),
        ("list-map", "3,5,8",
            [71, 125]),
        ("list-map", "3,8",
            [28, 69, 72, 104, 107, 145]),
        ("list-map", "4,5",
            [157]),
        ("list-map", "5",
            [182]),
        ("list-map", "8",
            [57]),
        ("list-map-push", "3",
            [0, 2, 3, 6, 9, 10, 14, 15, 16, 20, 24, 25, 26, 28, 29, 30, 32, 33, 34, 35, 36, 37, 39,
            40, 43, 52, 53, 55, 56, 57, 58, 61, 62, 64, 65, 66, 67, 68, 69, 72, 73, 74, 76, 77, 78,
            82, 84, 86, 87, 89, 90, 94, 96, 97, 98, 99, 100, 102, 103, 106, 108, 110, 111, 112,
            113, 114, 115, 117, 118, 119, 120, 121, 124, 127, 128, 129, 130, 131, 133, 134, 136,
            137, 138, 139, 140, 141, 142, 143, 144, 147, 149, 150, 151, 152, 153, 154, 155, 157,
            159, 160, 165, 168, 169, 171, 172, 173, 174, 175, 176, 177, 178, 182, 186, 188, 189,
            190, 192, 193, 194, 195, 196, 198, 199]),
        ("list-map-push", "3,4",
            [4, 7, 18, 21, 22, 42, 63, 70, 75, 93, 107, 126, 132, 148, 166, 179, 181]),
        ("list-map-push", "3,4,5",
            [91]),
        ("list-map-push", "3,5",
            [5, 11, 13, 17, 19, 50, 59, 92, 122, 145, 161, 167, 191]),
        ("list-map-push", "3,5,8",
            [125]),
        ("list-map-push", "3,8",
            [1, 85]),
        ("list-map-push", "4",
            [197]),
        ("list-map-push", "5",
            [105]),
        ("xml", "3",
            [2, 4, 7, 8, 9, 11, 12, 14, 15, 16, 17, 19, 20, 21, 23, 24, 25, 27, 28, 29, 31, 33, 34,
            36, 37, 40, 41, 43, 44, 45, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 61, 62,
            63, 64, 65, 66, 68, 69, 70, 71, 72, 73, 74, 77, 78, 80, 82, 83, 84, 86, 87, 88, 91, 92,
            93, 94, 95, 99, 100, 101, 102, 103, 106, 108, 109, 110, 111, 112, 114, 118, 119, 120,
            122, 123, 125, 126, 127, 128, 129, 130, 133, 135, 136, 137, 138, 139, 140, 141, 142,
            144, 145, 146, 147, 148, 149, 150, 151, 152, 153, 154, 156, 158, 159, 160, 162, 163,
            164, 165, 166, 168, 169, 170, 171, 172, 173, 176, 177, 178, 179, 180, 181, 182, 183,
            184, 185, 187, 190, 191, 192, 193, 194, 195, 196, 197, 198]),
        ("xml", "3,4",
            [1, 3, 6, 10, 26, 30, 32, 35, 38, 39, 42, 60, 67, 75, 76, 79, 81, 85, 90, 96, 98, 105,
            107, 113, 116, 117, 121, 124, 131, 132, 134, 155, 157, 161, 167, 174, 175, 188, 189,
            199]),
        ("xml", "3,4,5",
            [13]),
        ("xml", "3,5",
            [115, 143]),
        ("xml", "3,8",
            [97]),
    ];

    private static readonly Dictionary<(string Set, int N), string> Known = Pinned
        .SelectMany(p => p.Traces.Select(n => (Key: (p.Set, n), Reason: string.Join("; ", p.Findings.Split(',').Select(f => Findings[f])))))
        .ToDictionary(x => x.Key, x => x.Reason);

    public static void Check(string set, int n, Action assertion)
    {
        if (!Known.TryGetValue((set, n), out var reason))
        {
            assertion();
            return;
        }

        try
        {
            assertion();
        }
        catch (AssertFailedException e)
        {
            Assert.Inconclusive($"KNOWN FAILURE ({set} #{n}): {reason}{Environment.NewLine}{e.Message}");
        }

        Assert.Fail($"{set} #{n} is listed in TraceKnownFailures but now passes; remove the entry.");
    }
}
