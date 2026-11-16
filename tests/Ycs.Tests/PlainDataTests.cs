using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class PlainDataTests
{
    [TestMethod]
    public void ItemId_UsedAsDictionaryKey_FindsEqualValue()
    {
        var map = new Dictionary<ItemId, string> { [new ItemId(1, 2)] = "a" };

        Assert.AreEqual(("a", false), (map[new ItemId(1, 2)], map.ContainsKey(new ItemId(2, 1))));
    }

    [TestMethod]
    public void UpdateLimits_With_ChangesOneLimitAndKeepsTheRest()
    {
        var changed = UpdateLimits.Default with { MaxDepth = 5 };

        Assert.AreEqual((5, UpdateLimits.Default.MaxPendingBytes, 3_000), (changed.MaxDepth, changed.MaxPendingBytes, UpdateLimits.Default.MaxDepth));
    }

    [TestMethod]
    public void UpdateLimits_Equality_IsByValue()
    {
        Assert.AreEqual((true, false), (UpdateLimits.Default == new UpdateLimits(), UpdateLimits.Default == UpdateLimits.Unlimited));
    }

    [TestMethod]
    public void RelativePosition_InHashSet_EqualPositionsCollapse()
    {
        var doc = new YDoc();
        var text = doc.GetText("t");
        text.Insert(0, "abc");
        var set = new HashSet<RelativePosition>
        {
            RelativePosition.FromIndex(text, 1),
            RelativePosition.FromIndex(text, 1),
            RelativePosition.FromIndex(text, 2),
            RelativePosition.FromIndex(text, 1, -1),
        };

        Assert.AreEqual(3, set.Count);
    }

    [TestMethod]
    public void RelativePosition_Equals_ObjectOverloadAgreesWithTypedAndHash()
    {
        var doc = new YDoc();
        var text = doc.GetText("t");
        text.Insert(0, "abc");
        var a = RelativePosition.FromIndex(text, 1);
        var b = RelativePosition.FromIndex(text, 1);

        Assert.AreEqual((true, true, false), (a.Equals((object)b), a.GetHashCode() == b.GetHashCode(), a.Equals((object)"x")));
    }

    [TestMethod]
    public void Snapshot_InHashSetAndDictionary_EqualSnapshotsCollapse()
    {
        var doc = new YDoc(new YDocOptions { Gc = false });
        var text = doc.GetText("t");
        text.Insert(0, "abc");
        var (first, second) = (doc.CreateSnapshot(), doc.CreateSnapshot());
        text.Delete(1, 1);
        var third = doc.CreateSnapshot();
        var set = new HashSet<Snapshot> { first, second, third };
        var map = new Dictionary<Snapshot, int> { [first] = 1 };

        Assert.AreEqual((2, 1, false), (set.Count, map[second], map.ContainsKey(third)));
    }

    [TestMethod]
    public void Snapshot_GetHashCode_ManyClientsWithLargeValues_DoesNotThrowUnderCheckedArithmetic()
    {
        var vector = new Dictionary<long, long>();
        for (var i = 0; i < 200; i++)
        {
            vector[uint.MaxValue - i] = int.MaxValue - i;
        }

        var snapshot = new Snapshot(new DeleteSet(), vector);

        Assert.AreEqual(snapshot.GetHashCode(), new Snapshot(new DeleteSet(), new Dictionary<long, long>(vector)).GetHashCode());
    }

    [TestMethod]
    public void Snapshot_Equals_ObjectOverloadAgreesWithTypedAndHash()
    {
        var doc = new YDoc(new YDocOptions { Gc = false });
        doc.GetText("t").Insert(0, "abc");
        var a = doc.CreateSnapshot();
        var b = doc.CreateSnapshot();

        Assert.AreEqual((true, true, false), (a.Equals((object)b), a.GetHashCode() == b.GetHashCode(), a.Equals((object)"x")));
    }
}
