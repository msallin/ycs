using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// A user can hold a type after it was collected (docs/design-item-layout.md, 2.4): its list is gone, so are its
/// search markers, and what the user does with it touches no other type.
/// </summary>
[TestClass]
public class CollectedTypeMarkersTests
{
    private static YArray Filled(YDoc doc, string name)
    {
        var array = doc.GetArray(name);
        array.Add(Enumerable.Range(0, 400).Select(i => (object)i).ToList().Select(YValue.FromObject).ToArray());
        for (var i = 0; i < 400; i += 37)
        {
            array[i].ToObject();
            array.Insert(i, "x");
        }

        return array;
    }

    [TestMethod]
    public void Collect_ArrayWithMarkers_ClearsThem()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var map = doc.GetMap("m");
        var held = new YArray();
        map["k"] = held;
        held.Add(Enumerable.Range(0, 400).Select(i => (object)i).ToList().Select(YValue.FromObject).ToArray());
        held[300].ToObject();
        var before = held.DescribeSearchMarkers();

        map.Remove("k");

        Assert.AreEqual((false, ""), (before == "", held.DescribeSearchMarkers()));
    }

    [TestMethod]
    public void Insert_IntoACollectedArray_ChangesNoOtherTypesMarkers()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var map = doc.GetMap("m");
        var held = new YArray();
        map["k"] = held;
        held.Add(Enumerable.Range(0, 400).Select(i => (object)i).ToList().Select(YValue.FromObject).ToArray());
        held[300].ToObject();
        var other = Filled(doc, "o");
        map.Remove("k");
        var markers = other.DescribeSearchMarkers();
        var content = other.ToJsonString();
        var violations = other.SearchMarkerViolations();

        held.Insert(0, "q");
        held.Insert(0, "r");

        Assert.AreEqual((markers, content, violations), (other.DescribeSearchMarkers(), other.ToJsonString(), other.SearchMarkerViolations()));
    }
}
