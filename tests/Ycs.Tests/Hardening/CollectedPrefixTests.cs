using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// An item whose first clocks the document holds as a GC struct, but which has a parent: Yjs 13.6.32 throws (a
/// TypeError in the conflict resolution, after <c>getItemCleanEnd</c> returned the GC struct as its left).
/// </summary>
[TestClass]
public class CollectedPrefixTests
{
    // A GC struct of client 5, clocks 0 to 2.
    private static readonly byte[] Collected = [1, 1, 5, 0, 0, 3, 0];

    // An item of client 5 from clock 0, "hello" in the root text "t".
    private static readonly byte[] Hello = [1, 1, 5, 0, 4, 1, 1, (byte)'t', 5, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o', 0];

    [TestMethod]
    public void ApplyUpdate_ItemContinuingACollectedStruct_ThrowsAndLeavesTheTextEmpty()
    {
        var doc = new YDoc();
        var text = doc.GetText("t");
        doc.ApplyUpdate(Collected);

        var e = Assert.ThrowsExactly<YUpdateException>(() => doc.ApplyUpdate(Hello));

        Assert.AreEqual((true, "", 0, 3L), (e.PartlyApplied, text.ToString(), text.Length, doc.Store.GetState(5)));
    }
}
