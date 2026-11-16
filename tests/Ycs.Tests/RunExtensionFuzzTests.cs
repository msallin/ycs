using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Random scenarios run as Ycs and as its reference (<see cref="ReferenceRun"/>), whose logs must be equal: typing,
/// deletes, formats, surrogate halves, transactions of up to 120 operations, observers, deep observers and
/// transaction handlers attached and detached within transactions, encodings within transactions, an UndoManager
/// made, used and destroyed within transactions, throwing bodies and handlers, relative positions, peer syncs,
/// updates applied within transactions, garbage collection filters, snapshots taken and read within transactions,
/// and transactions started from observers and from update handlers. The log holds the content, the encodings,
/// every event with the items it added and deleted, the structs and the search markers after every step.
/// </summary>
/// <remarks>
/// <c>YCS_EXTENSION_FUZZ=first:count</c> runs other seeds (a soak: 40,000 seeds take about ten minutes).
/// </remarks>
[TestClass]
public class RunExtensionFuzzTests
{
    [TestMethod]
    public void RandomScenarios_CandidateAndReference_LogTheSame()
    {
        var (first, count) = Seeds();
        var failures = new List<string>();

        for (var seed = first; seed < first + count && failures.Count < 5; seed++)
        {
            var s = seed;
            if (ReferenceRun.FirstDifference(run => Scenario(run, s)) is { } difference)
            {
                failures.Add($"seed {seed}: {difference}");
            }
        }

        Assert.AreEqual(0, failures.Count, $"seeds {first}..{first + count - 1}:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    private static (ulong First, ulong Count) Seeds()
    {
        var spec = Environment.GetEnvironmentVariable("YCS_EXTENSION_FUZZ");
        if (string.IsNullOrEmpty(spec))
        {
            return (1, 300);
        }

        var parts = spec.Split(':');
        return (ulong.Parse(parts[0], CultureInfo.InvariantCulture), ulong.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    /// <summary>xorshift64, as the reviewer's generator and interop/markers/gen.mjs.</summary>
    private sealed class Rng
    {
        private ulong _s;

        public Rng(ulong seed)
        {
            _s = (seed * 0x9E3779B97F4A7C15UL) + 0x1234567;
            Next();
            Next();
        }

        public ulong Next()
        {
            _s ^= _s << 13;
            _s ^= _s >> 7;
            _s ^= _s << 17;
            return _s;
        }

        public int Int(int n) => n <= 0 ? 0 : (int)(Next() % (ulong)n);

        public bool Chance(double p) => Next() % 10000 < p * 10000;
    }

    private static object[] Vals(params object[] v) => v;

    internal static void Scenario(ReferenceRun c, ulong seed)
    {
        var rng = new Rng(seed);
        var gc = rng.Chance(0.6);
        Predicate<ItemView> filter = null;
        if (gc && rng.Chance(0.25))
        {
            var k = 2 + rng.Int(3);
            filter = item =>
            {
                c.L($"gcFilter {item.Id.Client}:{item.Id.Clock}+{item.Length}");
                return item.Id.Clock % k != 0;
            };
        }

        var doc = c.NewDoc(1, gc, filter);
        var peer = c.NewDoc(2, gc);
        var t = doc.GetText("t");
        var a = doc.GetArray("a");
        var m = doc.GetMap("m");
        var pt = peer.GetText("t");
        var pa = peer.GetArray("a");
        var pm = peer.GetMap("m");
        YText nt = null;
        YArray na = null;
        c.LogUpdates(doc, "d");
        var bold = new Dictionary<string, object> { ["bold"] = true };
        var relpos = new List<RelativePosition>();
        var snapshots = new List<Snapshot>();
        UndoManager um = null;
        Action<YTextEvent> tObs = null;
        Action<YArrayEvent> aObs = null;
        Action<YMapEvent> reactor = null;
        Action<IReadOnlyList<YEvent>, Transaction> mDeep = null;
        IDisposable tSub = null, aSub = null, rSub = null, mSub = null;
        EventHandler<Transaction> atx = null, boc = null;
        EventHandler<UpdateEventArgs> onUpdate = null;
        var caret = 0;
        var slot = 0;
        var armed = 0;
        if (rng.Chance(0.7))
        {
            t.Insert(0, new string('s', 20 + rng.Int(300)));
        }

        if (rng.Chance(0.5))
        {
            a.Push(Enumerable.Range(0, 10 + rng.Int(200)).Cast<object>().ToArray());
        }

        var throwInHandler = false;

        void SyncToPeer() => peer.ApplyUpdateV1(doc.EncodeStateAsUpdateV1(peer.EncodeStateVectorV1()));

        void PeerEdit()
        {
            var n = 1 + rng.Int(3);
            for (var i = 0; i < n; i++)
            {
                var r = rng.Int(7);
                if (r == 0 && pt.Length > 0)
                {
                    var at = rng.Int(pt.Length);
                    pt.Delete(at, Math.Min(1 + rng.Int(3), pt.Length - at));
                }
                else if (r <= 2)
                {
                    pt.Insert(rng.Int(pt.Length + 1), rng.Chance(0.5) ? "P" : "PQ");
                }
                else if (r == 3 && pa.Length > 0)
                {
                    var at = rng.Int(pa.Length);
                    pa.Delete(at, Math.Min(1 + rng.Int(2), pa.Length - at));
                }
                else if (r == 4)
                {
                    // Reaches the reactor on m during the remote transaction's cleanup.
                    pm.Set("p", rng.Int(10));
                }
                else
                {
                    pa.Insert(rng.Int(pa.Length + 1), Vals(-1 - rng.Int(9)));
                }
            }
        }

        byte[] RemoteDiff(bool v2) => v2 ? peer.EncodeStateAsUpdateV2(doc.EncodeStateVectorV1()) : peer.EncodeStateAsUpdateV1(doc.EncodeStateVectorV1());

        void ReadSnapshot()
        {
            if (snapshots.Count == 0)
            {
                return;
            }

            var s = snapshots[rng.Int(snapshots.Count)];
            var prev = rng.Chance(0.5) ? snapshots[rng.Int(snapshots.Count)] : null;
            Func<YText.YTextChangeType, ItemId, YText.YTextChangeAttributes> cy = rng.Chance(0.5)
                ? null
                : (type, id) =>
                {
                    c.L($"ychange {type} {id.Client}:{id.Clock}");
                    return new YText.YTextChangeAttributes { Type = type, User = rng.Chance(0.5) ? id.Client : null };
                };
            c.L("snap delta " + ReferenceRun.D(t.ToDelta(s, prev, cy)) + " array " + ReferenceRun.V(a.ToArraySnapshot(s)));
        }

        void ToggleObserver(int which)
        {
            switch (which)
            {
                case 0:
                    if (tObs == null)
                    {
                        tObs = c.TextObserver("t");
                        tSub = t.Observe(tObs);
                        c.L("+tObs");
                    }
                    else
                    {
                        tSub.Dispose();
                        tObs = null;
                        c.L("-tObs");
                    }

                    break;
                case 1:
                    if (aObs == null)
                    {
                        aObs = e =>
                        {
                            var ch = e.Changes;
                            c.L($"a delta={ReferenceRun.D(e.Delta)} added={ReferenceRun.Items(ch.Added)} deleted={ReferenceRun.Items(ch.Deleted)}");
                            if (throwInHandler)
                            {
                                throwInHandler = false;
                                throw new InvalidOperationException("handler");
                            }
                        };
                        aSub = a.Observe(aObs);
                        c.L("+aObs");
                    }
                    else
                    {
                        aSub.Dispose();
                        aObs = null;
                        c.L("-aObs");
                    }

                    break;
                case 2:
                    if (mDeep == null)
                    {
                        mDeep = c.DeepObserver("m");
                        mSub = m.ObserveDeep(mDeep);
                        c.L("+mDeep");
                    }
                    else
                    {
                        mSub.Dispose();
                        mDeep = null;
                        c.L("-mDeep");
                    }

                    break;
                case 3:
                    if (atx == null)
                    {
                        atx = (_, tr) => c.L("atx changed=" + string.Join(";", tr.Changed.Select(kv => kv.Key.GetType().Name + ":" + string.Join(",", kv.Value.Select(k => k ?? "~").OrderBy(k => k, StringComparer.Ordinal))).OrderBy(x => x, StringComparer.Ordinal)) + " cpt=" + tr.ChangedParentTypes.Count + " " + ReferenceRun.Dump(doc).Replace("\n", " ", StringComparison.Ordinal));
                        doc.AfterTransaction += atx;
                        c.L("+atx");
                    }
                    else
                    {
                        doc.AfterTransaction -= atx;
                        atx = null;
                        c.L("-atx");
                    }

                    break;
                case 4:
                    if (boc == null)
                    {
                        boc = (_, tr) => c.L("boc " + ReferenceRun.Dump(doc).Replace("\n", " ", StringComparison.Ordinal));
                        doc.BeforeObserverCalls += boc;
                        c.L("+boc");
                    }
                    else
                    {
                        doc.BeforeObserverCalls -= boc;
                        boc = null;
                        c.L("-boc");
                    }

                    break;
                case 5:
                    // A reactor: m's observer edits the lists (in a transaction of its own or one per operation) when armed,
                    // also during a remote transaction's cleanup, while the lists' markers are stale.
                    if (reactor == null)
                    {
                        reactor = e =>
                        {
                            c.L("reactor local=" + e.Transaction.Local + " armed=" + armed);
                            if (armed == 0)
                            {
                                return;
                            }

                            var k = armed;
                            armed = 0;
                            if (rng.Chance(0.5))
                            {
                                doc.Transact(_ =>
                                {
                                    for (var i = 0; i < k; i++)
                                    {
                                        Op(true);
                                    }
                                });
                            }
                            else
                            {
                                for (var i = 0; i < k; i++)
                                {
                                    Op(false);
                                }
                            }
                        };
                        rSub = m.Observe(reactor);
                        c.L("+reactor");
                    }
                    else
                    {
                        rSub.Dispose();
                        reactor = null;
                        c.L("-reactor");
                    }

                    break;
                case 6:
                    // An update handler that types on once: a transaction started after another's merge.
                    if (onUpdate == null)
                    {
                        var left = 1 + rng.Int(3);
                        onUpdate = (_, e) =>
                        {
                            if (left-- <= 0)
                            {
                                return;
                            }

                            // A remote transaction that brought this client's own structs gave it a random id just now.
                            c.FixClientId(doc);
                            caret = Math.Min(caret, t.Length);
                            t.Insert(caret, "u");
                            caret++;
                            a.Push(Vals(-100));
                        };
                        doc.Updated += onUpdate;
                        c.L("+update");
                    }
                    else
                    {
                        doc.Updated -= onUpdate;
                        onUpdate = null;
                        c.L("-update");
                    }

                    break;
            }
        }

        void Op(bool inTxn)
        {
            // A remote transaction that brought this client's own structs gave it a random id, perhaps just before a
            // handler called this.
            c.FixClientId(doc);
            var r = rng.Int(100);
            if (r < 28)
            {
                caret = Math.Min(caret, t.Length);
                var s = rng.Chance(0.85) ? "x" : rng.Chance(0.5) ? "yz" : rng.Chance(0.5) ? "😀" : "w\uD83D";
                t.Insert(caret, s);
                caret += s.Length;
                if (s.EndsWith('\uD83D'))
                {
                    t.Insert(caret, "\uDE00");
                    caret++;
                }
            }
            else if (r < 34 && t.Length > 0)
            {
                caret = Math.Min(caret, t.Length);
                if (caret > 0)
                {
                    caret--;
                    t.Delete(caret, 1);
                }
            }
            else if (r < 40)
            {
                caret = t.Length;
                t.Insert(caret, "e");
                caret++;
            }
            else if (r < 46)
            {
                caret = rng.Int(t.Length + 1);
                t.Insert(caret, rng.Chance(0.5) ? "ab" : "q");
                caret += 1;
            }
            else if (r < 51 && t.Length > 0)
            {
                var at = rng.Int(t.Length);
                var len = Math.Min(1 + rng.Int(4), t.Length - at);
                t.Delete(at, len);
                caret = at;
            }
            else if (r < 62)
            {
                a.Push(Vals(rng.Int(1000)));
            }
            else if (r < 68)
            {
                slot = Math.Min(slot, a.Length);
                a.Insert(slot, Vals(rng.Int(1000), "s"));
                slot += 2;
            }
            else if (r < 71)
            {
                slot = rng.Int(a.Length + 1);
                a.Insert(slot, Vals(rng.Int(10)));
                slot++;
            }
            else if (r < 76 && a.Length > 0)
            {
                var at = rng.Int(a.Length);
                a.Delete(at, Math.Min(1 + rng.Int(3), a.Length - at));
                slot = Math.Min(slot, a.Length);
            }
            else if (r < 78)
            {
                if (reactor != null && rng.Chance(0.5))
                {
                    armed = 1 + rng.Int(4);
                }

                m.Set(rng.Chance(0.5) ? "k" : "j", rng.Int(10));
            }
            else if (r < 80)
            {
                if (nt == null || rng.Chance(0.1))
                {
                    nt = new YText(rng.Chance(0.5) ? "pre" : "");
                    m.Set("nt", nt);
                }
                else
                {
                    nt.Insert(nt.Length, "n");
                    nt.Insert(nt.Length, "m");
                }
            }
            else if (r < 82)
            {
                if (na == null || rng.Chance(0.1))
                {
                    na = new YArray();
                    a.Insert(rng.Int(a.Length + 1), Vals(na));
                }
                else
                {
                    na.Push(Vals(1));
                    na.Push(Vals(2, 3));
                }
            }
            else if (r < 83 && t.Length > 0)
            {
                var at = rng.Int(t.Length);
                t.Format(at, Math.Min(1 + rng.Int(3), t.Length - at), bold);
            }
            else if (r < 84)
            {
                caret = Math.Min(caret, t.Length);
                t.Insert(caret, "B", bold);
                caret++;
                t.Insert(caret, "C", bold);
                caret++;
            }
            else if (r < 85)
            {
                a.Push(Vals(new byte[] { 1, 2 }));
            }
            else if (r < 87)
            {
                relpos.Add(RelativePosition.FromIndex(t, rng.Int(t.Length + 1), rng.Chance(0.5) ? 0 : -1));
                relpos.Add(RelativePosition.FromIndex(a, rng.Int(a.Length + 1), rng.Chance(0.5) ? 0 : -1));
                c.L("rp " + ReferenceRun.Hex(relpos[^2].Encode()) + " " + ReferenceRun.Hex(relpos[^1].Encode()));
            }
            else if (r < 89)
            {
                c.State(doc, inTxn ? "mid" : "st");
            }
            else if (r < 91)
            {
                c.L("diff " + ReferenceRun.Hex(doc.EncodeStateAsUpdateV1(peer.EncodeStateVectorV1())) + " " + ReferenceRun.Hex(doc.EncodeStateAsUpdateV2(peer.EncodeStateVectorV1())));
            }
            else if (r < 93)
            {
                if (reactor != null && rng.Chance(0.5))
                {
                    armed = 1 + rng.Int(4);
                }

                var u = RemoteDiff(rng.Chance(0.5));
                try
                {
                    doc.ApplyUpdate(u);
                }
                catch (Exception e)
                {
                    c.L("apply threw " + e.GetType().Name);
                }
            }
            else if (r < 96)
            {
                ToggleObserver(rng.Int(7));
            }
            else if (r < 97)
            {
                if (um == null)
                {
                    um = new UndoManager(new object[] { t, a }, new UndoManagerOptions { CaptureTimeout = rng.Chance(0.5) ? 0 : 100000 });
                    c.L("+um");
                }
                else if (rng.Chance(0.3))
                {
                    um.Destroy();
                    um = null;
                    c.L("-um");
                }
                else
                {
                    um.StopCapturing();
                }
            }
            else if (r < 99 && um != null)
            {
                var si = rng.Chance(0.6) ? um.Undo() : um.Redo();
                c.L("undo/redo " + (si != null) + " " + um.UndoStack.Count + "/" + um.RedoStack.Count);
                caret = Math.Min(caret, t.Length);
            }
            else if (r < 99 && !gc)
            {
                snapshots.Add(doc.CreateSnapshot());
                c.L("snap " + ReferenceRun.Hex(snapshots[^1].EncodeSnapshot()));
            }
            else if (!gc)
            {
                ReadSnapshot();
            }
            else
            {
                c.L("len " + t.Length + " " + a.Length + " " + ReferenceRun.Esc(t.ToString()).Length);
            }
        }

        var steps = 10 + rng.Int(40);
        for (var s = 0; s < steps; s++)
        {
            var r = rng.Int(100);
            try
            {
                if (r < 40)
                {
                    var k = 2 + rng.Int(rng.Chance(0.2) ? 120 : 25);
                    var boom = rng.Chance(0.05);
                    throwInHandler = rng.Chance(0.05);
                    doc.Transact(
                        tr =>
                        {
                            for (var i = 0; i < k; i++)
                            {
                                Op(true);
                            }

                            if (boom)
                            {
                                throw new InvalidOperationException("boom");
                            }
                        },
                        rng.Chance(0.1) ? "origin" : null);
                }
                else if (r < 75)
                {
                    Op(false);
                }
                else if (r < 85)
                {
                    if (rng.Chance(0.7))
                    {
                        SyncToPeer();
                    }

                    PeerEdit();
                }
                else if (r < 93)
                {
                    if (reactor != null && rng.Chance(0.5))
                    {
                        armed = 1 + rng.Int(4);
                    }

                    doc.ApplyUpdateV1(RemoteDiff(false));
                    SyncToPeer();
                }
                else
                {
                    ToggleObserver(rng.Int(7));
                }
            }
            catch (InvalidOperationException e) when (e.Message is "boom" or "handler")
            {
                c.L("caught " + e.Message);
            }
            catch (Exception e) when (e.Message == "Array length exceeded" || e.Message.StartsWith("Length exceeded", StringComparison.Ordinal))
            {
                c.L("caught " + e.Message);
            }

            throwInHandler = false;
            c.FixClientId(doc);
            c.L($"#{s} t={ReferenceRun.Esc(t.ToString())} a={ReferenceRun.V(a.ToArray())} n={(nt == null ? "-" : nt.ToString())}");
            c.State(doc, "st");
            if (a.Length != a.ToArray().Count || pa.Length != pa.ToArray().Count)
            {
                c.L($"length mismatch a {a.Length} vs {a.ToArray().Count}; pa {pa.Length} vs {pa.ToArray().Count}");
            }

            c.L(ReferenceRun.Dump(doc, t, a));
            foreach (var rp in relpos)
            {
                var found = rp.TryResolve(doc, out var abs);
                c.Log.Append(found ? abs.Index : null).Append(',');
            }

            c.L("");
        }

        armed = 0;
        SyncToPeer();
        doc.ApplyUpdateV1(RemoteDiff(false));
        c.State(doc, "final");
        c.State(peer, "peer");
        c.L(pt.ToString() == t.ToString() ? "converged" : "diverged");
    }
}
