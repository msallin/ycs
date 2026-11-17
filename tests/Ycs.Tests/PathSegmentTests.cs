using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class PathSegmentTests
{
    [TestMethod]
    [DataRow("key")]
    [DataRow("")]
    public void IsKey_MapKey_IsTrue(string key)
    {
        Assert.IsTrue(new PathSegment(key, 0).IsKey);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(5)]
    public void IsKey_ArrayIndex_IsFalse(int index)
    {
        Assert.IsFalse(new PathSegment(null, index).IsKey);
    }

    [TestMethod]
    public void IsKey_SegmentsOfAnEventPath_TellKeysFromIndexes()
    {
        var doc = new YDoc();
        var outer = doc.GetMap("m");
        var list = new YArray();
        outer["list"] = list;
        var inner = new YMap();
        list.Insert(0, inner);
        System.Collections.Generic.IReadOnlyList<PathSegment> path = null;
        outer.ObserveDeep((events, _) => path = events[0].Path);

        inner["x"] = 1;

        CollectionAssert.AreEqual(new[] { true, false }, path.Select(s => s.IsKey).ToList());
    }
}
