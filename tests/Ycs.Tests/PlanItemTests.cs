using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// A planned item that overlaps the store has the id of the store's item (the plan keys its checks by item, not by
/// id): a store item and a decoded one are never equal, whatever their ids.
/// </summary>
[TestClass]
public class PlanItemTests
{
    private static (YDoc Doc, Item Decoded) SameIdPair()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        doc.GetText("t").Insert(0, "abc");
        return (doc, new Item(new ItemId(1, 0), null, null, null, null, "t", null, new ContentString("abcd")));
    }

    [TestMethod]
    public void Equals_StoreItemAndDecodedItemOfOneId_AreDifferentKeys()
    {
        var (doc, decoded) = SameIdPair();
        var stored = PlanItem.Store(doc.Store.Find(new ItemId(1, 0)));
        var keys = new Dictionary<PlanItem, int> { [stored] = 1 };

        keys[PlanItem.Decoded(decoded)] = 2;

        Assert.AreEqual((2, 1, 2), (keys.Count, keys[stored], keys[PlanItem.Decoded(decoded)]));
    }

    [TestMethod]
    public void Equals_TheSameItemTwice_IsOneKey()
    {
        var (doc, decoded) = SameIdPair();
        var keys = new Dictionary<PlanItem, int> { [PlanItem.Store(doc.Store.Find(new ItemId(1, 0)))] = 1, [PlanItem.Decoded(decoded)] = 2 };

        keys[PlanItem.Store(doc.Store.Find(new ItemId(1, 1)))] = 3;
        keys[PlanItem.Decoded(decoded)] = 4;

        Assert.AreEqual((2, 3, 4), (keys.Count, keys[PlanItem.Store(doc.Store.Find(new ItemId(1, 2)))], keys[PlanItem.Decoded(decoded)]));
    }

    [TestMethod]
    public void Equals_DefaultAndAnItem_AreDifferent()
    {
        var (doc, decoded) = SameIdPair();

        Assert.AreEqual((true, false, false), (default(PlanItem).IsNull, default(PlanItem) == PlanItem.Store(doc.Store.Find(new ItemId(1, 0))), default(PlanItem) == PlanItem.Decoded(decoded)));
    }
}
