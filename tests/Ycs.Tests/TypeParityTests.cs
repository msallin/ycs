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
/// <c>YArray.Map/ForEach/ToJson/Push/From</c>/indexer, <c>YText.ToJson/CleanupYTextFormatting</c>,
/// and <c>AbstractType.Observe/Unobserve/ObserveDeep/UnobserveDeep/ToJson</c>.
/// </summary>
[TestClass]
public class TypeParityTests
{
    [TestMethod]
    public void TestYMapHasClearSize()
    {
        var doc = new YDoc();
        var map = doc.GetMap("map");

        map.Set("a", 1);
        map.Set("b", 2);

        Assert.IsTrue(map.Has("a"));
        Assert.IsTrue(map.ContainsKey("a"));
        Assert.IsFalse(map.Has("missing"));
        Assert.AreEqual(2, map.Size);
        Assert.AreEqual(map.Count, map.Size);

        map.Clear();

        Assert.AreEqual(0, map.Size);
        Assert.IsFalse(map.Has("a"));
        Assert.IsFalse(map.Has("b"));
    }

    [TestMethod]
    public void TestYMapClearPrelim()
    {
        // Clear() on a not-yet-integrated (prelim) map must not throw and must empty it.
        var map = new YMap();
        map.Set("a", 1);
        Assert.AreEqual(1, map.Count);

        map.Clear();
        Assert.AreEqual(0, map.Count);
    }

    [TestMethod]
    public void TestYMapForEachAndEntries()
    {
        var doc = new YDoc();
        var map = doc.GetMap("map");
        map.Set("a", 1);
        map.Set("b", 2);
        map.Set("c", 3);

        var seenViaForEach = new Dictionary<string, object>();
        map.ForEach((value, key, m) =>
        {
            Assert.AreSame(map, m);
            seenViaForEach[key] = value;
        });
        CollectionAssert.AreEquivalent(new[] { "a", "b", "c" }, seenViaForEach.Keys.ToList());

        var seenViaEntries = map.Entries().ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        CollectionAssert.AreEquivalent(seenViaForEach, seenViaEntries);
    }

    [TestMethod]
    public void TestYMapToJsonRecursesIntoNestedTypes()
    {
        var doc = new YDoc();
        var map = doc.GetMap("map");
        map.Set("str", "hello");
        map.Set("int", 42);

        var nested = new YMap();
        map.Set("nested", nested);
        nested.Set("deep", "value");

        var innerArray = new YArray();
        nested.Set("list", innerArray);
        innerArray.Push(new object[] { 1, 2, 3 });

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
        map.Set("a", 1);

        var nested = new YMap();
        map.Set("nested", nested);
        nested.Set("x", 1);

        var clone = map.Clone();

        // Per Yjs' own contract ("the content is only readable after it has been included
        // somewhere in the Ydoc"), a freshly cloned type is prelim/unattached: its values live in
        // _prelimContent, not the Item-backed _map that Get() reads from. Attach it (into an
        // unrelated container, same or different doc -- doesn't matter) before reading it back.
        doc.GetArray("holder").Insert(0, new object[] { clone });

        Assert.AreEqual(1, clone.Get("a"));
        var clonedNested = clone.Get("nested") as YMap;
        Assert.IsNotNull(clonedNested);
        Assert.AreNotSame(nested, clonedNested);
        Assert.AreEqual(1, clonedNested.Get("x"));

        // Mutating the (now-attached) clone must not affect the original.
        doc.Transact(_ => clone.Set("a", 2));
        Assert.AreEqual(1, map.Get("a"));
    }

    [TestMethod]
    public void TestYArrayPushMapForEachIndexer()
    {
        var doc = new YDoc();
        var arr = doc.GetArray("array");
        arr.Push(new object[] { 1, 2, 3 });

        Assert.AreEqual(1, arr[0]);
        Assert.AreEqual(2, arr[1]);
        Assert.AreEqual(3, arr[2]);
        Assert.AreEqual(arr.Get(1), arr[1]);

        var doubled = arr.Map((v, i, a) => (int)v * 2);
        CollectionAssert.AreEqual(new[] { 2, 4, 6 }, doubled);

        var seen = new List<object>();
        arr.ForEach((v, i, a) =>
        {
            Assert.AreSame(arr, a);
            seen.Add(v);
        });
        CollectionAssert.AreEqual(new object[] { 1, 2, 3 }, seen);
    }

    [TestMethod]
    public void TestYArrayFromConstructsPrelimArray()
    {
        var arr = YArray.From(new object[] { "a", "b", "c" });

        // Length reads _prelimContent directly and is available immediately, but (per Yjs'
        // contract) the *content* of a prelim/unattached type is only readable once it has been
        // included somewhere in a Ydoc.
        Assert.AreEqual(3, arr.Length);

        var doc = new YDoc();
        doc.GetArray("array").Insert(0, new object[] { arr });

        var attached = doc.GetArray("array").Get(0) as YArray;
        Assert.AreEqual(3, attached.Length);
        CollectionAssert.AreEqual(new object[] { "a", "b", "c" }, attached.ToArray().ToList());
    }

    [TestMethod]
    public void TestYArrayToJsonRecursesIntoNestedTypes()
    {
        var doc = new YDoc();
        var arr = doc.GetArray("array");
        arr.Push(new object[] { "a", 1 });

        var nestedMap = new YMap();
        nestedMap.Set("k", "v");
        arr.Push(new object[] { nestedMap });

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
    public void TestCleanupYTextFormattingStaticWrapper()
    {
        var doc = new YDoc();
        var text = doc.GetText("text");
        text.Insert(0, "abc");
        text.Format(0, 3, new Dictionary<string, object> { { "bold", true } });
        text.Format(0, 3, new Dictionary<string, object> { { "bold", true } });

        // Redundant formatting runs should be collapsible; the static free-function wrapper
        // must behave exactly like the instance method it forwards to.
        var viaStatic = YText.CleanupYTextFormatting(text);
        Assert.IsTrue(viaStatic >= 0);
        Assert.AreEqual("abc", text.ToString());
    }

    [TestMethod]
    public void TestObserveUnobserveAreSymmetricWithEventHandler()
    {
        var doc = new YDoc();
        var map = doc.GetMap("map");

        int callCount = 0;
        void Handler(object sender, YEventArgs e) => callCount++;

        map.Observe(Handler);
        map.Set("a", 1);
        Assert.AreEqual(1, callCount);

        map.Unobserve(Handler);
        map.Set("b", 2);
        Assert.AreEqual(1, callCount, "Unobserve must remove the same handler Observe registered.");

        // Observe/Unobserve are thin wrappers over the EventHandler event: a handler subscribed
        // via '+=' must be removable via Unobserve, and vice versa.
        map.EventHandler += Handler;
        map.Set("c", 3);
        Assert.AreEqual(2, callCount);
        map.Unobserve(Handler);
        map.Set("d", 4);
        Assert.AreEqual(2, callCount);
    }

    [TestMethod]
    public void TestObserveDeepUnobserveDeepAreSymmetricWithDeepEventHandler()
    {
        var doc = new YDoc();
        var map = doc.GetMap("map");
        var nested = new YMap();
        map.Set("nested", nested);

        int callCount = 0;
        void Handler(object sender, YDeepEventArgs e) => callCount++;

        map.ObserveDeep(Handler);
        nested.Set("x", 1);
        Assert.AreEqual(1, callCount);

        map.UnobserveDeep(Handler);
        nested.Set("y", 2);
        Assert.AreEqual(1, callCount, "UnobserveDeep must remove the same handler ObserveDeep registered.");
    }
}
