using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class ClientTableTests
{
    [TestMethod]
    public void GetOrAdd_NewAndKnownClients_GiveStableConsecutiveIndexes()
    {
        var table = new ClientTable();

        var indexes = (table.GetOrAdd(9_007_199_254_740_991), table.GetOrAdd(5), table.GetOrAdd(9_007_199_254_740_991));

        Assert.AreEqual((0, 1, 0), indexes);
        Assert.AreEqual((2, 5L), (table.Count, table[1]));
    }

    [TestMethod]
    public void TryGetIndex_UnknownClient_IsFalseAndAddsNothing()
    {
        var table = new ClientTable();
        table.GetOrAdd(1);

        Assert.AreEqual((false, true, 1), (table.TryGetIndex(2, out _), table.TryGetIndex(1, out var i) && i == 0, table.Count));
    }
}
