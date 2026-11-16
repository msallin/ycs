using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class TypeTableTests
{
    [TestMethod]
    public void Add_Types_GetConsecutiveIndexes()
    {
        var table = new TypeTable();
        var a = new YArray();
        var b = new YMap();

        var indexes = (table.Add(a), table.Add(b));

        Assert.AreEqual((0, 1), indexes);
        Assert.IsTrue(ReferenceEquals(a, table[0]) && ReferenceEquals(b, table[1]));
    }

    [TestMethod]
    public void Add_AfterCollect_NeverReusesTheIndex()
    {
        var table = new TypeTable();
        var first = table.Add(new YArray());
        table.Collect(first);

        var next = table.Add(new YArray());

        Assert.AreEqual((0, 1, 2, 1, true), (first, next, table.Count, table.LiveCount, table[first] == null));
    }

    [TestMethod]
    public void Collect_Twice_CountsOnce()
    {
        var table = new TypeTable();
        var i = table.Add(new YArray());

        table.Collect(i);
        table.Collect(i);

        Assert.AreEqual(0, table.LiveCount);
    }
}
