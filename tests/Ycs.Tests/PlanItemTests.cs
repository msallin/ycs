using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// A planned item that overlaps the store has the id of the store's item (the plan keys its checks by item, not by
/// id): a store item and a staged one are never equal, whatever their ids; staged items are the same when the table and
/// the index are.
/// </summary>
[TestClass]
public class PlanItemTests
{
    private static (YDoc Doc, StagingTable Table) SameIdPair()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        doc.GetText("t").Insert(0, "abc");
        var table = new StagingTable();
        StageText(table, 1, 0, "abcd");
        return (doc, table);
    }

    private static void StageText(StagingTable table, long client, long clock, string text)
    {
        table.BeginClient(client, clock);
        StructStaging.Stage(table, new Item(new ItemId(client, clock), null, null, "t", null, new ContentString(text)));
    }

    [TestMethod]
    public void Equals_StoreItemAndStagedItemOfOneId_AreDifferentKeys()
    {
        var (doc, table) = SameIdPair();
        var stored = PlanItem.Store(doc.Store.Find(new ItemId(1, 0)));
        var keys = new Dictionary<PlanItem, int> { [stored] = 1 };

        keys[PlanItem.Staged(table, 0)] = 2;

        Assert.AreEqual((2, 1, 2), (keys.Count, keys[stored], keys[PlanItem.Staged(table, 0)]));
    }

    [TestMethod]
    public void Equals_TheSameItemTwice_IsOneKey()
    {
        var (doc, table) = SameIdPair();
        var keys = new Dictionary<PlanItem, int> { [PlanItem.Store(doc.Store.Find(new ItemId(1, 0)))] = 1, [PlanItem.Staged(table, 0)] = 2 };

        keys[PlanItem.Store(doc.Store.Find(new ItemId(1, 1)))] = 3;
        keys[PlanItem.Staged(table, 0)] = 4;

        Assert.AreEqual((2, 3, 4), (keys.Count, keys[PlanItem.Store(doc.Store.Find(new ItemId(1, 2)))], keys[PlanItem.Staged(table, 0)]));
    }

    [TestMethod]
    public void Equals_DefaultAndAnItem_AreDifferent()
    {
        var (doc, table) = SameIdPair();

        Assert.AreEqual((true, false, false), (default(PlanItem).IsNull, default(PlanItem) == PlanItem.Store(doc.Store.Find(new ItemId(1, 0))), default(PlanItem) == PlanItem.Staged(table, 0)));
    }

    [TestMethod]
    public void Equals_StagedItemsOfOneIndexInTwoTables_AreDifferent()
    {
        // A reader's table is reused for the next update: an index of the last one must not name an item of this one.
        var (_, first) = SameIdPair();
        var second = new StagingTable();
        StageText(second, 1, 0, "abcd");

        Assert.AreEqual((false, true, 0, 0), (PlanItem.Staged(first, 0) == PlanItem.Staged(second, 0), PlanItem.Staged(first, 0) == PlanItem.Staged(first, 0), PlanItem.Staged(first, 0).GetHashCode(), PlanItem.Staged(second, 0).GetHashCode()));
    }

    [TestMethod]
    public void Equals_TwoStagedItemsOfOneTable_AreDifferentAndHashByIndex()
    {
        var table = new StagingTable();
        StageText(table, 1, 0, "ab");
        StageText(table, 2, 0, "cd");

        Assert.AreEqual((false, 1), (PlanItem.Staged(table, 0) == PlanItem.Staged(table, 1), PlanItem.Staged(table, 1).GetHashCode()));
    }

    [TestMethod]
    public void Members_StagedItem_ReadTheStagedFields()
    {
        var table = new StagingTable();
        table.BeginClient(7, 4);
        StructStaging.Stage(table, new Item(new ItemId(7, 4), new ItemId(3, 9), null, null, null, new ContentString("xyz")));
        table.BeginClient(8, 0);
        StructStaging.Stage(table, new Item(new ItemId(8, 0), null, null, new ItemId(7, 2), "k", new ContentDeleted(2)));
        var (text, deleted) = (PlanItem.Staged(table, 0), PlanItem.Staged(table, 1));

        Assert.AreEqual(
            (new ItemId(7, 4), new ItemId(7, 6), 3, new ItemId(3, 9), false, null, false, false, false),
            (text.Id, text.LastId, text.Length, text.LeftOrigin, text.HasDeletedContent, text.ParentSub, text.HasParent, text.IsType, text.IsStored));
        Assert.AreEqual(
            (new ItemId(7, 2), null, "k", true, true, true),
            (deleted.ParentId, deleted.ParentName, deleted.ParentSub, deleted.HasParent, deleted.HasDeletedContent, deleted.IsStaged));
    }
}
