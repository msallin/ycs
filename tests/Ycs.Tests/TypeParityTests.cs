// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Ports the meaningful behavioural cases (not already covered by <see cref="YMapTests"/>,
/// <see cref="YArrayTests"/>, <see cref="YTextTests"/>) for the shared-type API surface that was
/// added or fixed to reach Yjs 13.6.32 parity: <c>YMap.Has/Clear/Size/ForEach/Entries/ToJson</c>,
/// <c>YArray.Map/ForEach/ToJson/Push/From</c>/indexer, <c>YText.ToJson</c>,
/// and <c>YType.Observe/Unobserve/ObserveDeep/UnobserveDeep/ToJson</c>.
/// </summary>
[TestClass]
public class TypeParityTests
{
    [TestMethod]
    public void TestYMapHasClearSize()
    {
        var doc = new YDoc();
        var map = doc.GetMap("map");

        map["a"] = 1;
        map["b"] = 2;

        Assert.IsTrue(map.ContainsKey("a"));
        Assert.IsTrue(map.ContainsKey("a"));
        Assert.IsFalse(map.ContainsKey("missing"));
        Assert.AreEqual(2, map.Count);

        map.Clear();

        Assert.AreEqual(0, map.Count);
        Assert.IsFalse(map.ContainsKey("a"));
        Assert.IsFalse(map.ContainsKey("b"));
    }

    [TestMethod]
    public void TestYMapClearPrelim()
    {
        // Clear() on a not-yet-integrated (prelim) map must not throw and must empty it.
        var map = new YMap();
        map["a"] = 1;
        Assert.AreEqual(1, map.Count);

        map.Clear();
        Assert.AreEqual(0, map.Count);
    }

    [TestMethod]
    public void TestYMapForEachAndEntries()
    {
        var doc = new YDoc();
        var map = doc.GetMap("map");
        map["a"] = 1;
        map["b"] = 2;
        map["c"] = 3;

        var seenViaForEach = new Dictionary<string, object>();
        foreach (var (key, value) in map)
        {
            seenViaForEach[key] = value.ToObject();
        }
        CollectionAssert.AreEquivalent(new[] { "a", "b", "c" }, seenViaForEach.Keys.ToList());

        var seenViaEntries = map.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToObject());
        CollectionAssert.AreEquivalent(seenViaForEach, seenViaEntries);
    }

    [TestMethod]
    public void TestYMapToJsonRecursesIntoNestedTypes()
    {
        var doc = new YDoc();
        var map = doc.GetMap("map");
        map["str"] = "hello";
        map["int"] = 42;

        var nested = new YMap();
        map["nested"] = nested;
        nested["deep"] = "value";

        var innerArray = new YArray();
        nested["list"] = innerArray;
        innerArray.Add(1, 2, 3);

        var json = map.ToJson() as IDictionary<string, object>;
        Assert.IsNotNull(json);
        Assert.AreEqual("hello", json["str"]);
        Assert.AreEqual(42, json["int"]);

        var nestedJson = json["nested"] as IDictionary<string, object>;
        Assert.IsNotNull(nestedJson, "Nested YMap must be recursively converted, not left as a YMap instance.");
        Assert.AreEqual("value", nestedJson["deep"]);

        var listJson = nestedJson["list"] as IList<object>;
        Assert.IsNotNull(listJson, "Nested YArray must be recursively converted, not left as a YArray instance.");
        CollectionAssert.AreEqual(new object[] { 1, 2, 3 }, listJson.ToList());
    }

    [TestMethod]
    public void TestYMapCloneIsIndependentAndRecursesIntoNestedTypes()
    {
        // Regression test: the original port's InternalClone() stored the raw `Item` object as
        // the cloned value (see the removed TODO comment) instead of the item's content, and
        // never cloned nested AbstractTypes -- so the clone silently shared/aliased state with
        // the original. This proves the fix.
        var doc = new YDoc();
        var map = doc.GetMap("map");
        map["a"] = 1;

        var nested = new YMap();
        map["nested"] = nested;
        nested["x"] = 1;

        var clone = map.Clone();

        // Per Yjs' own contract ("the content is only readable after it has been included
        // somewhere in the Ydoc"), a freshly cloned type is prelim/unattached: its values live in
        // _prelimContent, not the Item-backed _map that Get() reads from. Attach it (into an
        // unrelated container, same or different doc -- doesn't matter) before reading it back.
        doc.GetArray("holder").Insert(0, clone);

        Assert.AreEqual(1, clone["a"].ToObject());
        var clonedNested = clone["nested"].ToObject() as YMap;
        Assert.IsNotNull(clonedNested);
        Assert.AreNotSame(nested, clonedNested);
        Assert.AreEqual(1, clonedNested["x"].ToObject());

        // Mutating the (now-attached) clone must not affect the original.
        doc.Transact(_ => clone["a"] = 2);
        Assert.AreEqual(1, map["a"].ToObject());
    }

    [TestMethod]
    public void TestYArrayPushMapForEachIndexer()
    {
        var doc = new YDoc();
        var arr = doc.GetArray("array");
        arr.Add(1, 2, 3);

        Assert.AreEqual<YValue>(1, arr[0]);
        Assert.AreEqual<YValue>(2, arr[1]);
        Assert.AreEqual<YValue>(3, arr[2]);

        var doubled = arr.Select(v => v.GetInt32() * 2).ToList();
        CollectionAssert.AreEqual(new[] { 2, 4, 6 }, doubled);

        var seen = new List<object>();
        foreach (var v in arr)
        {
            seen.Add(v.ToObject());
        }
        CollectionAssert.AreEqual(new object[] { 1, 2, 3 }, seen);
    }

    [TestMethod]
    public void TestYArrayConstructorMakesPrelimArray()
    {
        var arr = new YArray("a", "b", "c");

        // Length reads _prelimContent directly and is available immediately, but (per Yjs'
        // contract) the *content* of a prelim/unattached type is only readable once it has been
        // included somewhere in a Ydoc.
        Assert.AreEqual(3, arr.Length);

        var doc = new YDoc();
        doc.GetArray("array").Insert(0, arr);

        var attached = doc.GetArray("array")[0].ToObject() as YArray;
        Assert.AreEqual(3, attached.Length);
        CollectionAssert.AreEqual(new object[] { "a", "b", "c" }, attached.Select(v => v.ToObject()).ToList());
    }

    [TestMethod]
    public void TestYArrayToJsonRecursesIntoNestedTypes()
    {
        var doc = new YDoc();
        var arr = doc.GetArray("array");
        arr.Add("a", 1);

        var nestedMap = new YMap();
        nestedMap["k"] = "v";
        arr.Add(nestedMap);

        var json = arr.ToJson() as IList<object>;
        Assert.IsNotNull(json);
        Assert.AreEqual(3, json.Count);
        Assert.AreEqual("a", json[0]);
        Assert.AreEqual(1, json[1]);

        var nestedJson = json[2] as IDictionary<string, object>;
        Assert.IsNotNull(nestedJson);
        Assert.AreEqual("v", nestedJson["k"]);
    }

    [TestMethod]
    public void TestYTextToJsonMatchesToString()
    {
        var doc = new YDoc();
        var text = doc.GetText("text");
        text.Insert(0, "hello world");

        Assert.AreEqual(text.ToString(), text.ToJson());
        Assert.AreEqual("hello world", text.ToJson());
    }

    [TestMethod]
    public void TestObserveDisposeStopsTheHandler()
    {
        var doc = new YDoc();
        var map = doc.GetMap("map");

        int callCount = 0;
        var subscription = map.Observe(_ => callCount++);
        map["a"] = 1;
        Assert.AreEqual(1, callCount);

        subscription.Dispose();
        map["b"] = 2;
        Assert.AreEqual(1, callCount, "Disposing the subscription must remove the handler.");

        subscription.Dispose();
        map["c"] = 3;
        Assert.AreEqual(1, callCount, "Disposing twice changes nothing.");
    }

    [TestMethod]
    public void TestObserveDeepDisposeStopsTheHandler()
    {
        var doc = new YDoc();
        var map = doc.GetMap("map");
        var nested = new YMap();
        map["nested"] = nested;

        int callCount = 0;
        var subscription = map.ObserveDeep((_, _) => callCount++);
        nested["x"] = 1;
        Assert.AreEqual(1, callCount);

        subscription.Dispose();
        nested["y"] = 2;
        Assert.AreEqual(1, callCount, "Disposing the subscription must remove the deep handler.");
    }
}
