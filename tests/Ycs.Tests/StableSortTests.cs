using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class StableSortTests
{
    [TestMethod]
    [DataRow(5)]
    [DataRow(33)]
    [DataRow(1000)]
    public void Sort_ManyTies_KeepsTheOrderOfEqualElements(int count)
    {
        // Keys 0..6 repeating backwards; the value is the original position.
        var list = Enumerable.Range(0, count).Select(i => (Key: 6 - (i % 7), Position: i)).ToList();
        var expected = list.OrderBy(e => e.Key).ToList();

        StableSort.Sort(list, static (a, b) => a.Key.CompareTo(b.Key));

        CollectionAssert.AreEqual(expected, list);
    }
}
