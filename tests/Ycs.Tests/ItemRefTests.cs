using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>The reference to a struct of the store (<see cref="ItemRef"/>): null, identity, and the members it reads through.</summary>
[TestClass]
public class ItemRefTests
{
    private static Item NewItem(long client, long clock, string text) =>
        new(new ItemId(client, clock), null, null, null, null, null, null, new ContentString(text));

    [TestMethod]
    public void Default_IsNull_EqualsTheReferenceToNull()
    {
        var reference = default(ItemRef);

        Assert.IsTrue((reference.IsNull, reference == ItemRef.Of(null), reference.IsItem, reference.IsGC, reference.ToStruct()) is (true, true, false, false, null));
    }

    [TestMethod]
    public void Equals_SameObject_IsEqualWithTheObjectsHash()
    {
        var item = NewItem(1, 0, "ab");
        var a = ItemRef.Of(item);
        var b = ItemRef.Of(item);

        Assert.IsTrue((a == b, a != b, a.Equals(b), a.Equals((object)b), a.GetHashCode() == RuntimeHelpers.GetHashCode(item)) is (true, false, true, true, true));
    }

    [TestMethod]
    public void Equals_EqualFieldsOtherObject_IsNotEqual()
    {
        var a = ItemRef.Of(NewItem(1, 0, "ab"));
        var b = ItemRef.Of(NewItem(1, 0, "ab"));

        Assert.IsTrue((a == b, a != b, a.Equals(b), a.Equals((object)b), a.Equals((object)null), a == default) is (false, true, false, false, false, false));
    }

    [TestMethod]
    public void HashSet_TwoReferencesToOneStruct_HoldsOne()
    {
        var item = NewItem(1, 0, "ab");
        var set = new HashSet<ItemRef> { ItemRef.Of(item), ItemRef.Of(item), ItemRef.Of(NewItem(1, 0, "ab")) };

        Assert.AreEqual(2, set.Count);
    }

    [TestMethod]
    public void Members_Item_ReadAndWriteTheItem()
    {
        var left = NewItem(1, 0, "a");
        var item = NewItem(1, 1, "bc");
        var reference = ItemRef.Of(item);

        reference.Left = ItemRef.Of(left);
        reference.LeftOrigin = left.Id;
        reference.Keep = true;
        reference.ParentSub = "k";

        Assert.AreEqual(
            (new ItemId(1, 1), 2, new ItemId(1, 2), true, false, true, (ItemId?)new ItemId(1, 0), true, "k", true),
            (reference.Id, reference.Length, reference.LastId, reference.IsItem, reference.IsGC, item.Left == left, item.LeftOrigin, item.Keep, item.ParentSub, reference.ToItem() == item));
    }

    [TestMethod]
    public void Members_GcStruct_ReadTheStructAndThrowForAnItemsMember()
    {
        var reference = ItemRef.Of(new GC(new ItemId(2, 5), 3));

        Assert.AreEqual((true, false, true, new ItemId(2, 5), 3), (reference.IsGC, reference.IsItem, reference.Deleted, reference.Id, reference.Length));
        Assert.ThrowsExactly<NullReferenceException>(() => reference.Right);
    }

    [TestMethod]
    public void IsSameKind_ItemAndGcStruct_ComparesTheKinds()
    {
        var item = ItemRef.Of(NewItem(1, 0, "a"));
        var gc = ItemRef.Of(new GC(new ItemId(1, 1), 1));

        Assert.IsTrue((item.IsSameKind(ItemRef.Of(NewItem(1, 1, "b"))), item.IsSameKind(gc), gc.IsSameKind(ItemRef.Of(new GC(new ItemId(1, 2), 1)))) == (true, false, true));
    }
}
