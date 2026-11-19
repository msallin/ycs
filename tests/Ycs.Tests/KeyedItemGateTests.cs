using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>What lets the garbage collection check be skipped: no key in the update and none ever in the store.</summary>
[TestClass]
public class KeyedItemGateTests
{
    [TestMethod]
    public void HasKeyedItems_TextOnlyDocument_IsFalseUntilAKeyIsSet()
    {
        var doc = new YDoc();
        doc.GetText("t").Insert(0, "abc");
        var textOnly = doc.Store.HasKeyedItems;

        doc.GetMap("m")["k"] = 1L;

        Assert.AreEqual((false, true), (textOnly, doc.Store.HasKeyedItems));
    }

    [TestMethod]
    public void HasKeyedItems_UpdateWithAKey_IsSetOnTheReceivingDocument()
    {
        var source = new YDoc();
        source.GetMap("m")["k"] = 1L;
        var target = new YDoc();

        target.ApplyUpdate(source.EncodeStateAsUpdate());

        Assert.IsTrue(target.Store.HasKeyedItems);
    }

    [TestMethod]
    public void HasKeys_AfterSetParentSubAndReset_IsTrueThenFalse()
    {
        var table = new StagingTable();
        table.BeginClient(1, 0);
        var i = table.Add(StagedKind.Any, 1);
        table.SetParentSub(i, "k");
        var set = table.HasKeys;

        table.Reset();

        Assert.AreEqual((true, false), (set, table.HasKeys));
    }
}
