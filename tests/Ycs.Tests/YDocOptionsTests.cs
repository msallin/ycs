using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class YDocOptionsTests
{
    [TestMethod]
    public void Docs_MadeFromOneOptionsObject_HaveTheirOwnRandomGuid()
    {
        var options = new YDocOptions();

        var (a, b) = (new YDoc(options), new YDoc(options));

        Assert.AreNotEqual(a.Guid, b.Guid);
        var guid = a.Guid;
        Assert.AreEqual(guid, a.Guid);
    }

    [TestMethod]
    public void Options_ShowThroughTheDocument()
    {
        var options = new YDocOptions { Guid = "g", GarbageCollection = false, AutoLoad = true, ShouldLoad = false, CollectionId = "c", Meta = "m", ClientId = 5 };

        var doc = new YDoc(options with { AutoLoad = false });

        Assert.AreEqual(("g", false, false, false, "c", "m", 5L), (doc.Guid, doc.GarbageCollection, doc.AutoLoad, doc.ShouldLoad, doc.CollectionId, (string)doc.Meta, doc.ClientId));
    }

    [TestMethod]
    public void GarbageCollectionFilter_FalseKeepsTheItem()
    {
        var kept = 0;
        var doc = new YDoc(new YDocOptions { GarbageCollectionFilter = _ => { kept++; return false; } });
        var text = doc.GetText("t");

        text.Insert(0, "abc");
        text.Remove(0, 3);

        Assert.IsTrue(kept > 0);
    }
}
