using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>In Debug a read of a slot checks that the slot's owner is the struct that reads it, and a freed slot is poisoned.</summary>
[TestClass]
public class SlabOwnerCheckTests
{
    private static YDoc NewDoc() => new(new YDocOptions { ClientId = 1 }) { RunExtensionsDisabledForTests = true };

    private static (YDoc Doc, ItemRef First, ItemRef Second) TwoItems()
    {
        var doc = NewDoc();
        var array = doc.GetArray("a");
        array.Add(1);
        array.Add(new YMap());
        return (doc, array._start, array._start.Right);
    }

#if DEBUG
    [TestMethod]
    public void Read_SlotOwnedByAnotherStruct_Throws()
    {
        var (doc, first, second) = TwoItems();
        doc.Store.Slab.SetOwner(first.Handle, second.ToStruct());

        var thrown = Assert.ThrowsExactly<InvalidOperationException>(() => _ = first.Length);

        Assert.IsTrue(thrown.Message.Contains("another struct", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Read_HandleOfAReusedSlot_Throws()
    {
        var (doc, first, second) = TwoItems();
        var slab = doc.Store.Slab;
        var handle = first.Handle;
        var stale = first.ToStruct();

        // What a holder of a bare handle sees: the slot freed, then named by another struct.
        slab.Free(handle);
        slab.AllowReuse();
        var reused = slab.Allocate();
        slab.SetOwner(reused, second.ToStruct());
        stale.Handle = handle;

        Assert.AreEqual(handle, reused);
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = ItemRef.Of(stale).Length);
    }

    [TestMethod]
    public void Right_LinkToAFreedSlot_Throws()
    {
        var (doc, first, _) = TwoItems();
        var slab = doc.Store.Slab;
        var freed = slab.Allocate();
        slab.Free(freed);
        slab.At(first.Handle).Right = freed;

        var thrown = Assert.ThrowsExactly<InvalidOperationException>(() => _ = first.Right);

        Assert.IsTrue(thrown.Message.Contains("outlived its target", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AllowReuse_FreedSlot_HoldsGarbageButItsKindAndLink()
    {
        var slab = new Slab(4);
        var a = slab.Allocate();
        var b = slab.Allocate();
        slab.At(a).Left = b;
        slab.At(a).Clock = 7;
        slab.Free(a);
        var before = slab.At(a).Left;

        slab.AllowReuse();

        Assert.AreEqual((b, 0x5A5A5A5A, true), (before, slab.At(a).Left, slab.At(a).IsFree));
    }
#endif

    [TestMethod]
    public void Read_StructOfTheStoreWithItsOwnSlot_Passes()
    {
        var (_, first, second) = TwoItems();

        Assert.AreEqual((1, 1, true), (first.Length, second.Length, first.Right == second));
    }
}
