using System;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// The slot of an item holds its content object and its map key, and a freed slot holds neither
/// (<c>StructStore.CheckForTests</c> compares them with the objects).
/// </summary>
[TestClass]
public class StructContentSlotTests
{
    private static YDoc NewDoc() => new(new YDocOptions { ClientId = 1 }) { RunExtensionsDisabledForTests = true };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference MergeAwayRightContent(YDoc doc)
    {
        var text = doc.GetText("t");
        WeakReference right = null;
        doc.Transact(_ =>
        {
            text.Insert(0, "ab");
            text.Insert(2, "cd");
            right = new WeakReference(doc.Store.Find(new ItemId(1, 2)).Content);
        });
        return right;
    }

    [TestMethod]
    public void Cleanup_MergingTheRightPart_ReleasesItsContent()
    {
        var doc = NewDoc();
        var right = MergeAwayRightContent(doc);

        System.GC.Collect();
        System.GC.WaitForPendingFinalizers();
        System.GC.Collect();

        Assert.AreEqual((1, 1, false), (doc.Store.Clients[1].Count, doc.Store.Slab.LiveCount, right.IsAlive));
    }

    [TestMethod]
    public void Insert_MapValue_HoldsTheContentAndTheKeyInTheSlot()
    {
        var doc = NewDoc();
        var map = doc.GetMap("m");

        map["k"] = 1;

        var item = doc.Store.Find(new ItemId(1, 0));
        Assert.AreEqual((true, "k"), (ReferenceEquals(doc.Store.Slab.GetRef(item.Handle), item.Content), doc.Store.Slab.GetKey(item.Handle)));
    }

    [TestMethod]
    public void Split_InTheMiddleOfAnItem_GivesBothPartsTheirContent()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, "abcd");

        text.Insert(2, "X");

        var left = doc.Store.Find(new ItemId(1, 0));
        var right = doc.Store.Find(new ItemId(1, 2));
        Assert.AreEqual((true, true, false), (ReferenceEquals(doc.Store.Slab.GetRef(left.Handle), left.Content), ReferenceEquals(doc.Store.Slab.GetRef(right.Handle), right.Content), ReferenceEquals(left.Content, right.Content)));
    }

    [TestMethod]
    public void Remove_MapValue_PutsContentDeletedInTheSlot()
    {
        var doc = NewDoc();
        var map = doc.GetMap("m");
        map["k"] = "v";
        var handle = doc.Store.Find(new ItemId(1, 0)).Handle;

        map.Remove("k");

        Assert.AreEqual(true, doc.Store.Slab.GetRef(handle) is ContentDeleted);
    }

    [TestMethod]
    public void Collect_ReplacingAnItemByGc_LeavesNoContentOrKeyInTheSlot()
    {
        var doc = NewDoc();
        var array = doc.GetArray("a");
        var outer = new YMap();
        array.Add(outer);
        outer["k"] = 1;
        var item = doc.Store.Find(new ItemId(1, 1));
        var handle = item.Handle;

        array.RemoveAt(0);

        Assert.AreEqual((true, handle, null, null), (doc.Store.Find(new ItemId(1, 1)).IsGC, doc.Store.Find(new ItemId(1, 1)).Handle, doc.Store.Slab.GetRef(handle), doc.Store.Slab.GetKey(handle)));
    }

    [TestMethod]
    public void Free_ASlotOfAMapItem_ClearsItsContentAndKey()
    {
        var doc = NewDoc();
        var map = doc.GetMap("m");
        var text = new YText();
        map["k"] = text;
        var slab = doc.Store.Slab;
        var handle = doc.Store.Find(new ItemId(1, 0)).Handle;
        var live = slab.LiveCount;

        slab.Free(handle);

        Assert.AreEqual((live - 1, null, null), (slab.LiveCount, slab.GetRef(handle), slab.GetKey(handle)));
    }
}
