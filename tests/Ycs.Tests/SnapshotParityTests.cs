// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Covers the Yjs-13.6.32 Snapshot surface that <see cref="SnapshotTests"/> (pre-existing, left
/// untouched) does not: <see cref="Snapshot.Empty"/>/<see cref="Snapshot.Equals(Snapshot)"/>, the v1
/// snapshot wire format (<see cref="Snapshot.Encode(UpdateFormat)"/>/<see cref="Snapshot.Decode"/>),
/// <see cref="Snapshot.ContainsUpdate"/>,
/// <see cref="YDoc.CreateDocFromSnapshot"/>, and the
/// snapshot-aware type helpers in Types/YType.Snapshot.cs.
/// <br/>
/// The base64 fixtures below were produced by real Yjs 13.6.32 (yjs13, pinned client id 1) -
/// see the generating script recorded next to <see cref="SnapshotBytesRoundTripThroughYjsFixtures"/>.
/// </summary>
[TestClass]
public class SnapshotParityTests
{
    // Generated with (interop/js, `node -e "..."`, yjs13 = 13.6.32):
    //   const doc = new Y.Doc({ gc: false }); doc.clientID = 1;
    //   doc.transact(() => {
    //     doc.getText('text').insert(0, 'hello world');
    //     doc.getArray('array').insert(0, ['a', 'b', 'c']);
    //   });
    //   doc.getText('text').delete(2, 3);   // 'hello world' -> 'he world'
    //   doc.getArray('array').delete(1, 1); // ['a','b','c'] -> ['a','c']
    //   const snap = Y.snapshot(doc);
    private const string UpdateV1 = "AQYBAAQBBHRleHQCaGWEAQEDbGxvhAEEBiB3b3JsZAgBBWFycmF5AXcBYYgBCwF3AWKIAQwBdwFjAQECAgMMAQ==";
    private const string SnapshotV1 = "AQECAgMMAQEBDg==";
    private const string SnapshotV2 = "AQECAgIHAAEBDg==";

    private static byte[] B(string base64) => Convert.FromBase64String(base64);

    [TestMethod]
    public void DecodesYjsSnapshotV1AndMatchesStateVector()
    {
        var snap = Snapshot.Decode(B(SnapshotV1));

        Assert.AreEqual(1, snap.StateVector.Count);
        Assert.AreEqual(14L, snap.StateVector[1]);
        Assert.AreEqual(1, snap.DeleteSet.Clients.Count);
        CollectionAssert.AreEqual(
            new[] { (2L, 3L), (12L, 1L) },
            snap.DeleteSet.Clients[1].Select(di => (di.Clock, di.Length)).ToArray());
    }

    [TestMethod]
    public void RoundTripsYjsSnapshotV1Bytes()
    {
        var snap = Snapshot.Decode(B(SnapshotV1));
        CollectionAssert.AreEqual(B(SnapshotV1), snap.Encode());
    }

    [TestMethod]
    public void RoundTripsYjsSnapshotV2Bytes()
    {
        var snap = Snapshot.Decode(B(SnapshotV2), UpdateFormat.V2);
        CollectionAssert.AreEqual(B(SnapshotV2), snap.Encode(UpdateFormat.V2));
    }

    [TestMethod]
    public void SnapshotBytesRoundTripThroughYjsFixtures()
    {
        // The v1 and v2 encodings of the same snapshot must decode to equal snapshots, even though
        // their bytes differ (the delete set uses different varint framing between the two).
        CollectionAssert.AreNotEqual(B(SnapshotV1), B(SnapshotV2));

        var fromV1 = Snapshot.Decode(B(SnapshotV1));
        var fromV2 = Snapshot.Decode(B(SnapshotV2), UpdateFormat.V2);

        Assert.IsTrue(Snapshot.Equals(fromV1, fromV2));
        Assert.IsTrue(fromV1.Equals(fromV2));
    }

    [TestMethod]
    public void EmptySnapshotIsEmptyAndEqualsItself()
    {
        Assert.AreEqual(0, Snapshot.Empty.StateVector.Count);
        Assert.AreEqual(0, Snapshot.Empty.DeleteSet.Clients.Count);
        Assert.IsTrue(Snapshot.Equals(Snapshot.Empty, Snapshot.Empty));

        var doc = new YDoc(new YDocOptions { Gc = false });
        doc.GetText().Insert(0, "x");
        var nonEmpty = doc.CreateSnapshot();

        Assert.IsFalse(Snapshot.Equals(Snapshot.Empty, nonEmpty));
    }

    [TestMethod]
    public void EqualSnapshotsIsNullSafe()
    {
        Assert.IsTrue(Snapshot.Equals(null, null));
        Assert.IsFalse(Snapshot.Equals(Snapshot.Empty, null));
        Assert.IsFalse(Snapshot.Equals(null, Snapshot.Empty));
    }

    [TestMethod]
    public void SnapshotContainsUpdate_TrueForAPrefixFalseOnceMoreIsWritten()
    {
        var doc = new YDoc(new YDocOptions { Gc = false });
        doc.GetText().Insert(0, "abc");
        var prefixUpdate = doc.EncodeStateAsUpdate();

        var snap = doc.CreateSnapshot();

        doc.GetText().Insert(3, "def");
        var fullUpdate = doc.EncodeStateAsUpdate();

        Assert.IsTrue(snap.ContainsUpdate(prefixUpdate));
        Assert.IsFalse(snap.ContainsUpdate(fullUpdate));
    }

    [TestMethod]
    public void SnapshotContainsUpdate_FalseWhenUpdateDeletesSomethingTheSnapshotDidNotSee()
    {
        var doc = new YDoc(new YDocOptions { Gc = false });
        doc.GetArray("array").Insert(0, new object[] { "a", "b", "c" });

        // Same ids the snapshot below will cover, no deletes yet: must be contained.
        var insertOnlyUpdate = doc.EncodeStateAsUpdate();

        var snap = doc.CreateSnapshot();

        doc.GetArray("array").Delete(0);
        var updateWithDelete = doc.EncodeStateAsUpdate();

        Assert.IsTrue(snap.ContainsUpdate(insertOnlyUpdate));
        Assert.IsFalse(snap.ContainsUpdate(updateWithDelete));
    }

    [TestMethod]
    public void SnapshotContainsUpdateV2_MirrorsV1()
    {
        var doc = new YDoc(new YDocOptions { Gc = false });
        doc.GetText().Insert(0, "abc");
        var prefixUpdate = doc.EncodeStateAsUpdate(format: UpdateFormat.V2);

        var snap = doc.CreateSnapshot();

        doc.GetText().Insert(3, "def");
        var fullUpdate = doc.EncodeStateAsUpdate(format: UpdateFormat.V2);

        Assert.IsTrue(snap.ContainsUpdate(prefixUpdate, UpdateFormat.V2));
        Assert.IsFalse(snap.ContainsUpdate(fullUpdate, UpdateFormat.V2));
    }

    [TestMethod]
    public void CreateDocFromSnapshot_CanTargetAnExistingDoc()
    {
        var doc = new YDoc(new YDocOptions { Gc = false });
        doc.GetArray("array").Insert(0, new[] { "hello" });
        var snap = doc.CreateSnapshot();
        doc.GetArray("array").Insert(1, new[] { "world" });

        var target = new YDoc();
        var result = doc.CreateDocFromSnapshot(snap, target);

        Assert.AreSame(target, result);
        CollectionAssert.AreEqual(new[] { "hello" }, (System.Collections.ICollection)target.GetArray("array").ToArray());
    }

    [TestMethod]
    public void CreateDocFromSnapshot_ThrowsWhenOriginIsGarbageCollected()
    {
        var doc = new YDoc(); // Gc = true (default)
        doc.GetText().Insert(0, "x");
        var snap = doc.CreateSnapshot();

        Assert.ThrowsExactly<InvalidOperationException>(() => doc.CreateDocFromSnapshot(snap));
    }

    [TestMethod]
    public void MapGetSnapshotAndGetAllSnapshot()
    {
        var doc = new YDoc(new YDocOptions { Gc = false });
        var map = doc.GetMap("map");
        map.Set("a", 1);
        map.Set("b", 2);

        var snap = doc.CreateSnapshot();

        map.Set("a", 100);
        map.Delete("b");
        map.Set("c", 3);

        Assert.AreEqual(1L, Convert.ToInt64(map.GetSnapshot("a", snap)));
        Assert.AreEqual(2L, Convert.ToInt64(map.GetSnapshot("b", snap)));
        Assert.IsNull(map.GetSnapshot("c", snap));

        // Live (unsnapshotted) reads see the current state.
        Assert.AreEqual(100L, Convert.ToInt64(map.Get("a")));
        Assert.AreEqual(3L, Convert.ToInt64(map.Get("c")));

        var all = map.GetAllSnapshot(snap);
        Assert.AreEqual(2, all.Count);
        Assert.AreEqual(1L, Convert.ToInt64(all["a"]));
        Assert.AreEqual(2L, Convert.ToInt64(all["b"]));
        Assert.IsFalse(all.ContainsKey("c"));
    }

    [TestMethod]
    public void ArrayToArraySnapshot()
    {
        var doc = new YDoc(new YDocOptions { Gc = false });
        var array = doc.GetArray("array");
        array.Insert(0, new object[] { "x", "y", "z" });

        var snap = doc.CreateSnapshot();

        // 'w' is merged into the item of 'y', 'z' by the cleanup; as Yjs' typeListToArraySnapshot,
        // the read does not split, so the merged item is visible whole (Yjs: x, y, z, w).
        array.Insert(3, new object[] { "w" });
        array.Delete(0);

        CollectionAssert.AreEqual(new object[] { "x", "y", "z", "w" }, ((IList<object>)array.ToArraySnapshot(snap)).ToArray());
        CollectionAssert.AreEqual(new object[] { "y", "z", "w" }, array.ToArray().ToArray());
    }

    [TestMethod]
    public void SnapshotReads_ItemMergedAcrossTheSnapshotAfterALoneHighSurrogate_LeaveTheDocumentUnchanged()
    {
        var doc = new YDoc(new YDocOptions { Gc = false });
        var text = doc.GetText("text");
        var array = doc.GetArray("array");
        var map = doc.GetMap("map");
        text.Insert(0, "c\ud800");
        var snap = doc.CreateSnapshot();
        text.Insert(2, "a");
        var before = doc.EncodeStateAsUpdate();
        var transactions = 0;
        doc.AfterTransaction += (_, _) => transactions++;

        array.ToArraySnapshot(snap);
        map.GetAllSnapshot(snap);
        map.GetSnapshot("k", snap);

        Assert.AreEqual(("c\ud800a", 0, Convert.ToBase64String(before)), (text.ToString(), transactions, Convert.ToBase64String(doc.EncodeStateAsUpdate())));
    }

    [TestMethod]
    public void ToDelta_SnapshotSplitsAfterALoneHighSurrogate_ReplacesBothSidesAsYjs()
    {
        // Yjs 13.6.32: toDelta(snapshot) splits at the snapshot, and ContentString.splice replaces the
        // high surrogate and the next character with U+FFFD (yjs#248): [{insert: 'c\ufffd'}], 'c\ufffd\ufffd'.
        var doc = new YDoc(new YDocOptions { Gc = false });
        var text = doc.GetText("text");
        text.Insert(0, "c\ud800");
        var snap = doc.CreateSnapshot();
        text.Insert(2, "a");

        var delta = text.ToDelta(snap);

        Assert.AreEqual(("c\ufffd", "c\ufffd\ufffd"), (delta.Single().Inserted().GetString(), text.ToString()));
    }
}
