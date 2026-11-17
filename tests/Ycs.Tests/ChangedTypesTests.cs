using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// <see cref="Transaction.Changed"/> and <see cref="Transaction.ChangedParentTypes"/> keep the order
/// in which the types changed (Yjs' Maps), also when changed types are deleted in the transaction or
/// events are built late.
/// </summary>
[TestClass]
public class ChangedTypesTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Transact_ManyChangedTypesDeleted_IsLinear()
    {
        // Each deleted map is removed from Transaction.Changed.
        ScalingProbe.AssertNotQuadratic(
            n =>
            {
                var doc = new YDoc(new YDocOptions { ClientId = 1 });
                var array = doc.GetArray("a");
                var maps = new List<YMap>();
                doc.Transact(_ =>
                {
                    for (var i = 0; i < n; i++)
                    {
                        var map = new YMap();
                        maps.Add(map);
                        array.Add(map);
                    }
                });

                return () => doc.Transact(_ =>
                {
                    foreach (var map in maps)
                    {
                        map["k"] = 1;
                    }

                    array.RemoveRange(0, array.Length);
                });
            },
            40_000,
            TestContext);
    }

    [TestMethod]
    public void Changed_TypeDeletedInTheTransaction_IsLeftOutAndTheOrderKept()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var array = doc.GetArray("a");
        var (first, gone, last) = (new YMap(), new YMap(), new YMap());
        array.Add(first, gone, last);
        var changed = new List<YType>();
        doc.AfterTransaction += (_, tr) => changed = tr.Changed.Keys.ToList();

        doc.Transact(_ =>
        {
            first["k"] = 1;
            gone["k"] = 1;
            array.RemoveRange(1, 1);
            last["k"] = 1;
        });

        CollectionAssert.AreEqual(new YType[] { first, array, last }, changed);
    }

    [TestMethod]
    public void ChangedParentTypes_DocumentListenerAttachedByAnObserver_InTheOrderOfChange()
    {
        // Yjs: "t,inner,a,m" (js/late.mjs of the second review): the text changed first.
        var doc = new YDoc();
        var text = doc.GetText("t");
        var map = doc.GetMap("m");
        var array = doc.GetArray("a");
        var inner = new YMap();
        array.Insert(0, inner);
        var names = new Dictionary<YType, string> { [text] = "t", [map] = "m", [array] = "a", [inner] = "inner" };
        string order = null;
        map.Observe(_ => doc.AfterTransaction += (_, tr) => order ??= string.Join(",", tr.ChangedParentTypes.Keys.Select(k => names[k])));

        doc.Transact(_ =>
        {
            text.Insert(0, "x");
            inner["k"] = 1;
            map["k"] = 1;
        });

        Assert.AreEqual("t,inner,a,m", order);
    }
}
