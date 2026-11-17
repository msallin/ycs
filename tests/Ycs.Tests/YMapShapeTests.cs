using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class YMapShapeTests
{
    [TestMethod]
    public void Indexer_MissingKey_Throws()
    {
        var map = new YDoc().GetMap("m");

        Assert.ThrowsExactly<KeyNotFoundException>(() => map["k"]);
    }

    [TestMethod]
    public void Indexer_Set_ReplacesAndCounts()
    {
        var map = new YDoc().GetMap("m");

        map["a"] = 1;
        map["a"] = 2;
        map["b"] = "x";

        Assert.AreEqual((2, 2, "x"), (map.Count, map["a"].GetInt32(), map["b"].GetString()));
    }

    [TestMethod]
    public void TryGetValue_NullValueAndMissingKey_AreTold()
    {
        var map = new YDoc().GetMap("m");
        map["n"] = YValue.Null;

        Assert.AreEqual((true, true, false, true, false), (map.TryGetValue("n", out var n), n.IsNull, map.TryGetValue("x", out _), map.ContainsKey("n"), map.ContainsKey("x")));
    }

    [TestMethod]
    public void Remove_AndClear_Remove()
    {
        var map = new YDoc().GetMap("m");
        map["a"] = 1;
        map["b"] = 2;
        map["c"] = 3;

        Assert.AreEqual((true, false, 2), (map.Remove("a"), map.Remove("a"), map.Count));
        map.Clear();
        Assert.AreEqual((0, false), (map.Count, map.ContainsKey("b")));
    }

    [TestMethod]
    public void KeysAndValues_AreTheEntries()
    {
        var map = new YDoc().GetMap("m");
        map["a"] = 1;
        map["b"] = 2;

        Assert.AreEqual("a,b|1,2|a=1,b=2", string.Join("|", string.Join(",", map.Keys.Order()), string.Join(",", map.Values.Select(v => v.GetInt32()).Order()), string.Join(",", map.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value.GetInt32()}"))));
    }

    [TestMethod]
    public void CollectionInitializer_BuildsAMapThatIsReadableBeforeItIsInADocument()
    {
        var map = new YMap { { "a", 1 }, { "b", "two" } };

        Assert.AreEqual((2, true, "two", 1), (map.Count, map.ContainsKey("a"), map["b"].GetString(), map.Keys.Count(k => k == "a")));
        Assert.ThrowsExactly<ArgumentException>(() => map.Add("a", 3));
    }

    [TestMethod]
    public void Constructor_FromEntries_ThenInserted_HoldsThem()
    {
        var doc = new YDoc();
        var map = new YMap(new Dictionary<string, YValue> { ["x"] = 1, ["y"] = new YArray(1) });

        doc.GetMap("m")["inner"] = map;

        Assert.AreEqual((2, 1), (map.Count, ((YArray)map["y"].ToObject()).Count));
    }

    [TestMethod]
    public void AtASnapshot_ReadsWhatWasThere()
    {
        var doc = new YDoc(new YDocOptions { GarbageCollection = false });
        var map = doc.GetMap("m");
        map["a"] = 1;
        map["b"] = 2;
        var snapshot = doc.CreateSnapshot();
        map["a"] = 10;
        map.Remove("b");
        map["c"] = 3;

        var then = map.ToDictionary(snapshot);

        Assert.AreEqual((true, 1, false, false, 2, "a,b"), (map.TryGetValue("a", snapshot, out var a), a.GetInt32(), map.TryGetValue("c", snapshot, out _), map.TryGetValue("b", out _), then["b"].GetInt32(), string.Join(",", then.Keys.Order())));
    }

    [TestMethod]
    public void Clone_CopiesNestedTypes()
    {
        var map = new YDoc().GetMap("m");
        map["a"] = new YMap { ["k"] = 1 };

        var clone = map.Clone();
        ((YMap)map["a"].ToObject())["k"] = 2;

        Assert.AreEqual(1, ((YMap)clone["a"].ToObject())["k"].GetInt32());
    }

    [TestMethod]
    public void Value_ContainerAfterInsert_IsNotChangedByTheCaller()
    {
        var list = new List<object?> { 1 };
        var map = new YDoc().GetMap("m");

        map["l"] = YValue.FromObject(list);
        list.Add(2);

        Assert.AreEqual(1, map["l"].Length);
    }

    [TestMethod]
    public void CollectionExpression_OfPairs_BuildsTheMap()
    {
        YMap map = [new("k", 1), new("j", "x")];

        Assert.AreEqual((2, (YValue)1, (YValue)"x"), (map.Count, map["k"], map["j"]));
    }
}
