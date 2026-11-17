using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// In Debug a reference keeps the generation of its slot, a free changes it, and a read through a reference that finds
/// another one throws, whether the slot is free or reused. Release has no generations.
/// </summary>
[TestClass]
public class SlabGenerationTests
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
    public void Read_ReferenceToAFreedSlot_Throws()
    {
        var (doc, first, _) = TwoItems();

        doc.Store.Slab.Free(first.Handle);

        var thrown = Assert.ThrowsExactly<InvalidOperationException>(() => _ = first.Length);
        Assert.IsTrue(thrown.Message.Contains("outlived its struct", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Read_ReferenceToAReusedSlot_Throws()
    {
        var (doc, first, _) = TwoItems();
        var slab = doc.Store.Slab;
        var handle = first.Handle;

        slab.Free(handle);
        slab.AllowReuse();
        var reused = slab.Allocate();

        Assert.AreEqual(handle, reused);
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = first.Length);
    }

    [TestMethod]
    public void Write_ReferenceToAReusedSlot_Throws()
    {
        var (doc, first, _) = TwoItems();
        var slab = doc.Store.Slab;

        slab.Free(first.Handle);
        slab.AllowReuse();
        _ = slab.Allocate();

        Assert.ThrowsExactly<InvalidOperationException>(() => first.Keep = true);
    }

    [TestMethod]
    public void Read_ReferenceToASlotOfAReleasedPage_Throws()
    {
        var slab = new Slab(4);
        var handles = new int[Slab.PageSlots + 1];
        for (var i = 0; i < handles.Length; i++)
        {
            handles[i] = slab.Allocate();
        }

        var stale = new ItemRef(slab, handles[^1]);
        slab.Free(handles[^1]);
        slab.AllowReuse();
        Assert.AreEqual(1, slab.PageCount);

        // The page is made again under the same number: its slots start with generations no earlier reference holds.
        Assert.AreEqual(handles[^1], slab.Allocate());

        Assert.ThrowsExactly<InvalidOperationException>(() => _ = stale.Length);
    }

    [TestMethod]
    public void Read_HandleThatNamesNoSlot_ThrowsInsteadOfIndexing()
    {
        var slab = new Slab(4);
        _ = slab.Allocate();

        Assert.ThrowsExactly<InvalidOperationException>(() => slab.CheckGeneration(7 << Slab.PageBits, 0));
        Assert.ThrowsExactly<InvalidOperationException>(() => slab.CheckGeneration(-1, 0));
        Assert.ThrowsExactly<InvalidOperationException>(() => slab.CheckGeneration(3, 0));
    }

    [TestMethod]
    public void Make_ReferenceToAFreeSlot_Throws()
    {
        var slab = new Slab(4);
        var handle = slab.Allocate();
        slab.Free(handle);

        Assert.ThrowsExactly<InvalidOperationException>(() => _ = new ItemRef(slab, handle));
    }
#endif

#if !DEBUG
    [TestMethod]
    public void Release_ItemRef_HoldsTheSlabAndTheHandleOnly()
    {
        var fields = typeof(ItemRef).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

        Assert.AreEqual("_handle,_slab", string.Join(",", fields.Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal)));
    }
#endif

    [TestMethod]
    public void Replace_ItemByGc_KeepsTheReferenceValid()
    {
        var doc = NewDoc();
        var array = doc.GetArray("a");
        var outer = new YMap();
        array.Add(outer);
        outer["k"] = 1;
        var held = doc.Store.Find(new ItemId(1, 1));

        array.RemoveAt(0);

        Assert.AreEqual((true, new ItemId(1, 1)), (held.IsGC, held.Id));
    }

    [TestMethod]
    public void Compare_ReferenceToAFreedSlot_NeverThrows()
    {
        var (doc, first, second) = TwoItems();
        var again = new ItemRef(doc.Store.Slab, first.Handle);

        doc.Store.Slab.Free(first.Handle);

        Assert.AreEqual((false, true, true), (first == second, first == again, first.IsFreed));
    }
}
