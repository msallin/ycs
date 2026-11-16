// The ONLY file of this project that touches the Ycs API. It is the C# twin of
// interop/properties/oracle.mjs: the same interpreter of the operation language, observing the
// same things, rendered to the same canonical JSON. Everything else in the project speaks plain
// values (Json.cs) and bytes. When the library's API changes, this is the file to edit; when the
// language gains an operation, it is added here and in oracle.mjs (see docs/testing.md).

using System.Collections;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using static Ycs.Properties.Json;

namespace Ycs.Corpus
{
    /// <summary>One struct of a sequence's item list, as tests/Ycs.Corpus/YcsAdapter.cs defines it for ItemListInvariants.</summary>
    internal readonly record struct ItemRun(
        long Client,
        long Clock,
        int Length,
        (long Client, long Clock)? Origin,
        (long Client, long Clock)? RightOrigin,
        bool Deleted,
        string Kind,
        bool LinkedBack,
        bool InParent)
    {
        public const string ItemKind = "Item";
    }
}

namespace Ycs.Properties
{
    using Ycs.Corpus;

    /// <summary>A shared type a script step can target: its path, kind, length (or size) and map keys.</summary>
    internal sealed record Target(List<object> Path, string Kind, int Length, IReadOnlyList<string> Keys);

    /// <summary>One execution of a script against Ycs, step by step (the generator drives it incrementally).</summary>
    internal sealed class ScriptRun
    {
        internal const string Net = "net";
        private const int InfTimeout = int.MaxValue;

        internal readonly Dictionary<string, object> Script;
        internal readonly List<(string Name, string Kind)> Roots;
        internal readonly bool Compact;
        internal readonly List<Client> Clients;
        internal readonly Dictionary<string, byte[]> RelativePositions = new();
        internal readonly Dictionary<UndoManager, string> UndoIds = new(ReferenceEqualityComparer.Instance);
        internal List<object> Events = new();
        private int _stepIndex;

        public ScriptRun(Dictionary<string, object> script)
        {
            Script = script;
            Roots = script.List("roots").Cast<Dictionary<string, object>>().Select(r => (r.Str("name"), r.Str("kind"))).ToList();
            Compact = script.Bool("compact");
            Clients = script.List("clients").Cast<Dictionary<string, object>>().Select((spec, i) => new Client(this, i, spec)).ToList();
        }

        /// <summary>Every step's observation and the final one, as oracle.mjs' Run.execute().</summary>
        public static List<object> Execute(Dictionary<string, object> script)
        {
            var run = new ScriptRun(script);
            var result = new List<object>();
            foreach (Dictionary<string, object> step in script.List("steps"))
            {
                result.Add(run.Step(step));
            }

            result.Add(run.End());
            Interlocked.Add(ref ExecutedSteps, run._stepIndex);
            Interlocked.Add(ref ExecutedStepsExtending, run._stepsExtending);
            Interlocked.Increment(ref ExecutedScripts);
            if (run._stepsExtending > 0)
            {
                Interlocked.Increment(ref ExecutedScriptsExtending);
            }
            return result;
        }

        /// <summary>The exception the last step that failed threw (the observation records only "err").</summary>
        public Exception LastException { get; private set; }

        /// <summary>Steps and scripts <see cref="Execute"/> ran (all of the process), and of them those in which some client extended a run in place.</summary>
        internal static long ExecutedSteps, ExecutedStepsExtending, ExecutedScripts, ExecutedScriptsExtending;

        private int _stepsExtending;

        private long RunsExtended()
        {
            long n = 0;
            foreach (var c in Clients)
            {
                n += c.Replica.RunsExtended;
            }

            return n;
        }

        private long _renamed;

        /// <summary>
        /// A client whose own clock a non-local transaction moved (local edits in a transaction an update was applied
        /// in) drew a random client id (Yjs' "Changed the client-id"): both interpreters replace it with the next of
        /// 5,000,000,000, 5,000,000,001, ..., in the transaction's update handler (the first thing called after the
        /// change that is not an observer) and before every step (oracle.mjs' fixClientIds).
        /// </summary>
        internal void FixClientIds()
        {
            foreach (var c in Clients)
            {
                if (c.Doc.ClientId != c.Id)
                {
                    c.Id = 5_000_000_000 + _renamed++;
                    Replica.SetClientId(c.Doc, c.Id);
                }
            }
        }

        public Dictionary<string, object> Step(Dictionary<string, object> step)
        {
            FixClientIds();
            Events = new List<object>();
            var obs = Obj(("i", (long)_stepIndex++), ("op", step.Str("op")));
            var extendedBefore = RunsExtended();
            try
            {
                var r = Dispatch(step);
                if (r is not Missing)
                {
                    obs["r"] = r;
                }
            }
            catch (Exception e)
            {
                obs["err"] = true;
                LastException = e;
                Debug(e);
            }

            if (RunsExtended() != extendedBefore)
            {
                _stepsExtending++;
            }

            var touched = step.Has("c") ? new[] { step.Int("c") } : step.Has("to") ? new[] { step.Int("to") } : [];
            obs["ev"] = Events;
            var st = new Dictionary<string, object>();
            foreach (var c in touched)
            {
                if (c >= 0 && c < Clients.Count)
                {
                    st[c.ToString(System.Globalization.CultureInfo.InvariantCulture)] = Clients[(int)c].Replica.State(!Compact);
                }
            }

            obs["st"] = st;
            return obs;
        }

        public Dictionary<string, object> End()
        {
            Events = new List<object>();
            var st = new Dictionary<string, object>();
            for (var i = 0; i < Clients.Count; i++)
            {
                st[i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = Clients[i].Replica.State(true);
            }

            return Obj(("i", "end"), ("st", st));
        }

        /// <summary>YCS_PROP_DEBUG=1 prints the exceptions the operations threw (they are observed only as "err").</summary>
        private static void Debug(Exception e)
        {
            if (Environment.GetEnvironmentVariable("YCS_PROP_DEBUG") is { Length: > 0 })
            {
                Console.WriteLine("EXCEPTION " + e);
            }
        }

        internal object OriginLabel(object o) => o switch
        {
            null => null,
            string s => s,
            UndoManager um when UndoIds.TryGetValue(um, out var id) => "um:" + id,
            _ => "?",
        };

        private Client ClientAt(Dictionary<string, object> s, string key)
        {
            if (!s.Has(key) || s.Get(key) is not JsonNumber and not long)
            {
                return null;
            }

            var i = s.Int(key);
            return i >= 0 && i < Clients.Count ? Clients[(int)i] : null;
        }

        private object Dispatch(Dictionary<string, object> s)
        {
            var c = ClientAt(s, s.Has("c") ? "c" : "to");
            if (c == null)
            {
                return "skip";
            }

            switch (s.Str("op"))
            {
                case "deliver":
                    return Deliver(s, c);
                case "sync":
                    return Sync(s, c);
                case "merged":
                    return Merged(s, c);
                case "raw":
                    return Raw(s, c);
                case "transact":
                    {
                        var results = new List<object>();
                        c.Doc.Transact(_ =>
                        {
                            foreach (Dictionary<string, object> op in s.List("ops"))
                            {
                                try
                                {
                                    // An update applied inside the transaction (to this client).
                                    var r = op.Str("op") == "deliver" ? Deliver(op, c) : c.Local(op);
                                    results.Add(r is Missing ? null : r);
                                }
                                catch (Exception e)
                                {
                                    results.Add(Obj(("err", true)));
                                    Debug(e);
                                }
                            }
                        }, s.Str("origin"));
                        return results;
                    }
                default:
                    return c.Local(s);
            }
        }

        private byte[] UpdateOf(object from, long k, bool v2)
        {
            if (from is not JsonNumber and not long)
            {
                return null;
            }

            var i = from is JsonNumber n ? n.Long : (long)from;
            if (i < 0 || i >= Clients.Count)
            {
                return null;
            }

            var log = Clients[(int)i].Log;
            return k < 0 || k >= log.Count ? null : v2 ? log[(int)k].V2 : log[(int)k].V1;
        }

        private object Deliver(Dictionary<string, object> s, Client to)
        {
            var via = s.Str("via") ?? "v1";
            var u = UpdateOf(s.Get("from"), s.Int("k"), via is "v2" or "v2to1");
            if (u == null)
            {
                return "skip";
            }

            switch (via)
            {
                case "v1":
                    to.Doc.ApplyUpdateV1(u, Net);
                    return Missing.Value;
                case "v2":
                    to.Doc.ApplyUpdateV2(u, Net);
                    return Missing.Value;
                case "v1to2":
                    {
                        var c = UpdateOperations.ConvertUpdateFormatV1ToV2(u);
                        to.Doc.ApplyUpdateV2(c, Net);
                        return Obj(("u", B64(c)));
                    }
                case "v2to1":
                    {
                        var c = UpdateOperations.ConvertUpdateFormatV2ToV1(u);
                        to.Doc.ApplyUpdateV1(c, Net);
                        return Obj(("u", B64(c)));
                    }
                default:
                    throw new ArgumentException($"unknown via {via}");
            }
        }

        private object Sync(Dictionary<string, object> s, Client to)
        {
            var from = ClientAt(s, "from");
            if (from == null)
            {
                return "skip";
            }

            var v2 = s.Str("via") == "v2";
            var m1 = new System.Buffers.ArrayBufferWriter<byte>();
            SyncProtocol.WriteSyncStep1(m1, to.Doc);
            var step1 = m1.WrittenSpan.ToArray();
            byte[] step2;
            if (s.Bool("diff"))
            {
                // Step 2 from DiffUpdate of the whole state instead of EncodeStateAsUpdate(sv), framed by hand.
                var reader = new MemoryStream(step1);
                reader.ReadByte();
                var sv = new byte[ReadVarUint(reader)];
                reader.ReadExactly(sv);
                var update = v2 ? UpdateOperations.DiffUpdateV2(from.Doc.EncodeStateAsUpdateV2(), sv) : UpdateOperations.DiffUpdate(from.Doc.EncodeStateAsUpdateV1(), sv);
                var m2 = new MemoryStream();
                m2.WriteByte(1);
                WriteVarUint(m2, update.Length);
                m2.Write(update);
                step2 = m2.ToArray();
            }
            else
            {
                var m2 = new System.Buffers.ArrayBufferWriter<byte>();
                SyncProtocol.ReadSyncMessage(step1, m2, from.Doc, Net, v2);
                step2 = m2.WrittenSpan.ToArray();
            }

            SyncProtocol.ReadSyncMessage(step2, new System.Buffers.ArrayBufferWriter<byte>(), to.Doc, Net, v2);
            return Obj(("s1", B64(step1)), ("s2", B64(step2)));
        }

        private static int ReadVarUint(Stream stream)
        {
            var num = 0;
            for (var shift = 0; ; shift += 7)
            {
                var b = stream.ReadByte();
                num |= (b & 0x7F) << shift;
                if (b < 0x80)
                {
                    return num;
                }
            }
        }

        private static void WriteVarUint(Stream stream, int num)
        {
            while (num > 0x7F)
            {
                stream.WriteByte((byte)(0x80 | (num & 0x7F)));
                num >>= 7;
            }

            stream.WriteByte((byte)num);
        }

        /// <summary>
        /// Violations of "a rejected update leaves the document as it was, and only YUpdateException rejects one",
        /// seen by raw steps (the structure-aware fuzzer checks this list; it is not part of the observation).
        /// </summary>
        internal readonly List<string> RejectionViolations = new();

        private object Raw(Dictionary<string, object> s, Client to)
        {
            var u = Convert.FromBase64String(s.Str("u"));
            var before = (Canonical(to.Replica.Content()), B64(to.Doc.EncodeStateAsUpdateV1()), B64(to.Doc.EncodeStateVectorV1()));
            try
            {
                switch (s.Str("via") ?? "v1")
                {
                    case "v1":
                        to.Doc.ApplyUpdateV1(u, Net);
                        return Missing.Value;
                    case "v2":
                        to.Doc.ApplyUpdateV2(u, Net);
                        return Missing.Value;
                    case "v1to2":
                        {
                            var c = UpdateOperations.ConvertUpdateFormatV1ToV2(u);
                            to.Doc.ApplyUpdateV2(c, Net);
                            return Obj(("u", B64(c)));
                        }
                    default:
                        throw new ArgumentException($"unknown via {s.Str("via")}");
                }
            }
            catch (Exception e)
            {
                var after = (Canonical(to.Replica.Content()), B64(to.Doc.EncodeStateAsUpdateV1()), B64(to.Doc.EncodeStateVectorV1()));
                if (e is not YUpdateException)
                {
                    RejectionViolations.Add($"threw {e.GetType().Name}: {e.Message}");
                }
                else if (before != after)
                {
                    RejectionViolations.Add($"rejected ({e.Message}) but the document changed");
                }

                throw;
            }
        }

        private object Merged(Dictionary<string, object> s, Client to)
        {
            var v2 = s.Str("via") == "v2";
            var updates = s.List("from").Cast<List<object>>()
                .Select(p => UpdateOf(p[0], ((JsonNumber)p[1]).Long, v2))
                .Where(u => u != null)
                .ToList();
            if (updates.Count == 0)
            {
                return "skip";
            }

            var u = v2 ? UpdateOperations.MergeUpdatesV2(updates) : UpdateOperations.MergeUpdates(updates);
            var merged = u;
            if (s.Bool("diff"))
            {
                u = v2 ? UpdateOperations.DiffUpdateV2(u, to.Doc.EncodeStateVector()) : UpdateOperations.DiffUpdate(u, to.Doc.EncodeStateVector());
            }

            var meta = v2 ? UpdateOperations.ParseUpdateMetaV2(u) : UpdateOperations.ParseUpdateMeta(u);
            var r = Obj(
                ("m", B64(merged)),
                ("u", B64(u)),
                ("sv", B64(v2 ? UpdateOperations.EncodeStateVectorFromUpdateV2(u) : UpdateOperations.EncodeStateVectorFromUpdate(u))),
                ("from", Clocks(meta.From)),
                ("to", Clocks(meta.To)));
            if (v2)
            {
                to.Doc.ApplyUpdateV2(u, Net);
            }
            else
            {
                to.Doc.ApplyUpdateV1(u, Net);
            }

            return r;
        }

        /// <summary>The shared types of client <paramref name="client"/> a step can target (for the generator).</summary>
        public List<Target> Targets(int client) => Clients[client].Replica.Targets();

        public int LogCount(int client) => Clients[client].Log.Count;

        public IReadOnlyCollection<string> UndoManagers(int client) => Clients[client].UndoManagers.Keys;

        public IReadOnlyCollection<string> Snapshots(int client) => Clients[client].Snapshots.Keys;

        internal static string B64(byte[] b) => Convert.ToBase64String(b);

        internal static Dictionary<string, object> Clocks(IEnumerable<KeyValuePair<long, long>> clocks)
        {
            var d = new Dictionary<string, object>();
            foreach (var (c, k) in clocks)
            {
                d[c.ToString(System.Globalization.CultureInfo.InvariantCulture)] = k;
            }

            return d;
        }

        /// <summary>Whether the v1 update's delete set has a client whose ranges are out of order or overlap (valid v1, not v2).</summary>
        internal static bool HasUnorderedDeleteSet(string base64V1)
        {
            try
            {
                return UpdateOperations.DecodeUpdate(Convert.FromBase64String(base64V1)).DeleteSet.Values
                    .Any(ranges => ranges.Zip(ranges.Skip(1)).Any(p => p.Second.Clock < p.First.Clock + p.First.Length));
            }
            catch (YUpdateException)
            {
                return false;
            }
        }

        internal static string IdText(object id) => id switch
        {
            JsonNumber n => n.Text,
            string s => s,
            null => "undefined",
            _ => id.ToString(),
        };
    }

    /// <summary>One client of a script: a document, its observers, its update log, undo managers and snapshots.</summary>
    internal sealed class Client
    {
        private readonly ScriptRun _run;
        internal readonly int Index;
        internal readonly Replica Replica;
        internal readonly List<(byte[] V1, byte[] V2)> Log = new();
        internal readonly Dictionary<string, UndoManager> UndoManagers = new();
        internal readonly Dictionary<string, Snapshot> Snapshots = new();
        private byte[] _pendingV1;

        internal YDoc Doc => Replica.Doc;

        /// <summary>The client id the script gave the document, or the one <see cref="ScriptRun"/> replaced a random one with.</summary>
        internal long Id;

        public Client(ScriptRun run, int index, Dictionary<string, object> spec)
        {
            _run = run;
            Index = index;
            // "gcf": a garbage collection filter that keeps every item whose clock is a multiple of it, and records each call.
            Predicate<ItemView> gcFilter = null;
            if (spec.Has("gcf"))
            {
                var keepEvery = spec.Int("gcf");
                gcFilter = item =>
                {
                    Push(Obj(("w", "gcf"), ("id", $"{item.Id.Client}:{item.Id.Clock}"), ("n", (long)item.Length)));
                    return item.Id.Clock % keepEvery != 0;
                };
            }

            Replica = new Replica(spec.Int("id"), spec.Get("gc") is not false, run.Roots, gcFilter);
            Id = Replica.ClientId;
            var doc = Replica.Doc;
            Replica.UseReferences(markers: run.Script.Get("markers") is not false, pairwiseMerge: run.Script.Get("merge") is "pairwise", extendRuns: run.Script.Get("extend") is not false, walk: run.Script.Get("walk") as string);
            // "quiet" (the client's, as oracle.mjs reads it, or the script's, which only Ycs reads): no observers and no
            // transaction handlers (only the update handlers the log needs), so that nothing makes the document split
            // the runs it extended in place.
            var quiet = spec.Get("quiet") is true || run.Script.Get("quiet") is true;
            foreach (var (name, _) in run.Roots)
            {
                if (quiet)
                {
                    break;
                }

                var t = Replica.Root(name);
                t.Observe((_, e) => Event("obs:" + name, [e.Event], e.Transaction));
                t.ObserveDeep((_, e) => Event("deep:" + name, e.Events, e.Transaction));
            }

            if (!quiet)
            {
                doc.AfterTransaction += (_, tr) => Push(Obj(
                    ("w", "atx"),
                    ("origin", run.OriginLabel(tr.Origin)),
                    ("local", tr.Local),
                    ("changed", tr.Changed
                        .Select(kv => (object)new List<object> { Replica.PathOf(kv.Key), kv.Value.Select(k => (object)(k ?? "")).OrderBy(k => (string)k, StringComparer.Ordinal).ToList() })
                        .OrderBy(Canonical, StringComparer.Ordinal)
                        .ToList())));
            }

            doc.Update += (_, e) =>
            {
                run.FixClientIds();
                Push(Obj(("w", "u1"), ("u", ScriptRun.B64(e.Item1)), ("origin", run.OriginLabel(e.Item2)), ("local", e.Item3.Local)));
                _pendingV1 = e.Item3.Local ? e.Item1 : null;
            };
            doc.UpdateV2 += (_, e) =>
            {
                Push(Obj(("w", "u2"), ("u", ScriptRun.B64(e.Item1)), ("origin", run.OriginLabel(e.Item2)), ("local", e.Item3.Local)));
                if (e.Item3.Local)
                {
                    Log.Add((_pendingV1, e.Item1));
                }

                _pendingV1 = null;
            };
            doc.SubdocsChanged += (_, e) => Push(Obj(
                ("w", "subdocs"),
                ("added", Guids(e.Added)),
                ("removed", Guids(e.Removed)),
                ("loaded", Guids(e.Loaded))));
        }

        private static List<object> Guids(IEnumerable<YDoc> docs) => docs.Select(d => d.Guid).Order(StringComparer.Ordinal).Cast<object>().ToList();

        internal void Push(Dictionary<string, object> record)
        {
            record["c"] = (long)Index;
            _run.Events.Add(record);
        }

        private void Event(string who, IEnumerable<YEvent> events, Transaction tr) => Push(Obj(
            ("w", who),
            ("es", events.Select(e => (object)EventView(e)).ToList()),
            ("origin", _run.OriginLabel(tr.Origin)),
            ("local", tr.Local)));

        private Dictionary<string, object> EventView(YEvent e)
        {
            var k = Replica.KindOf(e.Target);
            var o = Obj(("t", Replica.PathOf(e.Target)), ("k", k), ("p", e.Path.Select(p => p is int i ? (long)i : p).ToList()));
            var keys = e.Changes.Keys.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
            if (keys.Count > 0)
            {
                var kd = new Dictionary<string, object>();
                foreach (var (key, ch) in keys)
                {
                    kd[key] = ch.Action == ChangeAction.Add
                        ? Obj(("action", "add"))
                        : Obj(("action", ch.Action == ChangeAction.Update ? "update" : "delete"), ("old", Replica.View(ch.OldValue)));
                }

                o["keys"] = kd;
            }

            if (k is "text" or "xmlt")
            {
                o["d"] = Replica.DeltaView(e.Delta);
                o["clc"] = e is YTextEvent te ? te.ChildListChanged : "?";
            }
            else if (k is not "map" and not "hook")
            {
                o["d"] = Replica.DeltaView(e.Changes.Delta);
            }

            if (k is "xmle" or "xmlf")
            {
                if (e is YXmlEvent xe)
                {
                    o["ac"] = xe.AttributesChanged.Order(StringComparer.Ordinal).Cast<object>().ToList();
                    o["clc"] = xe.ChildListChanged;
                }
                else
                {
                    o["ac"] = "?";
                }
            }

            return o;
        }

        /// <summary>A step on this client's document (oracle.mjs' Run.local).</summary>
        internal object Local(Dictionary<string, object> s)
        {
            switch (s.Str("op"))
            {
                case "um":
                    return NewUndoManager(s);
                case "undo":
                case "redo":
                case "stop":
                case "umclear":
                    {
                        if (!UndoManagers.TryGetValue(ScriptRun.IdText(s.Get("id")), out var um))
                        {
                            return "skip";
                        }

                        object r = null;
                        switch (s.Str("op"))
                        {
                            case "undo": r = um.Undo() != null; break;
                            case "redo": r = um.Redo() != null; break;
                            case "stop": um.StopCapturing(); break;
                            default: um.Clear(); break;
                        }

                        return Obj(("r", r), ("u", um.CanUndo()), ("d", um.CanRedo()), ("n", (long)um.UndoStack.Count));
                    }
                case "snap":
                    {
                        var snap = Doc.CreateSnapshot();
                        Snapshots[ScriptRun.IdText(s.Get("id"))] = snap;
                        return Obj(("s1", ScriptRun.B64(snap.EncodeSnapshot())), ("s2", ScriptRun.B64(snap.EncodeSnapshotV2())));
                    }
                case "docFromSnap":
                    {
                        if (!Snapshots.TryGetValue(ScriptRun.IdText(s.Get("id")), out var snap))
                        {
                            return "skip";
                        }

                        var d = Doc.CreateDocFromSnapshot(snap);
                        var roots = new Dictionary<string, object>();
                        foreach (var (name, kind) in _run.Roots)
                        {
                            roots[name] = Replica.View(Replica.RootOf(d, name, kind));
                        }

                        return Obj(("roots", roots), ("v1", ScriptRun.B64(d.EncodeStateAsUpdateV1())));
                    }
                case "rpos":
                    {
                        var t = Replica.Resolve(s.List("path"));
                        var kind = t == null ? null : Replica.KindOf(t);
                        if (t == null || kind is "map" or "hook")
                        {
                            return "skip";
                        }

                        var rp = RelativePosition.FromIndex(t, Clamp(s.Int("at"), t.Length), (int)s.Int("assoc"));
                        var bytes = rp.Encode();
                        _run.RelativePositions[ScriptRun.IdText(s.Get("id"))] = bytes;
                        return Obj(("b", ScriptRun.B64(bytes)), ("j", Replica.View(Replica.JsonOf(rp))));
                    }
                case "rres":
                    {
                        if (!_run.RelativePositions.TryGetValue(ScriptRun.IdText(s.Get("id")), out var bytes))
                        {
                            return "skip";
                        }

                        return !RelativePosition.Decode(bytes).TryResolve(Doc, out var abs) ? null : Obj(("p", Replica.PathOf(abs.Type)), ("i", (long)abs.Index), ("a", (long)abs.Assoc));
                    }
            }

            var target = Replica.Resolve(s.List("path"));
            if (target == null)
            {
                return "skip";
            }

            var k = Replica.KindOf(target);
            var text = target as YText;
            var isSeq = k is "array" or "xmlf" or "xmle";
            switch (s.Str("op"))
            {
                case "insert":
                    if (text == null)
                    {
                        return "skip";
                    }

                    text.Insert(Clamp(s.Int("at"), text.Length), s.Str("text"), AttrsOf(s.Get("attrs")));
                    return Missing.Value;
                case "delete":
                    {
                        if (text == null && !isSeq)
                        {
                            return "skip";
                        }

                        var at = Clamp(s.Int("at"), target.Length);
                        var len = Clamp(s.Int("len"), target.Length - at);
                        if (text != null)
                        {
                            text.Delete(at, len);
                        }
                        else if (target is YArray a)
                        {
                            a.Delete(at, len);
                        }
                        else
                        {
                            ((YXmlFragment)target).Delete(at, len);
                        }

                        return Missing.Value;
                    }
                case "format":
                    if (text == null)
                    {
                        return "skip";
                    }

                    text.Format(Clamp(s.Int("at"), text.Length), (int)s.Int("len"), AttrsOf(s.Get("attrs")));
                    return Missing.Value;
                case "embed":
                    if (text == null)
                    {
                        return "skip";
                    }

                    text.InsertEmbed(Clamp(s.Int("at"), text.Length), Val(s.Get("value")), AttrsOf(s.Get("attrs")));
                    return Missing.Value;
                case "delta":
                    if (text == null)
                    {
                        return "skip";
                    }

                    text.ApplyDelta(DeltaOf(s.List("delta")), s.Get("sanitize") is not false);
                    return Missing.Value;
                case "ains":
                    if (!isSeq)
                    {
                        return "skip";
                    }

                    SeqInsert(target, Clamp(s.Int("at"), target.Length), Values(s));
                    return Missing.Value;
                case "push":
                    if (!isSeq)
                    {
                        return "skip";
                    }

                    if (target is YArray pa)
                    {
                        pa.Push(Values(s));
                    }
                    else
                    {
                        ((YXmlFragment)target).Push(Values(s));
                    }

                    return Missing.Value;
                case "unshift":
                    if (!isSeq)
                    {
                        return "skip";
                    }

                    if (target is YArray ua)
                    {
                        ua.Unshift(Values(s));
                    }
                    else
                    {
                        ((YXmlFragment)target).Unshift(Values(s));
                    }

                    return Missing.Value;
                case "xafter":
                    {
                        if (target is not YXmlFragment f)
                        {
                            return "skip";
                        }

                        var child = s.Get("ref") == null || f.Length == 0 ? null : f.Get(Clamp(s.Int("ref"), f.Length - 1));
                        if (child is not (null or YType))
                        {
                            // Yjs takes a reference that is not a type as the item itself and throws on it ('n'.right)
                            // before it changes anything; InsertAfter's parameter cannot hold one.
                            throw new InvalidOperationException("The reference is not a type: Yjs' insertAfter throws.");
                        }

                        f.InsertAfter((YType)child, Values(s));
                        return Missing.Value;
                    }
                case "set":
                    if (k != "map")
                    {
                        return "skip";
                    }

                    ((YMap)target).Set(s.Str("key"), Val(s.Get("value")));
                    return Missing.Value;
                case "mdel":
                    if (k != "map")
                    {
                        return "skip";
                    }

                    ((YMap)target).Delete(s.Str("key"));
                    return Missing.Value;
                case "clear":
                    if (k != "map")
                    {
                        return "skip";
                    }

                    ((YMap)target).Clear();
                    return Missing.Value;
                case "setAttr":
                    if (target is YXmlElement se)
                    {
                        se.SetAttribute(s.Str("key"), Val(s.Get("value")));
                    }
                    else if (text != null)
                    {
                        text.SetAttribute(s.Str("key"), Val(s.Get("value")));
                    }
                    else
                    {
                        return "skip";
                    }

                    return Missing.Value;
                case "rmAttr":
                    if (target is YXmlElement re)
                    {
                        re.RemoveAttribute(s.Str("key"));
                    }
                    else if (text != null)
                    {
                        text.RemoveAttribute(s.Str("key"));
                    }
                    else
                    {
                        return "skip";
                    }

                    return Missing.Value;
                case "observe":
                    {
                        var label = "o:" + string.Join("/", s.List("path").Select(p => p is JsonNumber n ? n.Text : (string)p));
                        target.Observe((_, e) => Event(label, [e.Event], e.Transaction));
                        target.ObserveDeep((_, e) => Event("deep" + label, e.Events, e.Transaction));
                        return Missing.Value;
                    }
                case "react":
                    {
                        // The first time the type changes, its observer makes the operations (in a transaction of their
                        // own with "txn"; else each in its own), also during another transaction's cleanup. With "on" the
                        // trigger is the document's handler instead: the first transaction's "cleanup" (after its merge
                        // pass), "update" or "after" (afterTransaction), whatever type it changed.
                        var fired = false;
                        void Fire(bool local)
                        {
                            if (fired)
                            {
                                return;
                            }

                            fired = true;
                            if (s.Has("on"))
                            {
                                _run.FixClientIds();
                            }

                            var results = new List<object>();
                            void Each()
                            {
                                foreach (Dictionary<string, object> op in s.List("ops"))
                                {
                                    try
                                    {
                                        var r = Local(op);
                                        results.Add(r is Missing ? null : r);
                                    }
                                    catch (Exception)
                                    {
                                        results.Add(Obj(("err", true)));
                                    }
                                }
                            }

                            if (s.Bool("txn"))
                            {
                                Doc.Transact(_ => Each());
                            }
                            else
                            {
                                Each();
                            }

                            Push(Obj(("w", "react"), ("id", s.Get("id")), ("local", local), ("r", results)));
                        }

                        switch (s.Get("on") as string)
                        {
                            case "cleanup":
                                Doc.AfterTransactionCleanup += (_, tr) => Fire(tr.Local);
                                break;
                            case "update":
                                Doc.Update += (_, e) => Fire(e.Item3.Local);
                                break;
                            case "after":
                                Doc.AfterTransaction += (_, tr) => Fire(tr.Local);
                                break;
                            default:
                                target.Observe((_, e) => Fire(e.Transaction.Local));
                                break;
                        }

                        return Missing.Value;
                    }
                case "readSnap":
                    {
                        if (!Snapshots.TryGetValue(ScriptRun.IdText(s.Get("id")), out var snap))
                        {
                            return "skip";
                        }

                        if (text != null)
                        {
                            Snapshot prev = null;
                            if (s.Has("prev"))
                            {
                                Snapshots.TryGetValue(ScriptRun.IdText(s.Get("prev")), out prev);
                            }

                            return Replica.DeltaView(text.ToDelta(snap, prev));
                        }

                        if (isSeq)
                        {
                            return ((YArrayBase)target).ToArraySnapshot(snap).Select(Replica.View).ToList();
                        }

                        if (k == "map")
                        {
                            return Replica.View(target.GetAllSnapshot(snap));
                        }

                        return "skip";
                    }
                default:
                    throw new ArgumentException($"unknown op {s.Str("op")}");
            }
        }

        private object NewUndoManager(Dictionary<string, object> s)
        {
            var scope = new List<object>();
            foreach (var p in s.List("scope"))
            {
                if (p is "$doc")
                {
                    scope.Add(Doc);
                }
                else if (Replica.Resolve(p as List<object>) is { } t)
                {
                    scope.Add(t);
                }
            }

            if (scope.Count == 0)
            {
                return "skip";
            }

            var origins = s.List("origins") is { } list ? new HashSet<object>(list) : null;
            var options = new UndoManagerOptions { CaptureTimeout = s.Str("timeout") == "inf" ? int.MaxValue : 0 };
            if (origins != null)
            {
                options.TrackedOrigins = origins;
            }

            var um = new UndoManager(scope, options);
            var id = $"{Index}.{ScriptRun.IdText(s.Get("id"))}";
            _run.UndoIds[um] = id;
            UndoManagers[ScriptRun.IdText(s.Get("id"))] = um;
            um.StackItemAdded += (_, e) => Push(Obj(("w", "um+"), ("id", id), ("type", e.Type == UndoManager.OperationType.Undo ? "undo" : "redo"), ("origin", _run.OriginLabel(e.Origin))));
            um.StackItemUpdated += (_, e) => Push(Obj(("w", "um~"), ("id", id), ("type", e.Type == UndoManager.OperationType.Undo ? "undo" : "redo"), ("origin", _run.OriginLabel(e.Origin))));
            um.StackItemPopped += (_, e) => Push(Obj(("w", "um-"), ("id", id), ("type", e.Type == UndoManager.OperationType.Undo ? "undo" : "redo"), ("origin", _run.OriginLabel(e.Origin))));
            um.StackCleared += (_, e) => Push(Obj(("w", "umclear"), ("id", id), ("u", e.UndoStackCleared), ("r", e.RedoStackCleared)));
            return Missing.Value;
        }

        private static List<object> Values(Dictionary<string, object> s) => s.List("values").Select(Val).ToList();

        private static void SeqInsert(YType t, int at, List<object> values)
        {
            if (t is YArray a)
            {
                a.Insert(at, values);
            }
            else
            {
                ((YXmlFragment)t).Insert(at, values);
            }
        }

        private static int Clamp(long i, int n) => (int)Math.Max(0, Math.Min(i, n));

        private static IDictionary<string, object> AttrsOf(object a) => a == null ? null : (IDictionary<string, object>)Val(a);

        /// <summary>A script delta (docs/testing.md) as a Ycs delta.</summary>
        private static List<Delta> DeltaOf(List<object> delta) => delta.Cast<Dictionary<string, object>>().Select(d => new Delta
        {
            Insert = d.Has("insert") ? Val(d.Get("insert")) : null,
            Retain = d.Has("retain") ? (int)d.Int("retain") : null,
            Delete = d.Has("delete") ? (int)d.Int("delete") : null,
            Attributes = d.Has("attrs") ? AttrsOf(d.Get("attrs")) : null,
        }).ToList();

        /// <summary>A new text with its prelim content: "s" inserted, then the delta "dl" applied (both wait for the integration).</summary>
        private static YText PrelimText(YText text, Dictionary<string, object> s)
        {
            if (s.List("dl") is { } delta)
            {
                text.ApplyDelta(DeltaOf(delta), s.Get("dls") is not false);
            }

            return text;
        }

        /// <summary>A script value as the Ycs value it stands for (oracle.mjs' val); shared types are new.</summary>
        internal static object Val(object s)
        {
            switch (s)
            {
                case null:
                case bool:
                case string:
                    return s;
                case JsonNumber n:
                    if (!n.IsInteger)
                    {
                        return n.Double;
                    }

                    var l = n.Long;
                    return l is >= int.MinValue and <= int.MaxValue ? (object)(int)l : l;
                case List<object> list:
                    return list.Select(Val).ToList();
                case Dictionary<string, object> o:
                    if (o.Has("$i"))
                    {
                        var v = long.Parse(o.Str("$i"), System.Globalization.CultureInfo.InvariantCulture);
                        return v is >= int.MinValue and <= int.MaxValue ? (object)(int)v : v;
                    }

                    if (o.Has("$f"))
                    {
                        return FloatFromTag(o.Str("$f"));
                    }

                    if (o.Has("$n"))
                    {
                        return BigInteger.Parse(o.Str("$n"), System.Globalization.CultureInfo.InvariantCulture);
                    }

                    if (o.Has("$b"))
                    {
                        return Convert.FromBase64String(o.Str("$b"));
                    }

                    if (o.Has("$t"))
                    {
                        return NewType(o);
                    }

                    if (o.Has("$doc"))
                    {
                        var d = (Dictionary<string, object>)o["$doc"];
                        return new YDoc(new YDocOptions { Guid = d.Str("g"), Gc = d.Get("gc") is not false, AutoLoad = d.Bool("autoLoad") });
                    }

                    return o.ToDictionary(kv => kv.Key, kv => Val(kv.Value));
                default:
                    throw new ArgumentException($"not a script value: {s.GetType()}");
            }
        }

        internal static double FloatFromTag(string tag) => tag switch
        {
            "NaN" => double.NaN,
            "Infinity" => double.PositiveInfinity,
            "-Infinity" => double.NegativeInfinity,
            "-0" => -0.0,
            _ => BitConverter.Int64BitsToDouble(unchecked((long)ulong.Parse(tag, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture))),
        };

        private static YType NewType(Dictionary<string, object> s)
        {
            switch (s.Str("$t"))
            {
                case "text": return PrelimText(s.Has("s") ? new YText(s.Str("s")) : new YText(), s);
                case "xmlt": return PrelimText(s.Has("s") ? new YXmlText(s.Str("s")) : new YXmlText(), s);
                case "array": return new YArray((s.List("v") ?? []).Select(Val).ToList());
                case "map":
                    {
                        var entries = new Dictionary<string, object>();
                        foreach (List<object> kv in s.List("v") ?? [])
                        {
                            entries[(string)kv[0]] = Val(kv[1]);
                        }

                        return new YMap(entries);
                    }
                case "xmlf":
                    {
                        var f = new YXmlFragment();
                        if (s.List("v") is { } children)
                        {
                            f.Insert(0, children.Select(Val).ToList());
                        }

                        return f;
                    }
                case "xmle":
                    {
                        var e = new YXmlElement(s.Str("n"));
                        foreach (List<object> kv in s.List("a") ?? [])
                        {
                            e.SetAttribute((string)kv[0], Val(kv[1]));
                        }

                        if (s.List("v") is { } children)
                        {
                            e.Insert(0, children.Select(Val).ToList());
                        }

                        return e;
                    }
                default:
                    throw new ArgumentException($"unknown type {s.Str("$t")}");
            }
        }
    }

    /// <summary>
    /// A Ycs document with declared roots, and everything the tests read from it as plain values:
    /// the observation of oracle.mjs' Client.state, content views, encodings, the item lists.
    /// </summary>
    internal sealed class Replica
    {
        internal readonly YDoc Doc;
        private readonly List<(string Name, string Kind)> _roots;

        public Replica(long? clientId, bool gc, IEnumerable<(string Name, string Kind)> roots, Predicate<ItemView> gcFilter = null)
        {
            var options = new YDocOptions { Gc = gc, ClientId = clientId };
            if (gcFilter != null)
            {
                options.GcFilter = gcFilter;
            }

            Doc = new YDoc(options);
            _roots = roots.ToList();
            foreach (var (name, kind) in _roots)
            {
                RootOf(Doc, name, kind);
            }
        }

        public IReadOnlyList<(string Name, string Kind)> Roots => _roots;

        public bool Gc => Doc.Gc;

        public long ClientId => Doc.ClientId;

        internal YType Root(string name)
        {
            foreach (var (n, kind) in _roots)
            {
                if (n == name)
                {
                    return RootOf(Doc, n, kind);
                }
            }

            return null;
        }

        internal static YType RootOf(YDoc doc, string name, string kind) => kind switch
        {
            "text" => doc.GetText(name),
            "array" => doc.GetArray(name),
            "map" => doc.GetMap(name),
            "xml" => doc.GetXmlFragment(name),
            _ => throw new ArgumentException($"unknown root kind {kind}"),
        };

        // ---- bytes

        public byte[] EncodeV1(byte[] stateVector = null) => Doc.EncodeStateAsUpdateV1(stateVector);

        public byte[] EncodeV2(byte[] stateVector = null) => Doc.EncodeStateAsUpdateV2(stateVector);

        public byte[] StateVector() => Doc.EncodeStateVectorV1();

        public void ApplyV1(byte[] update, object origin = null) => Doc.ApplyUpdateV1(update, origin ?? ScriptRun.Net);

        public void ApplyV2(byte[] update, object origin = null) => Doc.ApplyUpdateV2(update, origin ?? ScriptRun.Net);

        public bool HasPending => Doc.HasPending;

        /// <summary>Raised with (v1 update, local) for every transaction that changed something.</summary>
        public event Action<byte[], bool> UpdateV1
        {
            add => Doc.Update += (_, e) => value(e.Item1, e.Item3.Local);
            remove => throw new NotSupportedException();
        }

        // ---- the observation of a state (oracle.mjs Client.state)

        public Dictionary<string, object> State(bool full)
        {
            var v1 = Doc.EncodeStateAsUpdateV1();
            var v2 = Doc.EncodeStateAsUpdateV2();
            var sv = Doc.EncodeStateVectorV1();
            return Obj(
                ("sv", ScriptRun.Clocks(Lib0.DecodeStateVector(sv))),
                ("svb", ScriptRun.B64(sv)),
                ("ds", DsView(UpdateOperations.DecodeUpdate(v1).DeleteSet)),
                ("v1", full ? ScriptRun.B64(v1) : Hash(v1)),
                ("v2", full ? ScriptRun.B64(v2) : Hash(v2)),
                ("pend", Doc.HasPending),
                ("roots", Content()));
        }

        /// <summary>The roots' views (deep content, toJSON and length): what a user of the document sees.</summary>
        public Dictionary<string, object> Content()
        {
            var roots = new Dictionary<string, object>();
            foreach (var (name, kind) in _roots)
            {
                var t = RootOf(Doc, name, kind);
                roots[name] = Obj(("v", View(t)), ("j", JsonView(t)), ("n", (long)LengthOf(t)));
            }

            return roots;
        }

        private static object JsonView(YType t)
        {
            try
            {
                return View(t.ToJson());
            }
            catch (Exception)
            {
                return Obj(("$err", "toJSON"));
            }
        }

        /// <summary>The delete set of an update, merged and sorted, as plain values.</summary>
        public static Dictionary<string, object> DeleteSetOf(byte[] update, bool v2) =>
            DsView((v2 ? UpdateOperations.DecodeUpdateV2(update) : UpdateOperations.DecodeUpdate(update)).DeleteSet);

        private static Dictionary<string, object> DsView(IReadOnlyDictionary<long, IReadOnlyList<DeletedRange>> ds)
        {
            var o = new Dictionary<string, object>();
            foreach (var (client, items) in ds.OrderBy(kv => kv.Key))
            {
                var merged = new List<long[]>();
                foreach (var r in items.OrderBy(r => r.Clock))
                {
                    if (merged.Count > 0 && merged[^1][0] + merged[^1][1] >= r.Clock)
                    {
                        merged[^1][1] = Math.Max(merged[^1][1], r.Clock + r.Length - merged[^1][0]);
                    }
                    else
                    {
                        merged.Add([r.Clock, r.Length]);
                    }
                }

                if (merged.Count > 0)
                {
                    o[client.ToString(System.Globalization.CultureInfo.InvariantCulture)] = merged.Select(m => (object)new List<object> { m[0], m[1] }).ToList();
                }
            }

            return o;
        }

        private static string Hash(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b))[..16];

        // ---- views

        internal static string KindOf(YType t) => t switch
        {
            YXmlText => "xmlt",
            YText => "text",
            YXmlElement => "xmle",
            YXmlFragment => "xmlf",
            YXmlHook => "hook",
            YMap => "map",
            YArray => "array",
            _ => null,
        };

        internal static int LengthOf(YType t) => t is YMap m ? m.Count : t.Length;

        private const long MaxSafeInteger = (1L << 53) - 1;

        private static object NumberView(double d)
        {
            if (double.IsNaN(d))
            {
                return Obj(("$f", "NaN"));
            }

            if (double.IsPositiveInfinity(d))
            {
                return Obj(("$f", "Infinity"));
            }

            if (double.IsNegativeInfinity(d))
            {
                return Obj(("$f", "-Infinity"));
            }

            if (d == 0 && double.IsNegative(d))
            {
                return Obj(("$f", "-0"));
            }

            if (Math.Floor(d) == d && Math.Abs(d) <= MaxSafeInteger)
            {
                return (long)d;
            }

            return Obj(("$f", BitConverter.DoubleToInt64Bits(d).ToString("x16", System.Globalization.CultureInfo.InvariantCulture)));
        }

        private static object NumberView(long l) => Math.Abs(l) <= MaxSafeInteger ? l : NumberView((double)l);

        /// <summary>What a value looks like (oracle.mjs' view): shared types with their kind and content.</summary>
        internal static object JsonOf(RelativePosition position)
        {
            using var stream = new MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
            {
                position.WriteJson(writer);
            }

            using var document = System.Text.Json.JsonDocument.Parse(stream.ToArray());
            return Plain(document.RootElement);
        }

        private static object Plain(System.Text.Json.JsonElement e) => e.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => Plain(p.Value)),
            System.Text.Json.JsonValueKind.String => e.GetString(),
            _ => e.GetInt64(),
        };

        internal static object View(object v)
        {
            switch (v)
            {
                case null:
                    return null;
                case bool or string:
                    return v;
                case int i:
                    return (long)i;
                case long l:
                    return NumberView(l);
                case short or sbyte or byte or ushort or uint:
                    return Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture);
                case ulong ul:
                    return NumberView((double)ul);
                case double d:
                    return NumberView(d);
                case float f:
                    return NumberView((double)f);
                case BigInteger b:
                    return Obj(("$n", b.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                case byte[] bytes:
                    return Obj(("$b", ScriptRun.B64(bytes)));
                case YDoc d:
                    return Obj(("$t", "doc"), ("g", d.Guid));
                case ItemId id:
                    return Obj(("client", id.Client), ("clock", id.Clock));
                case YText.YTextChangeAttributes ychange:
                    // Yjs' default computeYChange gives { type: 'added' | 'removed' }.
                    return Obj(("type", ychange.Type == YText.YTextChangeType.Added ? "added" : "removed"));
                case YType t:
                    return TypeView(t);
                case IDictionary<string, object> map:
                    return map.ToDictionary(kv => kv.Key, kv => View(kv.Value));
                case IDictionary dict:
                    {
                        var o = new Dictionary<string, object>();
                        foreach (DictionaryEntry e in dict)
                        {
                            o[(string)e.Key] = View(e.Value);
                        }

                        return o;
                    }
                case IEnumerable list:
                    return list.Cast<object>().Select(View).ToList();
                default:
                    return "?" + v.GetType().Name;
            }
        }

        private static object AttrsView(IEnumerable<KeyValuePair<string, object>> attrs)
        {
            if (attrs == null)
            {
                return Missing.Value;
            }

            var o = new Dictionary<string, object>();
            foreach (var (k, v) in attrs)
            {
                o[k] = View(v);
            }

            return o.Count == 0 ? Missing.Value : o;
        }

        internal static List<object> DeltaView(IEnumerable<Delta> delta) => delta.Select(d =>
        {
            var o = new Dictionary<string, object>();
            if (d.Insert != null)
            {
                o["insert"] = View(d.Insert);
            }

            if (d.Retain.HasValue)
            {
                o["retain"] = (long)d.Retain.Value;
            }

            if (d.Delete.HasValue)
            {
                o["delete"] = (long)d.Delete.Value;
            }

            if (AttrsView(d.Attributes) is Dictionary<string, object> a)
            {
                o["attributes"] = a;
            }

            return (object)o;
        }).ToList();

        private static Dictionary<string, object> MapView(IEnumerable<KeyValuePair<string, object>> entries)
        {
            var o = new Dictionary<string, object>();
            foreach (var (k, x) in entries)
            {
                o[k] = View(x);
            }

            return o;
        }

        private static object TypeView(YType t) => KindOf(t) switch
        {
            "text" => Obj(("$t", "text"), ("d", DeltaView(((YText)t).ToDelta())), ("a", AttrsView(((YText)t).GetAttributes()))),
            "xmlt" => Obj(("$t", "xmlt"), ("d", DeltaView(((YText)t).ToDelta())), ("a", AttrsView(((YText)t).GetAttributes()))),
            "array" => Obj(("$t", "array"), ("v", ((YArray)t).ToArray().Select(View).ToList())),
            "map" => Obj(("$t", "map"), ("v", MapView((YMap)t))),
            "xmlf" => Obj(("$t", "xmlf"), ("v", ((YXmlFragment)t).ToArray().Select(View).ToList())),
            "xmle" => Obj(("$t", "xmle"), ("n", ((YXmlElement)t).NodeName), ("a", AttrsView(((YXmlElement)t).GetAttributes())), ("v", ((YXmlFragment)t).ToArray().Select(View).ToList())),
            "hook" => Obj(("$t", "hook"), ("n", ((YXmlHook)t).HookName), ("v", MapView((YMap)t))),
            _ => Obj(("$t", "?")),
        };

        // ---- paths

        /// <summary>The path of a type from its root, by visible content only ("~" where it is not visible).</summary>
        internal List<object> PathOf(YType t)
        {
            var steps = new List<object>();
            while (true)
            {
                foreach (var (name, kind) in _roots)
                {
                    if (ReferenceEquals(RootOf(Doc, name, kind), t))
                    {
                        steps.Add(name);
                        steps.Reverse();
                        return steps;
                    }
                }

                var parent = t.Parent;
                var step = parent == null ? null : ChildStep(parent, t);
                if (step == null)
                {
                    return ["~"];
                }

                steps.Add(step);
                t = parent;
            }
        }

        private static object ChildStep(YType parent, YType t)
        {
            switch (KindOf(parent))
            {
                case "map":
                case "hook":
                    foreach (var (key, v) in (YMap)parent)
                    {
                        if (ReferenceEquals(v, t))
                        {
                            return key;
                        }
                    }

                    return null;
                case "text":
                case "xmlt":
                    {
                        long i = 0;
                        foreach (var d in ((YText)parent).ToDelta())
                        {
                            if (ReferenceEquals(d.Insert, t))
                            {
                                return i;
                            }

                            i += d.Insert is string s ? s.Length : 1;
                        }

                        return null;
                    }
                default:
                    {
                        var items = parent is YArray a ? a.ToArray() : ((YXmlFragment)parent).ToArray();
                        for (var i = 0; i < items.Count; i++)
                        {
                            if (ReferenceEquals(items[i], t))
                            {
                                return (long)i;
                            }
                        }

                        return null;
                    }
            }
        }

        internal YType Resolve(List<object> path)
        {
            if (path == null || path.Count == 0 || path[0] is not string rootName)
            {
                return null;
            }

            var t = Root(rootName);
            for (var i = 1; t != null && i < path.Count; i++)
            {
                var k = KindOf(t);
                object next;
                if (path[i] is JsonNumber or long)
                {
                    var p = path[i] is JsonNumber n ? n.Long : (long)path[i];
                    if (k is not ("array" or "xmlf" or "xmle") || p < 0 || p >= t.Length)
                    {
                        return null;
                    }

                    next = t is YArray a ? a.Get((int)p) : ((YXmlFragment)t).Get((int)p);
                }
                else
                {
                    if (k != "map")
                    {
                        return null;
                    }

                    var map = (YMap)t;
                    next = map.ContainsKey((string)path[i]) ? map.Get((string)path[i]) : null;
                }

                t = next as YType;
                if (t == null)
                {
                    return null;
                }
            }

            return t;
        }

        /// <summary>Every shared type reachable by visible content, with the path Resolve takes.</summary>
        internal List<Target> Targets()
        {
            var result = new List<Target>();
            foreach (var (name, kind) in _roots)
            {
                Visit(RootOf(Doc, name, kind), [name], result, 0);
            }

            return result;
        }

        private static void Visit(YType t, List<object> path, List<Target> into, int depth)
        {
            var k = KindOf(t);
            var keys = t is YMap m ? m.Keys().ToList() : (IReadOnlyList<string>)[];
            into.Add(new Target(path, k, LengthOf(t), keys));
            if (depth > 6)
            {
                return;
            }

            if (t is YMap map)
            {
                foreach (var (key, v) in map)
                {
                    if (v is YType child)
                    {
                        Visit(child, [.. path, key], into, depth + 1);
                    }
                }
            }
            else if (k is "array" or "xmlf" or "xmle")
            {
                var items = t is YArray a ? a.ToArray() : ((YXmlFragment)t).ToArray();
                for (var i = 0; i < items.Count; i++)
                {
                    if (items[i] is YType child)
                    {
                        Visit(child, [.. path, (long)i], into, depth + 1);
                    }
                }
            }
        }


        // ---- the internals the reference comparisons read and switch (search markers off, Yjs' merge order)

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "SearchMarkersDisabledForTests")]
        private static extern ref bool SearchMarkersDisabled(YDoc doc);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Store")]
        [return: UnsafeAccessorType("Ycs.StructStore, Ycs")]
        private static extern object StoreOf(YDoc doc);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "PairwiseMergeForTests")]
        private static extern ref bool PairwiseMerge([UnsafeAccessorType("Ycs.StructStore, Ycs")] object store);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "DumpForTests")]
        private static extern void DumpOf([UnsafeAccessorType("Ycs.StructStore, Ycs")] object store, StringBuilder sb, bool markers, Func<object, string> parentText, Func<string, IReadOnlyList<object>, string> contentText);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "NestedListsForTests")]
        private static extern IEnumerable<(ItemId Item, YArrayBase List)> NestedListsOf([UnsafeAccessorType("Ycs.StructStore, Ycs")] object store);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "MapIdsForTests")]
        private static extern IEnumerable<KeyValuePair<string, ItemId>> MapIdsOf(YType type);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_OwnerIdForTests")]
        private static extern ItemId? OwnerIdOf(YType type);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "InDeletedTypeForTests")]
        private static extern bool InDeletedTypeOf(YType type);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CountedForTests")]
        private static extern (int Length, string Text) CountedOf(YType type);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ListForTests")]
        private static extern List<(long Client, long Clock, int Length, ItemId? LeftOrigin, ItemId? RightOrigin, bool Deleted, string Kind, bool LinkedBack, bool InParent)> ListOf(YType type);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "DescribeSearchMarkers")]
        private static extern string DescribeSearchMarkers(YArrayBase type);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SearchMarkerViolations")]
        private static extern string SearchMarkerViolations(YArrayBase type);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "RunExtensionsDisabledForTests")]
        private static extern ref bool RunExtensionsDisabled(YDoc doc);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_ClientId")]
        internal static extern void SetClientId(YDoc doc, long id);

        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_RunsExtended")]
        private static extern long RunsExtendedOf([UnsafeAccessorType("Ycs.StructStore, Ycs")] object store);

        /// <summary>How many inserts extended a run in place in this document.</summary>
        internal long RunsExtended => RunsExtendedOf(StoreOf(Doc));

        /// <param name="walk">"plain": the marker lookups walk item by item (Yjs' loops); "eager": they ask the run-head
        /// index at once (instead of after 32 steps); null: as by default. Either way every lookup is logged.</param>
        internal void UseReferences(bool markers, bool pairwiseMerge, bool extendRuns, string walk = null)
        {
            SearchMarkersDisabled(Doc) = !markers;
            PairwiseMerge(StoreOf(Doc)) = pairwiseMerge;
            RunExtensionsDisabled(Doc) = !extendRuns;
            RunHeadIndexDisabled(Doc) = walk == "plain";
            if (walk == "eager")
            {
                LiteralWalkStepsForTests(Doc) = 1;
            }

            MarkerLookups(Doc) = walk == null ? null : [];
        }

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "RunHeadIndexDisabledForTests")]
        private static extern ref bool RunHeadIndexDisabled(YDoc doc);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "LiteralWalkStepsForTests")]
        private static extern ref int LiteralWalkStepsForTests(YDoc doc);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "MarkerLookupsForTests")]
        private static extern ref List<(ItemId Item, int Index)> MarkerLookups(YDoc doc);

        /// <summary>The marker lookups since the last call, as "client:clock@index" (with the script option "walk"), and forgets them.</summary>
        public string TakeMarkerLookups()
        {
            var lookups = MarkerLookups(Doc);
            if (lookups == null)
            {
                return "";
            }

            var text = string.Join(" ", lookups.Select(l => $"{l.Item.Client}:{l.Item.Clock}@{l.Index}"));
            lookups.Clear();
            return text;
        }

        private static string IdOf(ItemId? id) => id is ItemId i ? $"{i.Client}:{i.Clock}" : "-";

        private string TypeName(YType t)
        {
            foreach (var (name, _) in _roots)
            {
                if (ReferenceEquals(Root(name), t))
                {
                    return "root:" + name;
                }
            }

            return OwnerIdOf(t) is { } owner ? "in:" + IdOf(owner) : "detached";
        }

        /// <summary>
        /// Every struct of the store, client by client, in clock order: kind, id, length, deletion, content, links
        /// (left, right, origins, parent, key), keep, redone, marker flag; then every type's map entries and search
        /// markers. Two documents that went through the same steps give the same text when their structs are the same.
        /// </summary>
        public string StoreDump(bool markers = true)
        {
            var sb = new StringBuilder();
            var types = new List<YType>();
            foreach (var (name, _) in _roots)
            {
                types.Add(Root(name));
            }

            DumpOf(
                StoreOf(Doc),
                sb,
                markers,
                parent => parent is YType pt ? TypeName(pt) : parent?.ToString() ?? "-",
                (kind, values) => ContentText(kind, values, types));

            foreach (var t in types)
            {
                sb.Append(TypeName(t)).Append(" map");
                foreach (var (key, item) in MapIdsOf(t).OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    sb.Append(' ').Append(key).Append('=').Append(IdOf(item));
                }

                if (markers && t is YArrayBase list)
                {
                    sb.Append(" markers ").Append(DescribeSearchMarkers(list));
                }

                sb.Append('\n');
            }

            return sb.ToString();
        }

        private static string ContentText(string kind, IReadOnlyList<object> values, List<YType> types)
        {
            if (kind == "ContentString")
            {
                return Canonical((string)values[0]);
            }

            var parts = new List<string>();
            foreach (var v in values)
            {
                if (v is YType t)
                {
                    types.Add(t);
                    parts.Add("type " + t.GetType().Name);
                }
                else
                {
                    parts.Add(v is YDoc d ? "doc " + d.Guid : Canonical(View(v)));
                }
            }

            return string.Join(",", parts);
        }

        /// <summary>
        /// What is wrong with the search markers of any list in the document (SearchMarkerViolations), or null. Not of a
        /// deleted type: a remote deletion of a type does not reach its observer, which is where Yjs (and Ycs) clear the
        /// markers a remote change leaves wrong.
        /// </summary>
        public string SearchMarkerProblems()
        {
            foreach (var (item, nested) in NestedListsOf(StoreOf(Doc)))
            {
                if (!InDeletedTypeOf(nested) && SearchMarkerViolations(nested) is { } v)
                {
                    return IdOf(item) + ": " + v;
                }
            }

            foreach (var (name, _) in _roots)
            {
                if (Root(name) is YArrayBase list && SearchMarkerViolations(list) is { } v)
                {
                    return name + ": " + v;
                }
            }

            return null;
        }

        // ---- the item lists (for ItemListInvariants and the naive recomputations)

        /// <summary>The item list of every sequence reachable by visible content from the roots, keyed by path.</summary>
        public Dictionary<string, IReadOnlyList<ItemRun>> Sequences()
        {
            var result = new Dictionary<string, IReadOnlyList<ItemRun>>();
            foreach (var target in Targets())
            {
                if (target.Kind is "map" or "hook")
                {
                    continue;
                }

                result[Canonical(target.Path)] = ItemsOf(Resolve(target.Path));
            }

            return result;
        }

        private static List<ItemRun> ItemsOf(YType type) =>
            [.. ListOf(type).Select(r => new ItemRun(
                r.Client,
                r.Clock,
                r.Length,
                r.LeftOrigin is ItemId o ? (o.Client, o.Clock) : null,
                r.RightOrigin is ItemId ro ? (ro.Client, ro.Clock) : null,
                r.Deleted,
                r.Kind,
                r.LinkedBack,
                r.InParent))];

        /// <summary>
        /// Every sequence's and map's ToString/Length/Count recomputed naively by walking the items,
        /// compared with what the type reports; the differences, one line each.
        /// </summary>
        public List<string> NaiveRecomputationDifferences()
        {
            var differences = new List<string>();
            foreach (var target in Targets())
            {
                var t = Resolve(target.Path);
                var where = Canonical(target.Path);
                if (t is YMap map)
                {
                    var keys = map.Keys().ToList();
                    if (map.Count != keys.Count)
                    {
                        differences.Add($"{where}: Count {map.Count}, {keys.Count} keys");
                    }

                    continue;
                }

                var (length, text) = CountedOf(t);

                if (t.Length != length)
                {
                    differences.Add($"{where}: Length {t.Length}, items count {length}");
                }

                if (t is YText yt and not YXmlText && yt.ToString() != text)
                {
                    differences.Add($"{where}: ToString {Canonical(yt.ToString())}, items say {Canonical(text)}");
                }
            }

            return differences;
        }

        /// <summary>Whether everything <paramref name="update"/> (v1) holds is in this document (a snapshot of it contains it).</summary>
        public bool Contains(byte[] update) => Doc.CreateSnapshot().SnapshotContainsUpdate(update);
    }

    /// <summary>Operations on encoded updates and snapshots, without a script.</summary>
    internal static class Updates
    {
        public static byte[] Merge(IList<byte[]> updates, bool v2) => v2 ? UpdateOperations.MergeUpdatesV2(updates) : UpdateOperations.MergeUpdates(updates);

        public static byte[] Diff(byte[] update, byte[] stateVector, bool v2) => v2 ? UpdateOperations.DiffUpdateV2(update, stateVector) : UpdateOperations.DiffUpdate(update, stateVector);

        public static byte[] V1ToV2(byte[] update) => UpdateOperations.ConvertUpdateFormatV1ToV2(update);

        public static byte[] V2ToV1(byte[] update) => UpdateOperations.ConvertUpdateFormatV2ToV1(update);

        public static byte[] StateVectorFromUpdate(byte[] update, bool v2) => v2 ? UpdateOperations.EncodeStateVectorFromUpdateV2(update) : UpdateOperations.EncodeStateVectorFromUpdate(update);

        public static bool IsUpdateException(Exception e) => e is YUpdateException;
    }

    /// <summary>The lib0 var-uint decoding of a state vector (the format of EncodeStateVector).</summary>
    internal static class Lib0
    {
        public static List<KeyValuePair<long, long>> DecodeStateVector(byte[] encoded)
        {
            var pos = 0;
            var n = ReadVarUint(encoded, ref pos);
            var result = new List<KeyValuePair<long, long>>();
            for (long i = 0; i < n; i++)
            {
                var client = ReadVarUint(encoded, ref pos);
                var clock = ReadVarUint(encoded, ref pos);
                result.Add(new(client, clock));
            }

            return result.OrderBy(kv => kv.Key).ToList();
        }

        public static long ReadVarUint(byte[] b, ref int pos)
        {
            long num = 0;
            for (var shift = 0; ; shift += 7)
            {
                var r = b[pos++];
                num |= (long)(r & 0x7F) << shift;
                if (r < 0x80)
                {
                    return num;
                }
            }
        }
    }
}
