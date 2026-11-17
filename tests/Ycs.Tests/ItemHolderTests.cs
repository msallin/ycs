using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// A holder inside a store (a type's start and item, its map values, the search markers, the push hint, the leaves of a
/// client's list) holds the slot's handle and no slab; no holder names a freed slot (<c>StructStore.CheckForTests</c>),
/// and in Debug a read through a holder whose slot was freed throws.
/// </summary>
[TestClass]
public class ItemHolderTests
{
    private static YDoc NewDoc() => new(new YDocOptions { ClientId = 1 }) { RunExtensionsDisabledForTests = true };

    private static (YDoc Doc, YArray Array, ItemRef Stray) ArrayWithAFreedSlotAsItsStart()
    {
        var doc = NewDoc();
        var array = doc.GetArray("a");
        array.Add(1);
        var slab = doc.Store.Slab;
        var stray = new ItemRef(slab, slab.Allocate());
        array._start = stray;
        slab.Free(stray.Handle);
        return (doc, array, stray);
    }

#if !DEBUG
    [TestMethod]
    public void Release_ItemHandle_IsTheHandleAlone()
    {
        var fields = typeof(ItemHandle).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

        Assert.AreEqual("_handle", string.Join(",", fields.Select(f => f.Name)));
    }
#endif

    [TestMethod]
    public void Hold_DefaultReference_IsNone()
    {
        Assert.AreEqual((true, -1), (default(ItemRef).Hold.IsNone, default(ItemRef).Hold.Value));
    }

    [TestMethod]
    public void In_HeldReference_NamesTheSameSlot()
    {
        var doc = NewDoc();
        var array = doc.GetArray("a");
        array.Add(1);
        var first = array._start;

        Assert.AreEqual((first, true), (first.Hold.In(doc.Store.Slab), ItemHandle.None.In(doc.Store.Slab).IsNull));
    }

    [TestMethod]
    public void Check_TypeStartAtAFreedSlot_Throws()
    {
        var (doc, _, _) = ArrayWithAFreedSlotAsItsStart();

        var thrown = Assert.ThrowsExactly<InvalidOperationException>(() => doc.Store.CheckForTests());

        Assert.IsTrue(thrown.Message.Contains("freed", StringComparison.Ordinal), thrown.Message);
    }

    [TestMethod]
    public void Check_MapValueAtAFreedSlot_Throws()
    {
        var doc = NewDoc();
        var map = doc.GetMap("m");
        map["k"] = 1;
        var slab = doc.Store.Slab;
        var stray = new ItemRef(slab, slab.Allocate());
        map._map["k"] = stray.Hold;
        slab.Free(stray.Handle);

        var thrown = Assert.ThrowsExactly<InvalidOperationException>(() => doc.Store.CheckForTests());

        Assert.IsTrue(thrown.Message.Contains("freed", StringComparison.Ordinal), thrown.Message);
    }

#if DEBUG
    [TestMethod]
    public void Read_HolderOfAReusedSlot_Throws()
    {
        var doc = NewDoc();
        var array = doc.GetArray("a");
        array.Add(1);
        var slab = doc.Store.Slab;
        var handle = array._start.Handle;

        slab.Free(handle);
        slab.AllowReuse();
        Assert.AreEqual(handle, slab.Allocate());

        Assert.ThrowsExactly<InvalidOperationException>(() => _ = array._start.Length);
    }
#endif
}
