using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// The windows in which a run extended in place (<see cref="Item.ExtendRun"/>) or the search markers could tell
/// Ycs from Yjs, each with what Yjs 13.6.32 gives (recorded with interop/markers/blockers.mjs): search markers left
/// stale by a remote change, a garbage collection filter, snapshot reads within a transaction, and a transaction
/// started from an observer.
/// </summary>
[TestClass]
public class RunExtensionYjsTests
{
    [TestMethod]
    public void Transact_UpdateAppliedBetweenIndexEdits_InsertsWhereYjsDoes()
    {
        // The update leaves the markers stale; Yjs' lookup walks left over the run first (findMarker).
        var (content, v1) = AppliedInsideTransaction(observed: true);

        Assert.AreEqual(("bcd", "010203000101017401840300036263640103010001"), (content, v1));
    }

    [TestMethod]
    public void ObserverOfAnotherType_EditsDuringRemoteCleanup_InsertsWhereYjsDoes()
    {
        var doc = Doc(1);
        var t = doc.GetText("t");
        t.EventHandler += (_, _) => { };
        var m = doc.GetMap("m");
        doc.Transact(_ =>
        {
            t.Insert(0, "a");
            t.Insert(1, "b");
            t.Delete(1, 1);
            t.Insert(1, "c");
            t.Insert(2, "d");
            t.Delete(2, 1);
        });
        var peer = Doc(2);
        Sync(doc, peer);
        var pt = peer.GetText("t");
        peer.GetMap("m").Set("k", 1);
        pt.Insert(2, "e");
        pt.Delete(0, 1);
        m.EventHandler += (_, _) => t.Insert(2, "f");

        Sync(peer, doc);

        Assert.AreEqual(("cef", "020202002801016d016b017d0184010301650401000101017402840101016381010201840201016601010200020301"), (t.ToString(), Hex(doc.EncodeStateAsUpdateV1())));
    }

    [TestMethod]
    public void Transact_InsertAfterAppliedUpdateMovesMarkerOffDeletedItem_LandsOnYjsItem()
    {
        // The marker on the deleted item moves to the countable item left of it: in Yjs the last of the run's items.
        var doc = Doc(3);
        var a = doc.GetArray("t");
        var peer = Doc(2);
        var pa = peer.GetArray("t");
        a.Insert(0, new object[] { 0 });
        a.Delete(0, 1);
        a.Insert(0, new object[] { 1 });
        Sync(doc, peer);
        pa.Delete(0, 1);

        doc.Transact(_ =>
        {
            a.Insert(1, new object[] { 2 });
            a.Insert(2, new object[] { 3 });
            Sync(peer, doc);
            a.Insert(1, new object[] { 4 });
        });

        Assert.AreEqual(("4,2,3", "01040300010101740141030001c803010300027d027d03480301017d040103010002"), (string.Join(",", a.ToArray()), Hex(doc.EncodeStateAsUpdateV1())));
    }

    [TestMethod]
    public void GcFilter_RunTypedAndDeletedInOneTransaction_SeesEachYjsItem()
    {
        var (calls, updates, v1) = Filtered(item => item.Id.Clock != 1, doc =>
        {
            var t = doc.GetText("t");
            doc.Transact(_ =>
            {
                t.Insert(0, "a");
                t.Insert(1, "b");
                t.Delete(0, 2);
            });
        });

        Assert.AreEqual(("0+1,1+1", "01020100010101740184010001620101010002", "01020100010101740184010001620101010002"), (calls, updates, v1));
    }

    [TestMethod]
    public void GcFilter_RunContinuedAndDeletedInALaterTransaction_SeesEachYjsItem()
    {
        var (calls, updates, v1) = Filtered(item => item.Id.Clock != 1, doc =>
        {
            var t = doc.GetText("t");
            t.Insert(0, "a");
            doc.Transact(_ =>
            {
                t.Insert(1, "b");
                t.Delete(0, 2);
            });
        });

        Assert.AreEqual(("0+1,1+1", "0101010004010174016100,0101010184010001620101010002", "01020100010101740184010001620101010002"), (calls, updates, v1));
    }

    [TestMethod]
    public void GcFilter_ArrayPushesDeletedInOneTransaction_SeesEachYjsItem()
    {
        var (calls, updates, v1) = Filtered(item => item.Id.Clock % 2 == 0, doc =>
        {
            var a = doc.GetArray("a");
            doc.Transact(_ =>
            {
                a.Push(new object[] { 1 });
                a.Push(new object[] { 2 });
                a.Push(new object[] { 3 });
                a.Delete(0, 3);
            });
        });

        Assert.AreEqual(("0+1,1+1,2+1", "010301000101016101880100017d02810101010101010003", "010301000101016101880100017d02810101010101010003"), (calls, updates, v1));
    }

    private static (string Content, string V1) AppliedInsideTransaction(bool observed)
    {
        var doc = Doc(3);
        var t = doc.GetText("t");
        if (observed)
        {
            t.EventHandler += (_, _) => { };
        }

        var peer = Doc(2);
        var pt = peer.GetText("t");
        t.Insert(0, "a");
        Sync(doc, peer);
        pt.Delete(0, 1);

        doc.Transact(_ =>
        {
            t.Insert(1, "b");
            t.Insert(2, "c");
            Sync(peer, doc);
            t.Insert(2, "d");
        });

        return (t.ToString(), Hex(doc.EncodeStateAsUpdateV1()));
    }

    private static (string Calls, string Updates, string V1) Filtered(Func<Item, bool> keep, Action<YDoc> body)
    {
        var calls = new List<string>();
        var doc = new YDoc(new YDocOptions
        {
            ClientId = 1,
            GcFilter = item =>
            {
                calls.Add($"{item.Id.Clock}+{item.Length}");
                return keep(item);
            },
        });
        var updates = new List<string>();
        doc.Update += (_, e) => updates.Add(Hex(e.Item1));

        body(doc);

        return (string.Join(",", calls), string.Join(",", updates), Hex(doc.EncodeStateAsUpdateV1()));
    }

    private static YDoc Doc(long client) => new(new YDocOptions { ClientId = client });

    private static void Sync(YDoc from, YDoc to) => to.ApplyUpdateV1(from.EncodeStateAsUpdateV1(to.EncodeStateVectorV1()));

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    // Each op as its insert, and its ychange type after a colon.
    private static string Describe(IList<Delta> delta) =>
        string.Join("|", delta.Select(d => d.Insert + (d.Attributes != null && d.Attributes.TryGetValue("ychange", out var c) ? ":" + ((YText.YTextChangeAttributes)c).Type : "")));
}
