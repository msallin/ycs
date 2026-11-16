using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class EventApiTests
{
    [TestMethod]
    public void Observe_TypedByTheSharedType_GetsItsEvent()
    {
        var doc = new YDoc();
        YMapEvent mapEvent = null;
        YXmlEvent xmlEvent = null;
        doc.GetMap("m").Observe(e => mapEvent = e);
        doc.GetXmlFragment("x").Observe(e => xmlEvent = e);

        doc.GetMap("m").Set("a", 1);
        doc.GetXmlFragment("x").Insert(0, new[] { new YXmlElement("p") });

        Assert.AreEqual((true, true), (mapEvent?.KeysChanged.Contains("a"), xmlEvent?.ChildListChanged));
    }

    [TestMethod]
    public void Observe_AGeneralHandler_ObservesAnyEvent()
    {
        var doc = new YDoc();
        var seen = new List<string>();
        Action<YEvent> handler = e => seen.Add(e.GetType().Name);
        doc.GetArray("a").Observe(handler);
        doc.GetText("t").Observe(handler);

        doc.GetArray("a").Add(new object[] { 1 });
        doc.GetText("t").Insert(0, "x");

        CollectionAssert.AreEqual(new[] { "YArrayEvent", "YTextEvent" }, seen);
    }

    [TestMethod]
    public void Observe_AfterDispose_IsNotCalled()
    {
        var doc = new YDoc();
        var calls = 0;
        var subscription = doc.GetMap("m").Observe(_ => calls++);

        subscription.Dispose();
        doc.GetMap("m").Set("a", 1);

        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public void Path_OfAMapAndAListStep_TellsKeyFromIndex()
    {
        var doc = new YDoc();
        var root = doc.GetMap("m");
        var list = new YArray();
        var inner = new YMap();
        root.Set("list", list);
        list.Add(new object[] { 1, 2, inner });
        IReadOnlyList<PathSegment> path = null;
        root.ObserveDeep((events, _) => path = events[0].Path);

        inner.Set("k", 1);

        CollectionAssert.AreEqual(
            new[] { new PathSegment("list", 0), new PathSegment(null, 2) },
            path.ToArray());
        Assert.AreEqual("list/2", string.Join("/", path));
    }

    [TestMethod]
    public void Updated_EachEventCarriesItsOwnBytesTheHandlerMayKeep()
    {
        var doc = new YDoc();
        var kept = new List<ReadOnlyMemory<byte>>();
        doc.Updated += (_, e) => kept.Add(e.Update);
        var text = doc.GetText("t");

        text.Insert(0, "a");
        text.Insert(1, "b");

        var replica = new YDoc();
        foreach (var update in kept)
        {
            replica.ApplyUpdate(update.ToArray());
        }

        Assert.AreEqual(("ab", 2, false), (replica.GetText("t").ToString(), kept.Count, kept[0].Span.SequenceEqual(kept[1].Span)));
    }

    [TestMethod]
    public void Keys_OfAnUpdateAndADelete_AreKeyChangeValues()
    {
        var doc = new YDoc();
        var map = doc.GetMap("m");
        map.Set("a", 1);
        var changes = new List<IReadOnlyDictionary<string, KeyChange>>();
        map.Observe(e => changes.Add(e.Keys));

        map.Set("a", 2);
        map.Delete("a");

        Assert.AreEqual(
            (new KeyChange(KeyChangeAction.Update, 1), new KeyChange(KeyChangeAction.Delete, 2)),
            (changes[0]["a"], changes[1]["a"]));
    }
}
