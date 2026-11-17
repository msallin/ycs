using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>A reference to a struct of the store reads its slot, whatever else holds; one that left the store, in Debug, throws.</summary>
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

        Assert.AreEqual((99, true, true), (item.Length, item.Deleted, item.Keep));
    }

    [TestMethod]
    public void Write_Member_GoesToTheSlot()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, "abc");
        var item = text._start;

        item.Length = 7;
        item.Marker = true;
        item.Redone = new ItemId(9, 9);

        var slot = doc.Store.Slab.Read(item.Handle);
        Assert.AreEqual((7, Slot.MarkerFlag, Slot.HasRedoneFlag), (slot.Length, slot.Info & Slot.MarkerFlag, slot.Info & Slot.HasRedoneFlag));
        item.Length = 3;
        item.Marker = false;
        item.Redone = null;
    }

    [TestMethod]
    public void IsFreed_StructMergedAway_IsTrueUntilTheSlotIsReused()
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

        Assert.AreEqual((true, false), (merged.IsFreed, doc.Store.Find(new ItemId(1, 0)).IsFreed));
    }

    [TestMethod]
    public void IsFreed_DefaultReference_IsTrue()
    {
        Assert.IsTrue(default(ItemRef).IsFreed);
    }

#if DEBUG
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

        Assert.IsTrue(thrown.Message.Contains("outlived its struct", StringComparison.Ordinal));
    }
#endif
}
