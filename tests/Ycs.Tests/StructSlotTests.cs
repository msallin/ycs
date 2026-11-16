using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Every struct of the store has one live slot of the document's slab, which shadows its object
/// (<c>StructStore.CheckForTests</c> compares them after every transaction of the suite; these tests name the rules).
/// </summary>
[TestClass]
public class StructSlotTests
{
    private static int Structs(YDoc doc) => doc.Store.Clients.Values.Sum(list => list.Count);

    [TestMethod]
    public void Insert_Text_HoldsOneLiveSlotForEveryStruct()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var text = doc.GetText("t");

        text.Insert(0, "abc");
        text.Insert(1, "x");

        Assert.AreEqual((3, 3), (Structs(doc), doc.Store.Slab.LiveCount));
    }

    [TestMethod]
    public void Cleanup_MergingAdjacentItems_FreesTheRightPartsSlot()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 }) { RunExtensionsDisabledForTests = true };
        var text = doc.GetText("t");

        text.Insert(0, "ab");
        text.Insert(2, "cd");
        text.Insert(4, "ef");

        Assert.AreEqual((1, 1), (Structs(doc), doc.Store.Slab.LiveCount));
    }

    [TestMethod]
    public void Collect_ReplacingAnItemByGc_KeepsTheHandle()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var array = doc.GetArray("a");
        var outer = new YMap();
        array.Push(new object[] { outer });
        outer.Set("k", 1);
        var id = new ItemId(1, 1);
        var handle = doc.Store.Find(id).Handle;
        var live = doc.Store.Slab.LiveCount;

        array.Delete(0, 1);

        var after = doc.Store.Find(id);
        Assert.AreEqual((true, handle, live), (after.IsGC, after.Handle, doc.Store.Slab.LiveCount));
    }

    [TestMethod]
    public void Apply_ItemOfACollectedParent_StoresAGcStructWithASlot()
    {
        var first = new YDoc(new YDocOptions { ClientId = 1 });
        var array = first.GetArray("a");
        var map = new YMap();
        array.Push(new object[] { map });
        var second = new YDoc(new YDocOptions { ClientId = 2 });
        second.ApplyUpdate(first.EncodeStateAsUpdate());
        ((YMap)second.GetArray("a").Get(0)).Set("k", 1);

        array.Delete(0, 1);
        first.ApplyUpdate(second.EncodeStateAsUpdate(first.EncodeStateVector()));

        var stored = first.Store.Find(new ItemId(2, 0));
        Assert.AreEqual((true, true, Structs(first)), (stored.IsGC, stored.Handle >= 0, first.Store.Slab.LiveCount));
    }
}
