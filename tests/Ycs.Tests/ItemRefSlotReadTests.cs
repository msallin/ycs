using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>A reference to a struct of the store reads its slot; one that left the store reads nothing.</summary>
[TestClass]
public class ItemRefSlotReadTests
{
    private static YDoc NewDoc() => new(new YDocOptions { ClientId = 1 }) { RunExtensionsDisabledForTests = true };

    [TestMethod]
    public void Read_StructOfTheStore_ComesFromTheSlot()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, "abc");
        var item = text._start;

        doc.Store.Slab.At(item.Handle).Length = 99;
        doc.Store.Slab.At(item.Handle).Info |= Slot.DeletedFlag | Slot.KeepFlag;

        Assert.AreEqual((99, true, true, 3), (item.Length, item.Deleted, item.Keep, item.ToItem().Length));
    }

    [TestMethod]
    public void Read_ItemOutsideAStore_ComesFromTheObject()
    {
        var item = ItemRef.New(new ItemId(5, 7), default, default, new YMap(), "k", new ContentAny(new object[] { 1, 2 }));

        Assert.AreEqual((new ItemId(5, 7), 2, "k", -1), (item.Id, item.Length, item.ParentSub, item.Handle));
    }

    [TestMethod]
    public void Read_StructMergedAway_Throws()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, "a");
        ItemRef merged = default;
        doc.Transact(_ =>
        {
            text.Insert(1, "b");
            merged = doc.Store.Find(new ItemId(1, 1));
        });

        var thrown = Assert.ThrowsExactly<InvalidOperationException>(() => _ = merged.Length);

        Assert.AreEqual((true, true), (merged.IsDetached, thrown.Message.Contains("1:1", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Read_StructReplacedByAGcStruct_Throws()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, "ab");
        var item = text._start;
        doc.Store.ReplaceStruct(item, ItemRef.NewGC(item.Id, item.Length));

        Assert.ThrowsExactly<InvalidOperationException>(() => _ = item.Left);
    }
}
