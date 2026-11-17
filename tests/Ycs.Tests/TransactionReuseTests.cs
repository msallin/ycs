using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class TransactionReuseTests
{
    [TestMethod]
    public void Cleanup_MergeListGrewLarge_IsDroppedWithTheTransaction()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var text = doc.GetText("t");
        text.Insert(0, new string('x', 400));
        Transaction? transaction = null;
        var peak = 0;

        doc.Transact(t =>
        {
            transaction = t;
            for (var i = 0; i < 150; i++)
            {
                text.Remove(i, 1);
            }

            peak = t._mergeStructs.Capacity;
        });

        Assert.AreEqual((true, 0), (peak > 64, transaction!._mergeStructs.Capacity));
    }
}
