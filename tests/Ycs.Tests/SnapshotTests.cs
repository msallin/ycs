using System;
using System.Linq;
using System.Collections;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class SnapshotTests : YTestBase
{
    [TestMethod]
    public void TestBasicRestoreSnapshot()
    {
        var doc = new YDoc(new YDocOptions { GarbageCollection = false });
        doc.GetArray("array").Insert(0, "hello");

        var snap = doc.CreateSnapshot();
        doc.GetArray("array").Insert(1, "world");

        var docRestored = doc.CreateDocFromSnapshot(snap);

        CollectionAssert.AreEqual(new[] { "hello" }, docRestored.GetArray("array").ToArray().Select(v => v.ToObject()).ToList());
        CollectionAssert.AreEqual(new[] { "hello", "world" }, doc.GetArray("array").ToArray().Select(v => v.ToObject()).ToList());
    }

    [TestMethod]
    public void TestEmptyRestoreSnapshot()
    {
        var doc = new YDoc(new YDocOptions { GarbageCollection = false });
        var snap = doc.CreateSnapshot();

        snap.StateVector[9999] = 0;
        doc.GetArray().Insert(0, "world");

        var docRestored = doc.CreateDocFromSnapshot(snap);
        Assert.AreEqual(0, docRestored.GetArray().ToArray().Length);
        CollectionAssert.AreEqual(new[] { "world" }, doc.GetArray().ToArray().Select(v => v.ToObject()).ToList());

        // Now this snapshot reflects the latest state. It should still work.
        var snap2 = doc.CreateSnapshot();
        var docRestored2 = doc.CreateDocFromSnapshot(snap2);
        CollectionAssert.AreEqual(new[] { "world" }, docRestored2.GetArray().ToArray().Select(v => v.ToObject()).ToList());
    }

    [TestMethod]
    public void TestRestoreSnapshotWithSubType()
    {
        var doc = new YDoc(new YDocOptions { GarbageCollection = false });
        doc.GetArray("array").Insert(0, new YMap());
        var subMap = doc.GetArray("array")[0].ToObject() as YMap;
        subMap["key1"] = "value1";

        var snap = doc.CreateSnapshot();
        subMap["key2"] = "value2";
        var docRestored = doc.CreateDocFromSnapshot(snap);

        var restoredSubMap = docRestored.GetArray("array")[0].ToObject() as YMap;
        subMap = doc.GetArray("array")[0].ToObject() as YMap;

        Assert.AreEqual(1, restoredSubMap.Count);
        Assert.AreEqual("value1", restoredSubMap["key1"].ToObject());

        Assert.AreEqual(2, subMap.Count);
        Assert.AreEqual("value1", subMap["key1"].ToObject());
        Assert.AreEqual("value2", subMap["key2"].ToObject());
    }

    [TestMethod]
    public void TestRestoreDeletedItem1()
    {
        var doc = new YDoc(new YDocOptions { GarbageCollection = false });
        doc.GetArray("array").Insert(0, "item1", "item2");

        var snap = doc.CreateSnapshot();
        doc.GetArray("array").RemoveAt(0);
        var docRestored = doc.CreateDocFromSnapshot(snap);

        CollectionAssert.AreEqual(new[] { "item1", "item2" }, docRestored.GetArray("array").ToArray().Select(v => v.ToObject()).ToList());
        CollectionAssert.AreEqual(new[] { "item2" }, doc.GetArray("array").ToArray().Select(v => v.ToObject()).ToList());
    }

    [TestMethod]
    public void TestRestoreLeftItem()
    {
        var doc = new YDoc(new YDocOptions { GarbageCollection = false });
        doc.GetArray("array").Insert(0, "item1");
        doc.GetMap("map")["test"] = 1;
        doc.GetArray("array").Insert(0, "item0");

        var snap = doc.CreateSnapshot();
        doc.GetArray("array").RemoveAt(1);
        var docRestored = doc.CreateDocFromSnapshot(snap);

        CollectionAssert.AreEqual(new[] { "item0", "item1" }, docRestored.GetArray("array").ToArray().Select(v => v.ToObject()).ToList());
        CollectionAssert.AreEqual(new[] { "item0" }, doc.GetArray("array").ToArray().Select(v => v.ToObject()).ToList());
    }

    [TestMethod]
    public void TestDeletedItemsBase()
    {
        var doc = new YDoc(new YDocOptions { GarbageCollection = false });
        doc.GetArray("array").Insert(0, "item1");
        doc.GetArray("array").RemoveAt(0);

        var snap = doc.CreateSnapshot();
        doc.GetArray("array").Insert(0, "item0");
        var docRestored = doc.CreateDocFromSnapshot(snap);

        Assert.AreEqual(0, docRestored.GetArray("array").ToArray().Length);
        CollectionAssert.AreEqual(new[] { "item0" }, doc.GetArray("array").ToArray().Select(v => v.ToObject()).ToList());
    }

    [TestMethod]
    public void TestDeletedItems2()
    {
        var doc = new YDoc(new YDocOptions { GarbageCollection = false });
        doc.GetArray("array").Insert(0, "item1", "item2", "item3");
        doc.GetArray("array").RemoveAt(1);

        var snap = doc.CreateSnapshot();
        doc.GetArray("array").Insert(0, "item0");
        var docRestored = doc.CreateDocFromSnapshot(snap);

        CollectionAssert.AreEqual(new[] { "item1", "item3" }, docRestored.GetArray("array").ToArray().Select(v => v.ToObject()).ToList());
        CollectionAssert.AreEqual(new[] { "item0", "item1", "item3" }, doc.GetArray("array").ToArray().Select(v => v.ToObject()).ToList());
    }

    [TestMethod]
    public void TestDependentChanges()
    {
        Init(users: 2, new YDocOptions { GarbageCollection = false });
        var array0 = Arrays[Users[0]];
        var array1 = Arrays[Users[1]];

        array0.Insert(0, "user1item1");
        Connector.SyncAll();
        array1.Insert(1, "user2item1");
        Connector.SyncAll();

        var snap = array0.Doc.CreateSnapshot();

        array0.Insert(2, "user1item2");
        Connector.SyncAll();
        array1.Insert(3, "user2item2");
        Connector.SyncAll();

        var docRestored0 = array0.Doc.CreateDocFromSnapshot(snap);
        CollectionAssert.AreEqual(new[] { "user1item1", "user2item1" }, docRestored0.GetArray("array").ToArray().Select(v => v.ToObject()).ToList());

        var docRestored1 = array1.Doc.CreateDocFromSnapshot(snap);
        CollectionAssert.AreEqual(new[] { "user1item1", "user2item1" }, docRestored1.GetArray("array").ToArray().Select(v => v.ToObject()).ToList());
    }

    [TestMethod]
    [DataRow(UpdateFormat.V1)]
    [DataRow(UpdateFormat.V2)]
    public void Decode_InputOverwrittenAfterwards_ResultUnchanged(UpdateFormat format)
    {
        var doc = new YDoc(new YDocOptions { GarbageCollection = false });
        doc.GetText("t").Insert(0, "abc");
        doc.GetText("t").Remove(1, 1);
        var snapshot = doc.CreateSnapshot();
        var input = snapshot.Encode(format);

        var decoded = Snapshot.Decode(input, format);
        input.AsSpan().Fill(0xFF);

        Assert.AreEqual(snapshot, decoded);
    }

    [TestMethod]
    public void ContainsUpdate_NestingBeyondLoweredMaxDepth_ThrowsYUpdateException()
    {
        // One struct (client 5) holding a doc-less Any value nested ten levels deep.
        var nested = string.Concat(Enumerable.Repeat("75 01 ", 10));
        var update = Convert.FromHexString(("01 01 05 00 08 01 01 61 01 " + nested + "7E 00").Replace(" ", string.Empty, StringComparison.Ordinal));
        var snapshot = new YDoc().CreateSnapshot();

        var thrown = Assert.ThrowsExactly<YUpdateException>(() => snapshot.ContainsUpdate(update, limits: new UpdateLimits { MaxDepth = 3 }));
        Assert.IsNotNull(thrown);
        Assert.IsFalse(snapshot.ContainsUpdate(update));
    }
}
