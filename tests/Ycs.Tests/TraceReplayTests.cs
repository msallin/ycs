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
    public void Replay_EveryStep_MatchesYjs(string set, int n)
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
    }

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
        internal static YAttributes AttrsOf(JsonElement op, int i) =>
            i >= op.GetArrayLength() || op[i].ValueKind == JsonValueKind.Null
                ? null
                : ((IDictionary<string, object>)CanonicalJson.FromJson(op[i])).ToDictionary(kv => kv.Key, kv => YValue.FromObject(kv.Value));

        protected static List<object> KeysOf(YEvent e) =>
            e.Keys.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => (object)new List<object> { kv.Key, kv.Value.Action.ToString().ToLowerInvariant(), kv.Value.OldValue.ToObject() })
                .ToList();

        protected static Dictionary<string, object> Describe(YEvent e)
        {
            var d = new Dictionary<string, object>
            {
                ["path"] = e.Path.Select(p => p.Key ?? (object)p.Index).ToList(),
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

        protected void ObserveType(YType t, int id)
        {
            t.ObserveAny(e => Log.Add(new List<object> { "obs", id, Describe(e) }));
            if (id % 3 == 0)
            {
                t.ObserveDeep((deepEvents, _) => Log.Add(new List<object> { "deep", id, deepEvents.Select(Describe).ToList() }));
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

        private void ObserveText() => _text.Observe(e => Log.Add(DescribeText((YTextEvent)e)));

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
                t.Remove(op[at].GetInt32(), op[at + 1].GetInt32());
                break;
            case "format":
            case "tformat":
                t.Format(op[at].GetInt32(), op[at + 1].GetInt32(), Replay.AttrsOf(op, at + 2));
                break;
            case "embed":
            case "tembed":
                t.InsertEmbed(op[at].GetInt32(), YValue.FromObject(CanonicalJson.FromJson(op[at + 1])), Replay.AttrsOf(op, at + 2));
                break;
            case "embedType":
                t.InsertEmbed(op[at].GetInt32(), NewType(op[at + 1].GetString()), Replay.AttrsOf(op, at + 2));
                break;
            case "setAttribute":
                t.SetAttribute(op[at].GetString(), YValue.FromObject(CanonicalJson.FromJson(op[at + 1])));
                break;
            case "removeAttribute":
                t.RemoveAttribute(op[at].GetString());
                break;
            default:
                throw new InvalidOperationException(op[0].GetString());
        }

        static YType NewType(string name) => name switch
        {
            "YMap" => new YMap(YValue.FromObject(new Dictionary<string, object> { ["k"] = 1 }).EnumerateObject()),
            "YText" => new YText("t"),
            _ => new YArray(1),
        };
    }

    private sealed class ListMapReplay : Replay
    {
        private readonly HashSet<YType> _seen = new();

        public ListMapReplay()
        {
            Doc = new YDoc(new YDocOptions { ClientId = 1 });
            ObserveNew();
        }

        private List<(YType Type, string Path)> Walk()
        {
            var res = new List<(YType, string)>();
            Rec(Doc.GetMap("m"), ["m"]);
            Rec(Doc.GetArray("a"), ["a"]);
            return res;

            void Rec(YType t, List<object> path)
            {
                res.Add((t, JsonSerializer.Serialize(path)));
                if (t is YMap m)
                {
                    foreach (var (k, v) in m)
                    {
                        if (v.TryGetSharedType(out YType c))
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
                        if (v.TryGetSharedType(out YType c))
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

        private YType Resolve(JsonElement path)
        {
            YType t = path[0].GetString() == "m" ? Doc.GetMap("m") : Doc.GetArray("a");
            for (var i = 1; i < path.GetArrayLength(); i++)
            {
                t = (YType)(t is YMap m ? m[path[i].GetString()].ToObject() : ((YArray)t)[path[i].GetInt32()].ToObject());
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
                return hasInit ? new YMap(YValue.FromObject((IDictionary<string, object>)CanonicalJson.FromJson(init)).EnumerateObject()) : new YMap();
            }

            return hasInit ? new YArray(((List<object>)CanonicalJson.FromJson(init)).Select(YValue.FromObject).ToArray()) : new YArray();
        }

        protected override void ApplyOp(JsonElement op)
        {
            var t = Resolve(op[1]);
            switch (op[0].GetString())
            {
                case "mset": ((YMap)t)[op[2].GetString()] = YValue.FromObject(MakeValue(op[3])); break;
                case "mdel": ((YMap)t).Remove(op[2].GetString()); break;
                case "ains": ((YArray)t).Insert(op[2].GetInt32(), op[3].EnumerateArray().Select(MakeValue).ToList().Select(YValue.FromObject).ToArray()); break;
                case "adel": ((YArray)t).RemoveRange(op[2].GetInt32(), op[3].GetInt32()); break;
                case "apush": ((YArray)t).Add(op[2].EnumerateArray().Select(MakeValue).ToList().Select(YValue.FromObject).ToArray()); break;
                default: throw new InvalidOperationException(op[0].GetString());
            }
        }

        protected override Dictionary<string, object> Observe(JsonElement expect)
        {
            var got = new Dictionary<string, object>
            {
                ["m"] = Doc.GetMap("m").ToJsonObject(),
                ["a"] = Doc.GetArray("a").ToJsonObject(),
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
        private readonly HashSet<YType> _seen = new();
        private readonly YXmlFragment _root;

        public XmlReplay()
        {
            Doc = new YDoc(new YDocOptions { ClientId = 1 });
            _root = Doc.GetXmlFragment("x");
            ObserveNew();
        }

        private List<YType> Walk()
        {
            var res = new List<YType>();
            Rec(_root);
            return res;

            void Rec(YType t)
            {
                res.Add(t);
                if (t is YXmlFragment f)
                {
                    foreach (var v in f.ToArray())
                    {
                        if (v is YType c)
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

        private YType Resolve(JsonElement path)
        {
            YType t = _root;
            for (var i = 1; i < path.GetArrayLength(); i++)
            {
                t = (YType)((YXmlFragment)t)[path[i].GetInt32()];
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
                    el.SetAttribute(a.Name, YValue.FromObject(CanonicalJson.FromJson(a.Value)));
                }
            }

            if (spec.TryGetProperty("children", out var children))
            {
                el.Insert(0, children.EnumerateArray().Select(MakeNode).ToList().Cast<YType>().ToArray());
            }

            return el;
        }

        protected override void ApplyOp(JsonElement op)
        {
            var t = Resolve(op[1]);
            switch (op[0].GetString())
            {
                case "hset": ((YXmlHook)t)[op[2].GetString()] = YValue.FromObject(CanonicalJson.FromJson(op[3])); break;
                case "setAttr": ((YXmlElement)t).SetAttribute(op[2].GetString(), YValue.FromObject(CanonicalJson.FromJson(op[3]))); break;
                case "removeAttr": ((YXmlElement)t).RemoveAttribute(op[2].GetString()); break;
                case "xinsert": ((YXmlFragment)t).Insert(op[2].GetInt32(), op[3].EnumerateArray().Select(MakeNode).ToList().Cast<YType>().ToArray()); break;
                case "xdelete": ((YXmlFragment)t).RemoveRange(op[2].GetInt32(), op[3].GetInt32()); break;
                case "xpush": ((YXmlFragment)t).Add(op[2].EnumerateArray().Select(MakeNode).Cast<YType>().ToArray()); break;
                default: ApplyTextOp((YXmlText)t, op, 2); break;
            }
        }

        protected override Dictionary<string, object> Observe(JsonElement expect)
        {
            var got = new Dictionary<string, object>
            {
                ["str"] = _root.ToString(),
                ["json"] = _root.ToJsonObject(),
                ["nodes"] = Walk().Select(t => (object)(t switch
                {
                    YXmlElement el => new Dictionary<string, object> { ["name"] = el.NodeName, ["attrs"] = el.GetAttributes().ToDictionary(kv => kv.Key, kv => kv.Value.ToObject()), ["len"] = el.Length },
                    YXmlText xt => new Dictionary<string, object> { ["delta"] = xt.ToDelta(), ["len"] = xt.Length },
                    YXmlHook h => new Dictionary<string, object> { ["hook"] = h.ToJsonObject() },
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
            Doc = new YDoc(new YDocOptions { ClientId = clients[0], GarbageCollection = false });
            _text = Doc.GetText("t");
            return base.Run(trace);
        }

        protected override void Special(string kind, JsonElement step)
        {
            Assert.AreEqual("snapshot", kind);
            _snapshots.Add(Snapshot.Decode(Convert.FromBase64String(step.GetProperty("snapshot").GetString())));
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

        private static YChange ComputeYChange(YChangeKind kind, ItemId id) => new(kind, id.Client);
    }
}
