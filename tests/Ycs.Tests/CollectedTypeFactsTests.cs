using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// A user can hold a type or a subdocument after it was collected, when its struct has merged away (and a slot
/// can be reused, docs/design-item-layout.md, 2.4): it still answers as Yjs' orphaned item does. The expected
/// values are Yjs 13.6.32's, for the same script.
/// </summary>
[TestClass]
public class CollectedTypeFactsTests
{
    // inner's item is a GC struct that merges with its neighbours' (all one client, consecutive clocks); outer's item
    // is a deleted item that merges into the deleted item left of it.
    private static (YDoc Doc, YArray Array, YMap Outer, YMap Inner) Collected()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var array = doc.GetArray("a");
        var outer = new YMap();
        var inner = new YMap();
        array.Push(new object[] { "x" });
        array.Push(new object[] { outer });
        outer.Set("a1", 1);
        outer.Set("i", inner);
        outer.Set("b1", 2);
        inner.Set("p", "v");

        array.Delete(0, 2);

        return (doc, array, outer, inner);
    }

    [TestMethod]
    public void Parent_OfCollectedTypes_IsTheTypeTheyWereIn()
    {
        var (_, array, outer, inner) = Collected();

        Assert.AreEqual((true, true), (ReferenceEquals(inner.Parent, outer), ReferenceEquals(outer.Parent, array)));
    }

    [TestMethod]
    public void FromTypeIndex_OfCollectedTypes_NamesTheirItems()
    {
        var (_, _, outer, inner) = Collected();

        var ids = new[] { inner, outer }.Select(t => RelativePosition.FromTypeIndex(t, 0).TypeId).ToArray();

        CollectionAssert.AreEqual(new ID?[] { new ID(1, 3), new ID(1, 1) }, ids);
    }

    [TestMethod]
    public void Set_OnACollectedType_ChangesNothingVisibleAndEncodesAsYjs()
    {
        var (doc, _, _, inner) = Collected();

        inner.Set("z", 1);

        Assert.AreEqual(
            ("0", "1,3,1,0,1,1,1,97,2,0,4,33,0,1,3,1,122,1,1,1,1,0,7"),
            (inner.Count.ToString(), string.Join(",", doc.EncodeStateAsUpdate())));
    }

    [TestMethod]
    public void Changes_OfACollectedTypeOutsideADeletedParent_AreNotReported()
    {
        var (doc, array, outer, _) = Collected();
        var events = 0;
        array.DeepEventHandler += (_, _) => events++;

        outer.Set("z", 1);

        Assert.AreEqual((0, 0), (events, array.Length));
    }

    [TestMethod]
    public void Delete_OfASubdocumentWhoseItemIsCollected_ReportsItRemoved()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var map = doc.GetMap("m");
        var sub = new YDoc(new YDocOptions { Guid = "g", ShouldLoad = false });
        var log = new System.Collections.Generic.List<string>();
        doc.SubdocsChanged += (_, e) => log.Add($"{e.Loaded.Count}{e.Added.Count}{e.Removed.Count}");
        map.Set("s", sub);
        map.Set("t", 1);

        doc.Transact(_ =>
        {
            map.Delete("s");
            map.Delete("t");
        });
        sub.Load();
        sub.Destroy();

        Assert.AreEqual("010,001,001", string.Join(",", log));
    }
}
