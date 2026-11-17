using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// The windows in which a run extended in place (<see cref="ItemRef.ExtendRun"/>) or the search markers could tell
/// Ycs from Yjs, each with what Yjs 13.6.32 gives (recorded with interop/markers/blockers.mjs): search markers left
/// stale by a remote change (also one beyond the index looked up), a garbage collection filter, snapshot reads within
/// a transaction, and a transaction started from an observer.
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
        t.Observe(_ => { });
        var m = doc.GetMap("m");
        doc.Transact(_ =>
        {
            t.Insert(0, "a");
            t.Insert(1, "b");
            t.Remove(1, 1);
            t.Insert(1, "c");
            t.Insert(2, "d");
            t.Remove(2, 1);
        });
        var peer = Doc(2);
        Sync(doc, peer);
        var pt = peer.GetText("t");
        peer.GetMap("m")["k"] = 1;
        pt.Insert(2, "e");
        pt.Remove(0, 1);
        m.Observe(_ => t.Insert(2, "f"));

        Sync(peer, doc);

        Assert.AreEqual(("cef", "020202002801016d016b017d0184010301650401000101017402840101016381010201840201016601010200020301"), (t.ToString(), Hex(doc.EncodeStateAsUpdate())));
    }

    [TestMethod]
    public void Transact_InsertAfterAppliedUpdateMovesMarkerOffDeletedItem_LandsOnYjsItem()
    {
        // The marker on the deleted item moves to the countable item left of it: in Yjs the last of the run's items.
        var doc = Doc(3);
        var a = doc.GetArray("t");
        var peer = Doc(2);
        var pa = peer.GetArray("t");
        a.Insert(0, 0);
        a.RemoveRange(0, 1);
        a.Insert(0, 1);
        Sync(doc, peer);
        pa.RemoveRange(0, 1);

        doc.Transact(_ =>
        {
            a.Insert(1, 2);
            a.Insert(2, 3);
            Sync(peer, doc);
            a.Insert(1, 4);
        });

        Assert.AreEqual(("4,2,3", "01040300010101740141030001c803010300027d027d03480301017d040103010002"), (string.Join(",", a.ToArray()), Hex(doc.EncodeStateAsUpdate())));
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
                t.Remove(0, 2);
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
                t.Remove(0, 2);
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
                a.Add(1);
                a.Add(2);
                a.Add(3);
                a.RemoveRange(0, 3);
            });
        });

        Assert.AreEqual(("0+1,1+1,2+1", "010301000101016101880100017d02810101010101010003", "010301000101016101880100017d02810101010101010003"), (calls, updates, v1));
    }

    [TestMethod]
    public void ToArraySnapshot_TakenAndReadWithinATransaction_ShowsWhatTheSnapshotHolds()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1, GarbageCollection = false });
        var a = doc.GetArray("a");
        a.Add(1, 2);
        IList<object> read = null;

        doc.Transact(_ =>
        {
            a.Add(3);
            var snapshot = doc.CreateSnapshot();
            a.Add(4);
            a.Add(5);
            read = a.ToArray(snapshot).Select(v => v.ToObject()).ToList();
        });

        Assert.AreEqual("1,2,3", string.Join(",", read));
    }

    [TestMethod]
    public void ToDelta_SnapshotsWithinATransaction_ChangesPerYjsItem()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1, GarbageCollection = false });
        var t = doc.GetText("t");
        t.Insert(0, "ab");
        var s1 = doc.CreateSnapshot();
        var calls = new List<string>();
        string withChanges = null;
        string plain = null;

        doc.Transact(_ =>
        {
            t.Insert(2, "c");
            t.Insert(3, "d");
            t.Insert(4, "e");
            t.Insert(5, "f");
            t.Remove(1, 2);
            var s2 = doc.CreateSnapshot();

            // A user that never equals the client, as Yjs' 'u' + client: every item is a change of its own.
            withChanges = Describe(t.ToDelta(s2, s1, (type, id) =>
            {
                calls.Add($"{type} {id.Client}:{id.Clock}");
                return new YChange(type, null);
            }));
            plain = Describe(t.ToDelta(s2, s1));
        });

        Assert.AreEqual(
            ("Removed 1:1,Added 1:3,Added 1:4,Added 1:5", "a|b:Removed|d:Added|e:Added|f:Added", "a|b:Removed|d:Added|e:Added|f:Added"),
            (string.Join(",", calls), withChanges, plain));
    }

    [TestMethod]
    public void Observer_StartsTransactionTypingOntoTheOuterOne_EventsAsYjs()
    {
        // The outer transaction's merge takes the inner item in before the inner transaction's events are read.
        var doc = Doc(1);
        var x = doc.GetText("x");
        var y = doc.GetText("y");
        var deltas = new List<string>();
        var fired = false;
        x.Observe(_ =>
        {
            if (fired)
            {
                return;
            }

            fired = true;
            y.Insert(y.Length, "c");
            y.Observe(e => deltas.Add(Describe(e.Delta)));
        });

        doc.Transact(_ =>
        {
            x.Insert(0, "x");
            y.Insert(0, "b");
        });

        Assert.AreEqual(("bc | ", "010201000401017801780401017902626300"), (string.Join(" | ", deltas), Hex(doc.EncodeStateAsUpdate())));
    }

    [TestMethod]
    public void Observer_StartsTransactionTypingOntoTheOuterOne_ChangedParentTypesAsYjs()
    {
        var doc = Doc(1);
        var x = doc.GetText("x");
        var y = doc.GetText("y");
        var seen = new List<string>();
        var fired = false;
        x.Observe(_ =>
        {
            if (fired)
            {
                return;
            }

            fired = true;
            y.Insert(y.Length, "c");
            doc.AfterTransaction += (_, tr) => seen.Add(tr.ChangedParentTypes.TryGetValue(y, out var events) ? string.Join(";", events.Select(e => Describe(e.Delta))) : "-");
        });

        doc.Transact(_ =>
        {
            x.Insert(0, "x");
            y.Insert(0, "b");
        });

        Assert.AreEqual("bc | ", string.Join(" | ", seen.Take(2)));
    }

    [TestMethod]
    public void Observer_StartsTransactionPushingOntoTheOuterOne_OuterEventAddsYjsItems()
    {
        var doc = Doc(1);
        var x = doc.GetText("x");
        var y = doc.GetArray("y");
        var added = new List<string>();
        var fired = false;
        x.Observe(_ =>
        {
            if (fired)
            {
                return;
            }

            fired = true;
            y.Add(2);
            y.Observe(e => added.Add(string.Join(",", e.Changes.Added.Select(i => $"{i.Id.Clock}+{i.Length}").Order(StringComparer.Ordinal))));
        });

        doc.Transact(_ =>
        {
            x.Insert(0, "x");
            y.Add(1);
        });

        Assert.AreEqual("1+1,2+1 | ", string.Join(" | ", added));
    }

    private static (string Content, string V1) AppliedInsideTransaction(bool observed)
    {
        var doc = Doc(3);
        var t = doc.GetText("t");
        if (observed)
        {
            t.Observe(_ => { });
        }

        var peer = Doc(2);
        var pt = peer.GetText("t");
        t.Insert(0, "a");
        Sync(doc, peer);
        pt.Remove(0, 1);

        doc.Transact(_ =>
        {
            t.Insert(1, "b");
            t.Insert(2, "c");
            Sync(peer, doc);
            t.Insert(2, "d");
        });

        return (t.ToString(), Hex(doc.EncodeStateAsUpdate()));
    }

    [TestMethod]
    public void Get_StaleMarkerBeyondTheIndex_ReturnsNothingAsYjs()
    {
        // Yjs reads the content at a negative offset (undefined), although index 1 holds a value.
        var (result, v1) = StaleMarkerBeyondTheIndex(a => a[1].ToObject() ?? "undefined");

        Assert.AreEqual(("undefined", "0206020041010001410200014102010141020201410203012801016d016b017d0102010008010161017d01870100010102010005"), (result, v1));
    }

    [TestMethod]
    public void Insert_StaleMarkerBeyondTheIndexAndTheClock_ThrowsAndChangesNothingAsYjs()
    {
        var (result, v1) = StaleMarkerBeyondTheIndex(a =>
        {
            a.Insert(1, 7);
            return "inserted";
        });

        Assert.AreEqual(("throws", "0206020041010001410200014102010141020201410203012801016d016b017d0102010008010161017d01870100010102010005"), (result, v1));
    }

    [TestMethod]
    public void Delete_StaleMarkerBeyondTheIndex_DeletesFromTheStartAsYjs()
    {
        var (result, v1) = StaleMarkerBeyondTheIndex(a =>
        {
            a.RemoveRange(1, 1);
            return "deleted";
        });

        Assert.AreEqual(("deleted", "0206020041010001410200014102010141020201410203012801016d016b017d01020100010101610187010001020201000501010001"), (result, v1));
    }

    [TestMethod]
    public void TryGetValue_NegativeIndex_IsFalse()
    {
        var doc = new YDoc();
        var a = doc.GetArray("a");
        var x = doc.GetXmlFragment("x");
        a.Add(1, 2);
        x.Add(new YXmlText("t"));

        Assert.AreEqual((false, false), (a.TryGetValue(-1, out _), x.TryGetValue(-1, out _)));
    }

    /// <summary>
    /// A marker on client 1's item at index 5; a remote transaction deletes the five items before it and changes the
    /// map first, so the map's observer runs <paramref name="act"/> while the list's markers are still stale.
    /// </summary>
    private static (object Result, string V1) StaleMarkerBeyondTheIndex(Func<YArray, object> act)
    {
        var doc = Doc(1);
        var a = doc.GetArray("a");
        var m = doc.GetMap("m");
        a.Insert(0, 1);
        a.Add(new YMap());
        var peer = Doc(2);
        Sync(doc, peer);
        var pa = peer.GetArray("a");
        for (var i = 0; i < 5; i++)
        {
            pa.Insert(0, new YMap());
        }

        Sync(peer, doc);
        a[5].ToObject();
        peer.Transact(_ =>
        {
            peer.GetMap("m")["k"] = 1;
            pa.RemoveRange(0, 5);
        });
        object result = null;
        m.Observe(_ =>
        {
            try
            {
                result = act(a);
            }
            catch (InvalidOperationException)
            {
                // Yjs throws as well (findIndexSS on a negative clock).
                result = "throws";
            }
        });

        Sync(peer, doc);

        return (result, Hex(doc.EncodeStateAsUpdate()));
    }

    private static (string Calls, string Updates, string V1) Filtered(Func<ItemView, bool> keep, Action<YDoc> body)
    {
        var calls = new List<string>();
        var doc = new YDoc(new YDocOptions
        {
            ClientId = 1,
            GarbageCollectionFilter = item =>
            {
                calls.Add($"{item.Id.Clock}+{item.Length}");
                return keep(item);
            },
        });
        var updates = new List<string>();
        doc.Updated += (_, e) => updates.Add(Hex(e.Update.ToArray()));

        body(doc);

        return (string.Join(",", calls), string.Join(",", updates), Hex(doc.EncodeStateAsUpdate()));
    }

    private static YDoc Doc(long client) => new(new YDocOptions { ClientId = client });

    private static void Sync(YDoc from, YDoc to) => to.ApplyUpdate(from.EncodeStateAsUpdate(to.EncodeStateVector()));

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    // Each op as its insert, and its ychange type after a colon.
    private static string Describe(IEnumerable<Delta> delta) =>
        string.Join("|", delta.Select(d => d.Inserted() + (d.Attributes != null && d.Attributes.TryGetValue("ychange", out var c) ? ":" + (c["type"] == "added" ? "Added" : "Removed") : "")));
}
