// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs
{
    /// <summary>
    /// Covers the Yjs-13.6.32 Snapshot surface that <see cref="SnapshotTests"/> (pre-existing, left
    /// untouched) does not: <see cref="Snapshot.Empty"/>/<see cref="Snapshot.EqualSnapshots"/>, the v1
    /// snapshot wire format (<see cref="Snapshot.EncodeSnapshot"/>/<see cref="Snapshot.DecodeSnapshot(byte[])"/>),
    /// <see cref="Snapshot.SnapshotContainsUpdate"/>/<see cref="Snapshot.SnapshotContainsUpdateV2"/>,
    /// the <see cref="YDocSnapshotExtensions.CreateDocFromSnapshot"/> extension, and the
    /// snapshot-aware type helpers in Types/AbstractType.Snapshot.cs.
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
            var snap = Snapshot.DecodeSnapshot(B(SnapshotV1));

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
            var snap = Snapshot.DecodeSnapshot(B(SnapshotV1));
            CollectionAssert.AreEqual(B(SnapshotV1), snap.EncodeSnapshot());
        }

        [TestMethod]
        public void RoundTripsYjsSnapshotV2Bytes()
        {
            var snap = Snapshot.DecodeSnapshotV2(B(SnapshotV2));
            CollectionAssert.AreEqual(B(SnapshotV2), snap.EncodeSnapshotV2());
        }

        [TestMethod]
        public void SnapshotBytesRoundTripThroughYjsFixtures()
        {
            // The v1 and v2 encodings of the same snapshot must decode to equal snapshots, even though
            // their bytes differ (the delete set uses different varint framing between the two).
            CollectionAssert.AreNotEqual(B(SnapshotV1), B(SnapshotV2));

            var fromV1 = Snapshot.DecodeSnapshot(B(SnapshotV1));
            var fromV2 = Snapshot.DecodeSnapshotV2(B(SnapshotV2));

            Assert.IsTrue(Snapshot.EqualSnapshots(fromV1, fromV2));
            Assert.IsTrue(fromV1.Equals(fromV2));
        }

        [TestMethod]
        public void EmptySnapshotIsEmptyAndEqualsItself()
        {
            Assert.AreEqual(0, Snapshot.Empty.StateVector.Count);
            Assert.AreEqual(0, Snapshot.Empty.DeleteSet.Clients.Count);
            Assert.IsTrue(Snapshot.EqualSnapshots(Snapshot.Empty, Snapshot.Empty));

            var doc = new YDoc(new YDocOptions { Gc = false });
            doc.GetText().Insert(0, "x");
            var nonEmpty = doc.CreateSnapshot();

            Assert.IsFalse(Snapshot.EqualSnapshots(Snapshot.Empty, nonEmpty));
        }

        [TestMethod]
        public void EqualSnapshotsIsNullSafe()
        {
            Assert.IsTrue(Snapshot.EqualSnapshots(null, null));
            Assert.IsFalse(Snapshot.EqualSnapshots(Snapshot.Empty, null));
            Assert.IsFalse(Snapshot.EqualSnapshots(null, Snapshot.Empty));
        }

        [TestMethod]
        public void SnapshotContainsUpdate_TrueForAPrefixFalseOnceMoreIsWritten()
        {
            var doc = new YDoc(new YDocOptions { Gc = false });
            doc.GetText().Insert(0, "abc");
            var prefixUpdate = doc.EncodeStateAsUpdateV1();

            var snap = doc.CreateSnapshot();

            doc.GetText().Insert(3, "def");
            var fullUpdate = doc.EncodeStateAsUpdateV1();

            Assert.IsTrue(snap.SnapshotContainsUpdate(prefixUpdate));
            Assert.IsFalse(snap.SnapshotContainsUpdate(fullUpdate));
        }

        [TestMethod]
        public void SnapshotContainsUpdate_FalseWhenUpdateDeletesSomethingTheSnapshotDidNotSee()
        {
            var doc = new YDoc(new YDocOptions { Gc = false });
            doc.GetArray("array").Insert(0, new object[] { "a", "b", "c" });

            // Same ids the snapshot below will cover, no deletes yet: must be contained.
            var insertOnlyUpdate = doc.EncodeStateAsUpdateV1();

            var snap = doc.CreateSnapshot();

            doc.GetArray("array").Delete(0);
            var updateWithDelete = doc.EncodeStateAsUpdateV1();

            Assert.IsTrue(snap.SnapshotContainsUpdate(insertOnlyUpdate));
            Assert.IsFalse(snap.SnapshotContainsUpdate(updateWithDelete));
        }

        [TestMethod]
        public void SnapshotContainsUpdateV2_MirrorsV1()
        {
            var doc = new YDoc(new YDocOptions { Gc = false });
            doc.GetText().Insert(0, "abc");
            var prefixUpdate = doc.EncodeStateAsUpdateV2();

            var snap = doc.CreateSnapshot();

            doc.GetText().Insert(3, "def");
            var fullUpdate = doc.EncodeStateAsUpdateV2();

            Assert.IsTrue(snap.SnapshotContainsUpdateV2(prefixUpdate));
            Assert.IsFalse(snap.SnapshotContainsUpdateV2(fullUpdate));
        }

        [TestMethod]
        public void CreateDocFromSnapshot_MatchesRestoreDocument()
        {
            var doc = new YDoc(new YDocOptions { Gc = false });
            doc.GetArray("array").Insert(0, new[] { "hello" });

            var snap = doc.CreateSnapshot();
            doc.GetArray("array").Insert(1, new[] { "world" });

            var viaRestoreDocument = snap.RestoreDocument(doc);
            var viaCreateDocFromSnapshot = doc.CreateDocFromSnapshot(snap);

            CollectionAssert.AreEqual(
                (System.Collections.ICollection)viaRestoreDocument.GetArray("array").ToArray(),
                (System.Collections.ICollection)viaCreateDocFromSnapshot.GetArray("array").ToArray());
            CollectionAssert.AreEqual(new[] { "hello" }, (System.Collections.ICollection)viaCreateDocFromSnapshot.GetArray("array").ToArray());
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

            Assert.ThrowsException<InvalidOperationException>(() => doc.CreateDocFromSnapshot(snap));
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

            // 'w' lands right after 'z' and, being from the same client, would otherwise get merged
            // into one struct with 'y'/'z' by transaction cleanup - a regression test that
            // ToArraySnapshot correctly excludes content added after the snapshot even when it ends up
            // struct-merged with content that predates it.
            array.Insert(3, new object[] { "w" });
            array.Delete(0);

            CollectionAssert.AreEqual(new object[] { "x", "y", "z" }, ((IList<object>)array.ToArraySnapshot(snap)).ToArray());
            CollectionAssert.AreEqual(new object[] { "y", "z", "w" }, array.ToArray().ToArray());
        }
    }
}
