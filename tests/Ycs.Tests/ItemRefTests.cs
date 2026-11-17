using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>The reference to a struct of the store (<see cref="ItemRef"/>): null, identity, and the members it reads and writes through.</summary>
[TestClass]
public class ItemRefTests
{
    private static ItemRef NewItem(Slab slab, long client, long clock, string text, YType? parent = null) =>
        ItemRef.New(slab, new ItemId(client, clock), default, default, parent ?? new YMap(), null, new ContentString(text));

    [TestMethod]
    public void Default_IsNull_ReadsAsNoStruct()
    {
        var reference = default(ItemRef);

        Assert.IsTrue((reference.IsNull, reference.IsItem, reference.IsGC, reference.Handle, reference == default) is (true, false, false, -1, true));
    }

    [TestMethod]
    public void Equals_SameSlot_IsEqualWithTheHandleAsHash()
    {
        var slab = new Slab();
        var item = NewItem(slab, 1, 0, "ab");
        var a = new ItemRef(slab, item.Handle);
        var b = new ItemRef(slab, item.Handle);

        Assert.IsTrue((a == b, a != b, a.Equals(b), a.Equals((object)b), a.GetHashCode() == item.Handle) is (true, false, true, true, true));
    }

    [TestMethod]
    public void Equals_EqualFieldsOtherSlot_IsNotEqual()
    {
        var slab = new Slab();
        var a = NewItem(slab, 1, 0, "ab");
        var b = NewItem(slab, 1, 0, "ab");

        Assert.IsTrue((a == b, a != b, a.Equals(b), a.Equals((object?)b), a.Equals((object?)null), a == default) is (false, true, false, false, false, false));
    }

    [TestMethod]
    public void Equals_SameHandleOtherSlab_IsNotEqual()
    {
        var a = NewItem(new Slab(), 1, 0, "ab");
        var b = NewItem(new Slab(), 1, 0, "ab");

        Assert.IsTrue((a.Handle == b.Handle, a == b) is (true, false));
    }

    [TestMethod]
    public void HashSet_TwoReferencesToOneStruct_HoldsOne()
    {
        var slab = new Slab();
        var item = NewItem(slab, 1, 0, "ab");
        var set = new HashSet<ItemRef> { item, new ItemRef(slab, item.Handle), NewItem(slab, 1, 0, "ab") };

        Assert.AreEqual(2, set.Count);
    }

    [TestMethod]
    public void Members_Item_ReadAndWriteTheSlot()
    {
        var slab = new Slab();
        var left = NewItem(slab, 1, 0, "a");
        var reference = NewItem(slab, 1, 1, "bc");

        reference.Left = left;
        reference.LeftOrigin = left.Id;
        reference.Keep = true;
        reference.ParentSub = "k";

        Assert.AreEqual(
            (new ItemId(1, 1), 2, new ItemId(1, 2), true, false, true, (ItemId?)new ItemId(1, 0), true, "k", left.Handle),
            (reference.Id, reference.Length, reference.LastId, reference.IsItem, reference.IsGC, reference.Left == left, reference.LeftOrigin, reference.Keep, reference.ParentSub, slab.Read(reference.Handle).Left));
    }

    [TestMethod]
    public void Redone_SetAndCleared_IsBesideTheSlabAndForgottenWithTheSlot()
    {
        var slab = new Slab();
        var item = NewItem(slab, 1, 0, "a");
        var handle = item.Handle;

        item.Redone = new ItemId(2, 5);
        var set = (item.Redone, slab.RedoneCount, (slab.Read(handle).Info & Slot.HasRedoneFlag) != 0);
        item.Redone = null;
        var cleared = (item.Redone, slab.RedoneCount, (slab.Read(handle).Info & Slot.HasRedoneFlag) != 0);
        item.Redone = new ItemId(3, 1);
        item.FreeSlot();

        Assert.AreEqual(((new ItemId(2, 5), 1, true), ((ItemId?)null, 0, false), 0), (set, cleared, slab.RedoneCount));
    }

    [TestMethod]
    public void Members_GcStruct_ReadTheRangeAndNothingElse()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var reference = doc.Store.AddGcForTests(new ItemId(2, 5), 3);

        Assert.AreEqual((true, false, true, new ItemId(2, 5), 3, true, true, null, null), (reference.IsGC, reference.IsItem, reference.Deleted, reference.Id, reference.Length, reference.Right.IsNull, reference.Left.IsNull, reference.Content, reference.Parent));
    }

    [TestMethod]
    public void IsSameKind_ItemAndGcStruct_ComparesTheKinds()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var gc = doc.Store.AddGcForTests(new ItemId(1, 0), 1);
        var other = doc.Store.AddGcForTests(new ItemId(1, 1), 1);
        var slab = doc.Store.Slab;
        var item = NewItem(slab, 2, 0, "a");

        Assert.IsTrue((item.IsSameKind(NewItem(slab, 2, 1, "b")), item.IsSameKind(gc), gc.IsSameKind(other)) == (true, false, true));
    }
}
