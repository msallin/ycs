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
    [TestMethod]
    public void Transact_ManyChangedTypesDeleted_IsLinear()
    {
        // Each deleted map is removed from Transaction.Changed.
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var array = doc.GetArray("a");
        var maps = new List<YMap>();
        doc.Transact(_ =>
        {
            for (var i = 0; i < 40_000; i++)
            {
                var map = new YMap();
                maps.Add(map);
                array.Add(new object[] { map });
            }
        });

        var stopwatch = Stopwatch.StartNew();
        doc.Transact(_ =>
        {
            foreach (var map in maps)
            {
                map.Set("k", 1);
            }

            array.Delete(0, array.Length);
        });

        Assert.IsLessThan(2_000, stopwatch.ElapsedMilliseconds);
    }

    [TestMethod]
    public void Changed_TypeDeletedInTheTransaction_IsLeftOutAndTheOrderKept()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var array = doc.GetArray("a");
        var (first, gone, last) = (new YMap(), new YMap(), new YMap());
        array.Add(new object[] { first, gone, last });
        var changed = new List<YType>();
        doc.AfterTransaction += (_, tr) => changed = tr.Changed.Keys.ToList();

        doc.Transact(_ =>
        {
            first.Set("k", 1);
            gone.Set("k", 1);
            array.Delete(1, 1);
            last.Set("k", 1);
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
        array.Insert(0, new object[] { inner });
        var names = new Dictionary<YType, string> { [text] = "t", [map] = "m", [array] = "a", [inner] = "inner" };
        string order = null;
        map.EventHandler += (_, _) => doc.AfterTransaction += (_, tr) => order ??= string.Join(",", tr.ChangedParentTypes.Keys.Select(k => names[k]));

        doc.Transact(_ =>
        {
            text.Insert(0, "x");
            inner.Set("k", 1);
            map.Set("k", 1);
        });

        Assert.AreEqual("t,inner,a,m", order);
    }
}
