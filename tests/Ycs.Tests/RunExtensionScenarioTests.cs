using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Hand-written scenarios of the windows a run extended in place could show in (observers and handlers attached
/// within a transaction, throwing bodies and handlers, updates applied and encodings made within a transaction,
/// undo, surrogates, snapshots, relative positions, prelim types, a parent deleted while typing, garbage collection
/// filters, transactions started from observers): Ycs and its reference (<see cref="ReferenceRun"/>) log the same.
/// </summary>
[TestClass]
public class RunExtensionScenarioTests
{
    private static readonly List<(string Name, Action<ReferenceRun> Run)> All = new();

    static RunExtensionScenarioTests()
    {
        Add("s01-gcfilter-by-id-same-txn", c =>
        {
            var doc = c.NewDoc(1, gc: true, gcFilter: item => { c.L($"gcFilter {item.Id.Clock}+{item.Length}"); return item.Id.Clock != 1; });
            var t = doc.GetText("t");
            c.LogUpdates(doc, "d");
            doc.Transact(tr => { t.Insert(0, "a"); t.Insert(1, "b"); t.Remove(0, 2); });
            c.State(doc, "end"); c.L(ReferenceRun.Dump(doc));
        });
        Add("s01b-gcfilter-cross-txn", c =>
        {
            var doc = c.NewDoc(1, gc: true, gcFilter: item => { c.L($"gcFilter {item.Id.Clock}+{item.Length}"); return item.Id.Clock != 1; });
            var t = doc.GetText("t");
            c.LogUpdates(doc, "d");
            t.Insert(0, "a");
            doc.Transact(tr => { t.Insert(1, "b"); t.Remove(0, 2); });
            c.State(doc, "end"); c.L(ReferenceRun.Dump(doc));
        });
        Add("s01c-gcfilter-array", c =>
        {
            var doc = c.NewDoc(1, gc: true, gcFilter: item => { c.L($"gcFilter {item.Id.Clock}+{item.Length}"); return item.Id.Clock % 2 == 0; });
            var a = doc.GetArray("a");
            c.LogUpdates(doc, "d");
            doc.Transact(tr => { a.Add(Vals(1).Select(YValue.FromObject).ToArray()); a.Add(Vals(2).Select(YValue.FromObject).ToArray()); a.Add(Vals(3).Select(YValue.FromObject).ToArray()); a.RemoveRange(0, 3); });
            c.State(doc, "end"); c.L(ReferenceRun.Dump(doc));
        });
        Add("s02-reentrant-observer-then-observe", c =>
        {
            var doc = c.NewDoc(1);
            var x = doc.GetText("x"); var y = doc.GetText("y");
            c.LogUpdates(doc, "d");
            var fired = false;
            x.Observe(e =>
            {
                c.L("x observer " + ReferenceRun.D(e.Delta));
                if (fired) return;
                fired = true;
                y.Insert(y.Length, "c");
                y.Observe(c.TextObserver("y"));
            });
            doc.Transact(tr => { x.Insert(0, "x"); y.Insert(0, "b"); });
            c.State(doc, "end"); c.L(ReferenceRun.Dump(doc));
        });
        Add("s02b-reentrant-observer-two-types", c =>
        {
            var doc = c.NewDoc(1);
            var x = doc.GetText("x"); var y = doc.GetText("y"); var m = doc.GetMap("m");
            c.LogUpdates(doc, "d");
            var fired = false;
            x.Observe(e =>
            {
                c.L("x observer " + ReferenceRun.D(e.Delta));
                if (fired) return;
                fired = true;
                doc.Transact(tr => { y.Insert(y.Length, "c"); y.Insert(y.Length, "d"); m.ObserveDeep(c.DeepObserver("m")); m["k"] = 1; });
            });
            doc.Transact(tr => { x.Insert(0, "x"); y.Insert(0, "b"); });
            c.State(doc, "end"); c.L(ReferenceRun.Dump(doc));
        });
        Add("s02c-reentrant-aftertransaction-late", c =>
        {
            var doc = c.NewDoc(1);
            var x = doc.GetText("x"); var y = doc.GetText("y");
            c.LogUpdates(doc, "d");
            var fired = false;
            x.Observe(_ =>
            {
                if (fired) return;
                fired = true;
                y.Insert(y.Length, "c");
                doc.AfterTransaction += (_, tr) =>
                {
                    foreach (var (type, evs) in tr.ChangedParentTypes) foreach (var ev in evs) c.L("atx event " + ReferenceRun.D(ev.Delta));
                };
            });
            doc.Transact(tr => { x.Insert(0, "x"); y.Insert(0, "b"); });
            c.State(doc, "end"); c.L(ReferenceRun.Dump(doc));
        });
        Add("s29-toarraysnapshot-inside", c =>
        {
            var doc = c.NewDoc(1, gc: false); var a = doc.GetArray("a");
            a.Add(Vals(1, 2).Select(YValue.FromObject).ToArray());
            doc.Transact(tr =>
            {
                a.Add(Vals(3).Select(YValue.FromObject).ToArray());
                var sm = doc.CreateSnapshot();
                a.Add(Vals(4).Select(YValue.FromObject).ToArray()); a.Add(Vals(5).Select(YValue.FromObject).ToArray());
                c.L("inside " + ReferenceRun.V(a.ToArray(sm).Select(v => v.ToObject()).ToList()));
            });
        });
        Add("s02d-reentrant-array-added", c =>
        {
            var doc = c.NewDoc(1);
            var x = doc.GetText("x"); var y = doc.GetArray("y");
            var fired = false;
            x.Observe(_ =>
            {
                if (fired) return;
                fired = true;
                y.Add(Vals(2).Select(YValue.FromObject).ToArray());
                y.Observe(e2 => c.L("y delta=" + ReferenceRun.D(e2.Delta) + " added=" + string.Join(",", e2.Changes.Added.Select(i => i.Id.Clock + "+" + i.Length).OrderBy(v => v))));
            });
            doc.Transact(tr => { x.Insert(0, "x"); y.Add(Vals(1).Select(YValue.FromObject).ToArray()); });
        });
        Add("s30-xml-fragment-values", c =>
        {
            var doc = c.NewDoc(1); var m = doc.GetMap("m");
            c.LogUpdates(doc, "d");
            var a = new YArray(); m["a"] = a;
            doc.Transact(tr => { a.Add(Vals(1).Select(YValue.FromObject).ToArray()); a.Add(Vals(2).Select(YValue.FromObject).ToArray()); m["a"] = new YArray(); a.Add(Vals(3).Select(YValue.FromObject).ToArray()); });
            doc.Transact(tr => { var b = (YArray)m["a"].ToObject(); b.Add(Vals(1).Select(YValue.FromObject).ToArray()); b.Add(Vals(2).Select(YValue.FromObject).ToArray()); m.Observe(e => c.L("m " + string.Join(",", e.Keys.Keys))); b.Add(Vals(3).Select(YValue.FromObject).ToArray()); m.Remove("a"); });
            c.State(doc, "end"); c.L(ReferenceRun.Dump(doc));
        });
        Add("s03-observe-mid-txn", c =>
        {
            var doc = c.NewDoc(1);
            var t = doc.GetText("t");
            c.LogUpdates(doc, "d");
            t.Insert(0, "ab");
            doc.Transact(tr =>
            {
                t.Insert(2, "c"); t.Insert(3, "d"); t.Insert(4, "e");
                t.Observe(c.TextObserver("t"));
                t.Insert(5, "f"); t.Remove(3, 1);
            });
            c.State(doc, "end"); c.L(ReferenceRun.Dump(doc));
        });
        Add("s04-deep-observe-mid-txn-nested", c =>
        {
            var doc = c.NewDoc(1);
            var m = doc.GetMap("m");
            var t = new YText(); var a = new YArray();
            m["t"] = t; m["a"] = a;
            c.LogUpdates(doc, "d");
            t.Insert(0, "ab"); a.Add(Vals(1, 2).Select(YValue.FromObject).ToArray());
            doc.Transact(tr =>
            {
                t.Insert(2, "c"); t.Insert(3, "d");
                a.Add(Vals(3).Select(YValue.FromObject).ToArray()); a.Add(Vals(4).Select(YValue.FromObject).ToArray()); a.Insert(4, Vals(5).Select(YValue.FromObject).ToArray());
                m.ObserveDeep(c.DeepObserver("m"));
                t.Insert(4, "e"); a.Add(Vals(6).Select(YValue.FromObject).ToArray());
            });
            c.State(doc, "end"); c.L(ReferenceRun.Dump(doc));
        });
        Add("s05-body-throws", c =>
        {
            var doc = c.NewDoc(1);
            var t = doc.GetText("t");
            c.LogUpdates(doc, "d");
            t.Insert(0, "ab");
            try { doc.Transact((Action<Transaction>)(tr => { t.Insert(2, "c"); t.Insert(3, "d"); throw new InvalidOperationException("boom"); })); } catch (InvalidOperationException) { c.L("caught"); }
            c.L(ReferenceRun.Dump(doc));
            t.Observe(c.TextObserver("t"));
            doc.Transact(tr => { t.Insert(4, "e"); t.Insert(5, "f"); });
            c.State(doc, "end"); c.L(ReferenceRun.Dump(doc));
        });
        Add("s06-apply-update-inside-transact", c =>
        {
            var peer = c.NewDoc(2); var pt = peer.GetText("t");
            var doc = c.NewDoc(1); var t = doc.GetText("t");
            t.Insert(0, "ab");
            peer.ApplyUpdate(doc.EncodeStateAsUpdate());
            c.LogUpdates(doc, "d");
            // concurrent inserts at each boundary of what doc will type
            var baseSv = peer.EncodeStateVector();
            doc.Transact(tr =>
            {
                t.Insert(2, "c"); t.Insert(3, "d"); t.Insert(4, "e");
            });
            // now peer learns the run and inserts inside it
            peer.ApplyUpdate(doc.EncodeStateAsUpdate(baseSv));
            pt.Insert(3, "X"); pt.Insert(5, "Y");
            var remote = peer.EncodeStateAsUpdate(doc.EncodeStateVector());
            doc.Transact(tr =>
            {
                t.Insert(5, "f"); t.Insert(6, "g");
                doc.ApplyUpdate(remote);
                c.L("mid " + t.ToString() + " local=" + tr.Local);
                t.Insert(t.Length, "h"); t.Insert(t.Length, "i");
            });
            c.L(t.ToString());
            c.State(doc, "end"); c.L(ReferenceRun.Dump(doc));
        });
        Add("s07-encode-inside-transact", c =>
        {
            var doc = c.NewDoc(1); var t = doc.GetText("t"); var a = doc.GetArray("a");
            c.LogUpdates(doc, "d");
            t.Insert(0, "ab"); a.Add(Vals(1).Select(YValue.FromObject).ToArray());
            var sv0 = doc.EncodeStateVector();
            doc.Transact(tr =>
            {
                t.Insert(2, "c"); t.Insert(3, "d"); a.Add(Vals(2).Select(YValue.FromObject).ToArray()); a.Add(Vals(3).Select(YValue.FromObject).ToArray());
                c.State(doc, "mid1");
                c.L("diff " + ReferenceRun.Hex(doc.EncodeStateAsUpdate(sv0)) + " " + ReferenceRun.Hex(doc.EncodeStateAsUpdate(sv0, format: UpdateFormat.V2)));
                var w = new System.Buffers.ArrayBufferWriter<byte>(); doc.EncodeStateAsUpdate(w); c.L("w " + ReferenceRun.Hex(w.WrittenSpan.ToArray()));
                a.Add(Vals(4).Select(YValue.FromObject).ToArray()); t.Insert(4, "e"); t.Insert(5, "f");
                c.State(doc, "mid2");
                var ms = new System.Buffers.ArrayBufferWriter<byte>(); SyncProtocol.WriteSyncStep2(ms, doc, sv0); c.L("sync2 " + ReferenceRun.Hex(ms.WrittenSpan.ToArray()));
                t.Insert(6, "g");
            });
            c.State(doc, "end"); c.L(ReferenceRun.Dump(doc));
        });
        Add("s08-observer-throws", c =>
        {
            var doc = c.NewDoc(1); var t = doc.GetText("t"); var u = doc.GetText("u");
            c.LogUpdates(doc, "d");
            t.Insert(0, "ab");
            Action<YTextEvent> thrower = _ => throw new InvalidOperationException("handler");
            IDisposable thrown = null;
            try
            {
                doc.Transact(tr => { t.Insert(2, "c"); t.Insert(3, "d"); u.Insert(0, "x"); thrown = u.Observe(thrower); });
            }
            catch (InvalidOperationException) { c.L("caught"); }
            c.L(ReferenceRun.Dump(doc));
            thrown.Dispose();
            doc.Transact(tr => { t.Insert(4, "e"); t.Insert(5, "f"); });
            t.Observe(c.TextObserver("t"));
            doc.Transact(tr => { t.Insert(6, "g"); t.Insert(7, "h"); });
            c.State(doc, "end"); c.L(ReferenceRun.Dump(doc));
        });
        Add("s09-surrogates", c =>
        {
            var doc = c.NewDoc(1); var t = doc.GetText("t");
            c.LogUpdates(doc, "d");
            t.Insert(0, "x");
            doc.Transact(tr => { t.Insert(1, "\uD83D"); t.Insert(2, "\uDE00"); t.Insert(3, "y"); });
            c.L(ReferenceRun.Esc(t.ToString())); c.L(ReferenceRun.Dump(doc));
            doc.Transact(tr => { t.Insert(4, "a\uD83D"); t.Insert(6, "\uDE00b"); t.Observe(c.TextObserver("t")); });
            c.L(ReferenceRun.Esc(t.ToString())); c.L(ReferenceRun.Dump(doc));
            doc.Transact(tr => { t.Insert(8, "😀"); t.Insert(10, "😀"); t.Remove(9, 2); });
            c.L(ReferenceRun.Esc(t.ToString()));
            c.State(doc, "end"); c.L(ReferenceRun.Dump(doc));
        });
        Add("s09b-surrogates-unobserved-encode", c =>
        {
            var doc = c.NewDoc(1); var t = doc.GetText("t");
            c.LogUpdates(doc, "d");
            doc.Transact(tr => { t.Insert(0, "a"); t.Insert(1, "😀"); t.Insert(3, "\uD83D"); t.Insert(4, "\uDE00"); c.State(doc, "mid"); t.Insert(5, "\uD83D"); t.Insert(6, "z"); c.State(doc, "mid2"); });
            c.L(ReferenceRun.Esc(t.ToString()));
            c.State(doc, "end"); c.L(ReferenceRun.Dump(doc));
        });
        Add("s10-cross-txn-peer", c =>
        {
            var doc = c.NewDoc(1); var t = doc.GetText("t"); var a = doc.GetArray("a");
            var peer = c.NewDoc(2);
            doc.Updated += (_, e) => { c.L("u " + ReferenceRun.Hex(e.Update.ToArray())); peer.ApplyUpdate(e.Update.ToArray()); c.State(peer, "peer"); };
            t.Insert(0, "a"); t.Insert(1, "b"); a.Add(Vals(1).Select(YValue.FromObject).ToArray()); a.Add(Vals(2).Select(YValue.FromObject).ToArray()); t.Insert(2, "c");
            doc.Transact(tr => { t.Insert(3, "d"); t.Insert(4, "e"); });
            doc.Transact(tr => { t.Insert(5, "f"); a.Add(Vals(3).Select(YValue.FromObject).ToArray()); a.Add(Vals(4).Select(YValue.FromObject).ToArray()); a.Add(Vals(5, "s").Select(YValue.FromObject).ToArray()); });
            t.Insert(6, "g");
            c.State(doc, "end"); c.L(ReferenceRun.Dump(doc)); c.L(ReferenceRun.Dump(peer));
        });
        Add("s11-undo-manager-mid-txn", c =>
        {
            var doc = c.NewDoc(1); var t = doc.GetText("t");
            c.LogUpdates(doc, "d");
            t.Insert(0, "ab");
            UndoManager um = null;
            doc.Transact(tr => { t.Insert(2, "c"); t.Insert(3, "d"); um = new UndoManager(t); t.Insert(4, "e"); });
            c.L("stack " + um.UndoStack.Count);
            doc.Transact(tr => { t.Insert(5, "f"); t.Insert(6, "g"); });
            c.L("stack " + um.UndoStack.Count);
            um.Undo(); c.L(t.ToString()); c.L(ReferenceRun.Dump(doc));
            um.Undo(); c.L(t.ToString());
            um.Redo(); c.L(t.ToString());
            um.Dispose();
            doc.Transact(tr => { t.Insert(t.Length, "h"); t.Insert(t.Length, "i"); });
            c.State(doc, "end"); c.L(ReferenceRun.Dump(doc));
        });
        Add("s11b-undo-inside-transact", c =>
        {
            var doc = c.NewDoc(1); var t = doc.GetText("t"); var u = doc.GetText("u");
            c.LogUpdates(doc, "d");
            var um = new UndoManager(u);
            u.Insert(0, "uu");
            um.StopCapturing();
            um.Dispose(); // listener gone? keep a second manager unobserving
            var um2 = new UndoManager(u, new UndoManagerOptions { CaptureTimeout = TimeSpan.Zero });
            u.Insert(2, "v");
            c.L("can " + um2.CanUndo);
            c.State(doc, "end"); c.L(ReferenceRun.Dump(doc));
        });
        Add("s12-snapshot-todelta-inside", c =>
        {
            var doc = c.NewDoc(1, gc: false); var t = doc.GetText("t");
            c.LogUpdates(doc, "d");
            t.Insert(0, "ab");
            var s1 = doc.CreateSnapshot();
            Func<YChangeKind, ItemId, YChange> cy = (type, id) => { c.L($"ychange {type} {id.Client}:{id.Clock}"); return new YChange(type, id.Clock); };
            doc.Transact(tr =>
            {
                t.Insert(2, "c"); t.Insert(3, "d");
                var sm = doc.CreateSnapshot();
                c.L("sm " + ReferenceRun.Hex(sm.Encode()));
                t.Insert(4, "e"); t.Insert(5, "f"); t.Remove(1, 2);
                var s2 = doc.CreateSnapshot();
                c.L("s2 " + ReferenceRun.Hex(s2.Encode()) + " " + ReferenceRun.Hex(s2.Encode(UpdateFormat.V2)));
                c.L("d(s2,s1) " + ReferenceRun.D(t.ToDelta(s2, s1, cy)));
                c.L("d(sm,s1) " + ReferenceRun.D(t.ToDelta(sm, s1, cy)));
                c.L("d(s2,sm) " + ReferenceRun.D(t.ToDelta(s2, sm, cy)));
                t.Insert(t.Length, "g"); t.Insert(t.Length, "h");
                var restored = doc.CreateDocFromSnapshot(sm);
                c.L("restored " + restored.GetText("t").ToString() + " " + ReferenceRun.Hex(restored.EncodeStateAsUpdate()));
                t.Insert(t.Length, "i");
            });
            var s3 = doc.CreateSnapshot();
            c.L("d(s3,s1) " + ReferenceRun.D(t.ToDelta(s3, s1, cy)));
            c.State(doc, "end"); c.L(ReferenceRun.Dump(doc));
        });
        Add("s13-nonlocal-transact", c =>
        {
            var doc = c.NewDoc(1); var t = doc.GetText("t");
            c.LogUpdates(doc, "d");
            t.Insert(0, "ab");
            doc.Transact(tr => { t.Insert(2, "c"); t.Insert(3, "d"); }, null, false);
            c.L("client changed " + (doc.ClientId != 1));
            c.L(ReferenceRun.Dump(doc).Replace("c" + doc.ClientId, "cNEW"));
        });
        Add("s14-array-mix", c =>
        {
            var doc = c.NewDoc(1); var a = doc.GetArray("a");
            c.LogUpdates(doc, "d");
            a.Add(Vals(1, 2).Select(YValue.FromObject).ToArray());
            doc.Transact(tr =>
            {
                a.Add(Vals(3).Select(YValue.FromObject).ToArray()); a.Add(Vals(4, 5).Select(YValue.FromObject).ToArray()); a.Insert(5, Vals(6).Select(YValue.FromObject).ToArray()); a.Insert(2, Vals("m").Select(YValue.FromObject).ToArray()); a.Add(Vals(7).Select(YValue.FromObject).ToArray());
                a.Add(Vals(new byte[] { 1 }, 8, 9).Select(YValue.FromObject).ToArray()); a.Add(Vals(10).Select(YValue.FromObject).ToArray()); a.RemoveRange(4, 3); a.Add(Vals(11).Select(YValue.FromObject).ToArray()); a.Insert(0, Vals(0).Select(YValue.FromObject).ToArray()); a.Add(Vals(12).Select(YValue.FromObject).ToArray());
                a.Add(Vals(new YArray(), 13).Select(YValue.FromObject).ToArray()); a.Add(Vals(14).Select(YValue.FromObject).ToArray());
            });
            c.L(ReferenceRun.V(a.ToArray()));
            doc.Transact(tr => { a.Add(Vals(15).Select(YValue.FromObject).ToArray()); a.Observe(e => c.L("a delta " + ReferenceRun.D(e.Delta))); a.Add(Vals(16).Select(YValue.FromObject).ToArray()); ((YArray)a[a.Length - 4].ToObject()).Add(Vals(1).Select(YValue.FromObject).ToArray()); ((YArray)a[a.Length - 4].ToObject()).Add(Vals(2).Select(YValue.FromObject).ToArray()); });
            c.State(doc, "end"); c.L(ReferenceRun.Dump(doc));
        });
        Add("s15-xmltext", c =>
        {
            var doc = c.NewDoc(1); var f = doc.GetXmlFragment("f");
            c.LogUpdates(doc, "d");
            var xt = new YXmlText(); var el = new YXmlElement("p");
            f.Insert(0, Vals(el).Cast<YType>().ToArray()); el.Insert(0, Vals(xt).Cast<YType>().ToArray());
            doc.Transact(tr => { xt.Insert(0, "a"); xt.Insert(1, "b"); xt.Insert(2, "c", new Dictionary<string, YValue> { ["bold"] = true }); xt.Insert(3, "d", new Dictionary<string, YValue> { ["bold"] = true }); xt.Insert(4, "e"); xt.Insert(5, "f"); });
            c.L(f.ToString());
            doc.Transact(tr => { xt.Insert(4, "x", new Dictionary<string, YValue> { ["bold"] = true }); xt.Insert(5, "y", new Dictionary<string, YValue> { ["bold"] = true }); f.ObserveDeep(c.DeepObserver("f")); });
            c.L(f.ToString());
            c.State(doc, "end"); c.L(ReferenceRun.Dump(doc));
        });
        Add("s16-delete-part-reinsert", c =>
        {
            var doc = c.NewDoc(1); var t = doc.GetText("t");
            c.LogUpdates(doc, "d");
            t.Insert(0, "ab");
            doc.Transact(tr => { t.Insert(2, "c"); t.Insert(3, "d"); t.Insert(4, "e"); t.Remove(3, 1); t.Insert(3, "D"); t.Insert(4, "E"); t.Remove(0, 3); t.Insert(t.Length, "z"); t.Insert(t.Length, "y"); t.Remove(t.Length - 1, 1); t.Insert(t.Length, "w"); });
            c.L(t.ToString());
            c.State(doc, "end"); c.L(ReferenceRun.Dump(doc));
        });
        Add("s17-relpos-inside", c =>
        {
            var doc = c.NewDoc(1); var t = doc.GetText("t");
            t.Insert(0, "ab");
            var rs = new List<RelativePosition>();
            doc.Transact(tr =>
            {
                t.Insert(2, "c"); t.Insert(3, "d"); t.Insert(4, "e");
                for (var i = 0; i <= t.Length; i++) { rs.Add(RelativePosition.FromIndex(t, i, 0)); rs.Add(RelativePosition.FromIndex(t, i, -1)); }
                t.Insert(5, "f"); t.Remove(3, 1);
            });
            foreach (var r in rs) { var found = r.TryResolve(doc, out var abs); c.L(ReferenceRun.Hex(r.Encode()) + " -> " + (found ? abs.Index : null)); }
        });
        Add("s18-listeners-mid-txn", c =>
        {
            var doc = c.NewDoc(1); var t = doc.GetText("t");
            c.LogUpdates(doc, "d");
            t.Insert(0, "ab");
            doc.Transact(tr =>
            {
                t.Insert(2, "c"); t.Insert(3, "d");
                doc.BeforeObserverCalls += (_, tr2) => c.L("boc " + ReferenceRun.Dump(doc));
            });
            doc.Transact(tr => { t.Insert(4, "e"); t.Insert(5, "f"); });
            c.State(doc, "end"); c.L(ReferenceRun.Dump(doc));
        });
        Add("s18b-aftertransaction-mid-txn", c =>
        {
            var doc = c.NewDoc(1); var t = doc.GetText("t");
            c.LogUpdates(doc, "d");
            t.Insert(0, "ab");
            doc.Transact(tr =>
            {
                t.Insert(2, "c"); t.Insert(3, "d");
                doc.AfterTransaction += (_, tr2) => { c.L("atx " + ReferenceRun.Dump(doc)); foreach (var (ty, evs) in tr2.ChangedParentTypes) foreach (var ev in evs) c.L("atx ev " + ReferenceRun.D(ev.Delta) + " added " + string.Join(",", ev.Changes.Added.Select(i => i.Id.Clock + "+" + i.Length).OrderBy(x => x))); };
            });
            c.State(doc, "end"); c.L(ReferenceRun.Dump(doc));
        });
        Add("s18c-aftertransactioncleanup-only", c =>
        {
            var doc = c.NewDoc(1); var t = doc.GetText("t");
            c.LogUpdates(doc, "d");
            t.Insert(0, "ab");
            doc.Transact(tr =>
            {
                t.Insert(2, "c"); t.Insert(3, "d");
                doc.AfterTransactionCleanup += (_, tr2) => { c.L("atc " + ReferenceRun.Dump(doc)); };
                doc.AfterAllTransactions += (_, trs) => { c.L("aat " + trs.Count); };
            });
            c.State(doc, "end"); c.L(ReferenceRun.Dump(doc));
        });
        Add("s19-nested-created-same-txn", c =>
        {
            var doc = c.NewDoc(1); var m = doc.GetMap("m");
            c.LogUpdates(doc, "d");
            m.Observe(e => c.L("m keys " + string.Join(",", e.Keys.Keys)));
            doc.Transact(tr =>
            {
                var t = new YText("pre"); m["t"] = t;
                t.Insert(3, "a"); t.Insert(4, "b"); t.Observe(c.TextObserver("t")); t.Insert(5, "c");
                var a = new YArray(1, 2); m["a"] = a; a.Add(Vals(3).Select(YValue.FromObject).ToArray()); a.Add(Vals(4).Select(YValue.FromObject).ToArray());
            });
            c.State(doc, "end"); c.L(ReferenceRun.Dump(doc));
        });
        Add("s20-delete-parent-while-typing", c =>
        {
            var doc = c.NewDoc(1); var m = doc.GetMap("m");
            c.LogUpdates(doc, "d");
            var t = new YText(); m["t"] = t; t.Insert(0, "ab");
            doc.Transact(tr => { t.Insert(2, "c"); t.Insert(3, "d"); m.Remove("t"); t.Insert(4, "e"); t.Insert(5, "f"); });
            c.State(doc, "end"); c.L(ReferenceRun.Dump(doc));
            var t2 = new YText(); m["t"] = t2;
            doc.Transact(tr => { t2.Insert(0, "c"); t2.Insert(1, "d"); m["t"] = 5; });
            c.State(doc, "end2"); c.L(ReferenceRun.Dump(doc));
        });
        Add("s23-reentrant-typing-onto-outer", c =>
        {
            var doc = c.NewDoc(1); var x = doc.GetText("x"); var y = doc.GetText("y");
            c.LogUpdates(doc, "d");
            var n = 0;
            x.Observe(e =>
            {
                if (n++ > 1) return;
                doc.Transact(tr => { y.Insert(y.Length, "c"); y.Insert(y.Length, "d"); x.Insert(0, "q"); y.Insert(y.Length, "e"); });
            });
            y.Insert(0, "a");
            doc.Transact(tr => { x.Insert(0, "x"); y.Insert(1, "b"); });
            c.State(doc, "end"); c.L(ReferenceRun.Dump(doc));
        });
        Add("s24-format-between", c =>
        {
            var doc = c.NewDoc(1); var t = doc.GetText("t");
            c.LogUpdates(doc, "d");
            var bold = new Dictionary<string, YValue> { ["bold"] = true };
            doc.Transact(tr => { t.Insert(0, "a"); t.Insert(1, "b"); t.Insert(2, "c", bold); t.Insert(3, "d", bold); t.Insert(4, "e"); t.Insert(5, "f", new Dictionary<string, YValue>()); t.Format(1, 3, new Dictionary<string, YValue> { ["i"] = 1 }); t.Insert(6, "g"); t.Insert(3, "X"); t.Insert(4, "Y"); });
            c.L(ReferenceRun.D(t.ToDelta()));
            doc.Transact(tr => { t.InsertEmbed(2, YValue.FromObject(new Dictionary<string, object> { ["img"] = "u" })); t.Insert(3, "p"); t.Insert(4, "q"); t.ApplyDelta(new List<Delta> { new Delta.Retain(1), new Delta.Insert("r"), new Delta.Insert("s"), new Delta.Retain(2), new Delta.Insert("t") { Attributes = bold }, new Delta.Insert("u") { Attributes = bold }, new Delta.Delete(1), new Delta.Insert("v") }); });
            c.L(ReferenceRun.D(t.ToDelta()));
            c.State(doc, "end"); c.L(ReferenceRun.Dump(doc));
        });
        Add("s26-partly-applied-inside-transact", c =>
        {
            var peer = c.NewDoc(2); var pt = peer.GetText("t");
            var doc = c.NewDoc(1); var t = doc.GetText("t");
            t.Insert(0, "ab");
            peer.ApplyUpdate(doc.EncodeStateAsUpdate());
            for (var i = 0; i < 30; i++) pt.Insert(1, "p", i % 2 == 0 ? new Dictionary<string, YValue> { ["b"] = i } : null);
            for (var i = 0; i < 30; i++) t.Insert(1, "q", i % 2 == 0 ? new Dictionary<string, YValue> { ["c"] = i } : null);
            var remote = peer.EncodeStateAsUpdate(doc.EncodeStateVector());
            c.LogUpdates(doc, "d");
            try
            {
                doc.Transact(tr =>
                {
                    t.Insert(t.Length, "c"); t.Insert(t.Length, "d");
                    try { doc.ApplyUpdate(remote, null, limits: new UpdateLimits { MaxIntegrationSteps = 3 }); c.L("applied"); }
                    catch (YUpdateException e) { c.L("yue partly=" + e.PartlyApplied); }
                    t.Insert(t.Length, "e"); t.Insert(t.Length, "f");
                });
            }
            catch (Exception e) { c.L("outer " + e.GetType().Name); }
            c.L(t.ToString());
            c.State(doc, "end"); c.L(ReferenceRun.Dump(doc));
        });
        Add("s27-changed-set", c =>
        {
            var doc = c.NewDoc(1); var m = doc.GetMap("m"); var t = doc.GetText("t");
            doc.AfterTransaction += (_, tr) =>
            {
                foreach (var (ty, keys) in tr._changed)
                {
                    c.L($"changed {ty.GetType().Name} [{string.Join(",", keys.Select(k => k ?? "null"))}] ro={keys.IsReadOnly} count={keys.Count} contains={keys.Contains("k")}");
                    try { keys.Add("zz"); c.L("add ok"); } catch (Exception e) { c.L("add threw " + e.GetType().Name); }
                }
            };
            m["k"] = 1;
            doc.Transact(tr => { m["k"] = 2; m["j"] = 2; });
            t.Insert(0, "a");
        });
        Add("s28-gc-delete-run-then-type", c =>
        {
            var doc = c.NewDoc(1); var t = doc.GetText("t");
            c.LogUpdates(doc, "d");
            doc.Transact(tr => { t.Insert(0, "a"); t.Insert(1, "b"); t.Insert(2, "c"); });
            doc.Transact(tr => { t.Insert(3, "d"); t.Remove(1, 3); t.Insert(1, "e"); t.Insert(2, "f"); });
            doc.Transact(tr => { t.Remove(0, t.Length); t.Insert(0, "g"); t.Insert(1, "h"); });
            c.L(t.ToString());
            c.State(doc, "end"); c.L(ReferenceRun.Dump(doc));
        });
    }

    public static IEnumerable<object[]> Names => All.Select(s => new object[] { s.Name });

    [TestMethod]
    [DynamicData(nameof(Names))]
    public void Scenario_CandidateAndReference_LogTheSame(string name)
    {
        var scenario = All.Single(s => s.Name == name).Run;

        var difference = ReferenceRun.FirstDifference(scenario);

        Assert.IsNull(difference, difference);
    }

    private static void Add(string name, Action<ReferenceRun> run) => All.Add((name, run));

    private static object[] Vals(params object[] v) => v;
}
