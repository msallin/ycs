using System.Linq;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>Snapshots decoded from bytes another peer sent, used on a document they do not match.</summary>
[TestClass]
public class SnapshotInputTests
{
    private static YDoc Doc(int clientId, string text)
    {
        var doc = new YDoc(new YDocOptions { ClientId = clientId, GarbageCollection = false });
        doc.GetText("t").Insert(0, text);
        return doc;
    }

    // A snapshot of a document of client 2 that deleted one of its two characters.
    private static Snapshot ForeignSnapshot()
    {
        var other = Doc(2, "xy");
        other.GetText("t").Remove(0, 1);
        return Snapshot.Decode(other.CreateSnapshot().Encode());
    }

    [TestMethod]
    public void CreateDocFromSnapshot_UnknownClient_ThrowsYUpdateException()
    {
        var doc = Doc(1, "ab");

        Assert.ThrowsExactly<YUpdateException>(() => doc.CreateDocFromSnapshot(ForeignSnapshot()));
    }

    [TestMethod]
    public void CreateDocFromSnapshot_ClockBeyondTheDocument_ThrowsYUpdateException()
    {
        var later = Doc(1, "abc");
        var earlier = Doc(1, "ab");

        Assert.ThrowsExactly<YUpdateException>(() => earlier.CreateDocFromSnapshot(Snapshot.Decode(later.CreateSnapshot().Encode())));
    }

    [TestMethod]
    public void ToArraySnapshot_DeleteSetOfAnUnknownClient_IsIgnored()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1, GarbageCollection = false });
        var array = doc.GetArray("a");
        array.Insert(0, 1, 2);
        var own = doc.CreateSnapshot();
        var foreign = ForeignSnapshot();
        var snapshot = new Snapshot(new DeleteSet(new List<DeleteSet> { own.DeleteSet, foreign.DeleteSet }), own.StateVector);

        CollectionAssert.AreEqual(new object[] { 1, 2 }, (System.Collections.ICollection)array.ToArray(snapshot).Select(v => v.ToObject()).ToList());
    }

    [TestMethod]
    public void GetAllSnapshot_DeleteSetBeyondTheDocument_IsIgnored()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1, GarbageCollection = false });
        var map = doc.GetMap("m");
        map["k"] = 1;
        var own = doc.CreateSnapshot();
        var ds = new DeleteSet();
        ds.Add(1, 5, 3);
        var snapshot = new Snapshot(new DeleteSet(new List<DeleteSet> { own.DeleteSet, ds }), own.StateVector);

        Assert.AreEqual(1, map.ToDictionary(snapshot).ToDictionary(kv => kv.Key, kv => kv.Value.ToObject())["k"]);
    }
}
