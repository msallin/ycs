using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// What a user reads from a type held after it was collected (its parent item deleted and garbage-collected). Every
/// expectation was checked against Yjs 13.6.32 (interop/js): reads see an empty type, never an exception.
/// </summary>
[TestClass]
public class CollectedTypeReadTests
{
    private static T Collected<T>(T held, Action<T> fill) where T : YType
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var map = doc.GetMap("r");
        map["k"] = held;
        fill(held);
        map.Remove("k");
        return held;
    }

    private static YArray Array() => Collected(new YArray(), a => a.Add(1, 2, 3));

    private static YMap Map() => Collected(new YMap(), m => { m["a"] = 1; m["b"] = 2; });

    [TestMethod]
    public void Get_CollectedArray_HasNoValue()
    {
        var held = Array();

        Assert.AreEqual((false, false), (held.TryGetValue(0, out _), held.TryGetValue(5, out _)));
    }

    [TestMethod]
    public void Length_CollectedArray_IsZeroAsInYjs()
    {
        Assert.AreEqual(0, Array().Length);
    }

    [TestMethod]
    public void Read_CollectedArray_IsEmptyAsInYjs()
    {
        var held = Array();

        Assert.AreEqual((0, 0, 0, 0), (held.ToArray().Length, held.Slice(0, 0).Length, held.Count, held.ToList().Count));
    }

    [TestMethod]
    public void ToJson_CollectedArray_IsAnEmptyListAsInYjs()
    {
        Assert.AreEqual("[]", Array().ToJsonString());
    }

    [TestMethod]
    public void Insert_IntoACollectedArray_LeavesItEmptyAsInYjs()
    {
        var held = Array();

        held.Add(9);
        held.Insert(0, 9);
        held.Insert(0, 9);

        Assert.AreEqual(0, held.Length);
    }

    [TestMethod]
    public void RemoveRange_FromACollectedArray_Throws()
    {
        var held = Array();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => held.RemoveRange(0, 1));
    }

    [TestMethod]
    public void Read_CollectedMap_IsEmptyAsInYjs()
    {
        var held = Map();

        Assert.AreEqual((false, false, 0, 0, 0), (held.TryGetValue("a", out _), held.ContainsKey("a"), held.Count, held.Keys.Count(), held.Values.Count()));
    }

    [TestMethod]
    public void Set_OnACollectedMap_ReadsBackNothingAsInYjs()
    {
        var held = Map();

        held["c"] = 3;

        Assert.AreEqual((false, 0), (held.TryGetValue("c", out _), held.Count));
    }

    [TestMethod]
    public void Read_CollectedText_IsEmptyAsInYjs()
    {
        var held = Collected(new YText(), t => t.Insert(0, "hello"));

        Assert.AreEqual(("", 0, 0), (held.ToString(), held.Length, held.ToDelta().Count));
    }

    [TestMethod]
    public void Edit_CollectedText_LeavesItEmptyAsInYjs()
    {
        var held = Collected(new YText(), t => t.Insert(0, "hello"));

        held.Insert(0, "Z");

        Assert.AreEqual("", held.ToString());
    }

    [TestMethod]
    public void Read_CollectedXmlElement_IsEmptyAsInYjs()
    {
        var held = Collected(new YXmlElement("p"), e =>
        {
            e.Insert(0, new YXmlText("x"));
            e.SetAttribute("a", "1");
        });

        Assert.AreEqual(("<p></p>", 0, YValue.Null, 0, 0), (held.ToString(), held.Count, held.GetAttribute("a"),
            held.GetAttributes().Count, held.ToArray().Length));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => held[0]);
    }
}
