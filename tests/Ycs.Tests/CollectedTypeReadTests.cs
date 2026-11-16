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
        map.Set("k", held);
        fill(held);
        map.Delete("k");
        return held;
    }

    private static YArray Array() => Collected(new YArray(), a => a.Push(new object[] { 1, 2, 3 }));

    private static YMap Map() => Collected(new YMap(), m => { m.Set("a", 1); m.Set("b", 2); });

    [TestMethod]
    public void Get_CollectedArray_ReturnsNullLikeYjsUndefined()
    {
        var held = Array();

        Assert.AreEqual((null, null), (held.Get(0), held.Get(5)));
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

        Assert.AreEqual((0, 0, 0, 0), (held.ToArray().Count, held.Slice(0).Count, held.Slice(1, 2).Count, held.ToList().Count));
    }

    [TestMethod]
    public void ToJson_CollectedArray_IsAnEmptyListAsInYjs()
    {
        Assert.AreEqual("[]", System.Text.Json.JsonSerializer.Serialize(Array().ToJson()));
    }

    [TestMethod]
    public void Insert_IntoACollectedArray_LeavesItEmptyAsInYjs()
    {
        var held = Array();

        held.Push(new object[] { 9 });
        held.Insert(0, new object[] { 9 });
        held.Unshift(new object[] { 9 });

        Assert.AreEqual(0, held.Length);
    }

    [TestMethod]
    public void Delete_FromACollectedArray_ThrowsLikeYjsLengthExceeded()
    {
        var held = Array();

        Assert.ThrowsExactly<Exception>(() => held.Delete(0, 1));
    }

    [TestMethod]
    public void Read_CollectedMap_IsEmptyAsInYjs()
    {
        var held = Map();

        Assert.AreEqual((null, false, 0, 0, 0), (held.Get("a"), held.Has("a"), held.Size, held.Keys().Count(), held.Values().Count()));
    }

    [TestMethod]
    public void Set_OnACollectedMap_ReadsBackNothingAsInYjs()
    {
        var held = Map();

        held.Set("c", 3);

        Assert.AreEqual((null, 0), (held.Get("c"), held.Size));
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
            e.Insert(0, new object[] { new YXmlText("x") });
            e.SetAttribute("a", "1");
        });

        Assert.AreEqual(("<p></p>", 0, null, null, 0, 0), (held.ToString(), held.Length, held.Get(0), held.GetAttribute("a"),
            held.GetAttributes().Count, held.ToArray().Count));
    }
}
