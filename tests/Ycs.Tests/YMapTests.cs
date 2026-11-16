// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class YMapTests : YTestBase
{
    [TestMethod]
    public void TestMapHavingIterableAsConstructorParamTests()
    {
        Init(users: 1);
        var map0 = Maps[Users[0]];

        var m1 = new YMap(YValue.FromObject(new Dictionary<string, object> { { "number", 1 }, { "string", "hello" } }).EnumerateObject());
        map0["m1"] = m1;
        Assert.AreEqual(1, m1["number"].ToObject());
        Assert.AreEqual("hello", m1["string"].ToObject());

        var obj = new object[] { 1 };
        var m2 = new YMap(YValue.FromObject(new Dictionary<string, object> { { "object", obj }, { "boolean", true } }).EnumerateObject());
        map0["m2"] = m2;
        Assert.AreNotSame(obj, m2["object"].ToObject()); // the document holds a copy
        CollectionAssert.AreEqual(obj, (System.Collections.IList)m2["object"].ToObject());
        Assert.AreEqual(true, m2["boolean"].ToObject());

        CompareUsers();
    }

    [TestMethod]
    public void TestBasicMapTests()
    {
        Init(users: 3);
        var map0 = Maps[Users[0]];
        var map1 = Maps[Users[1]];
        var map2 = Maps[Users[2]];

        Users[2].Disconnect();

        map0["number"] = 1;
        map0["string"] = "hello Y";
        map0["object"] = YValue.FromObject(new List<object> { "value" });
        map0["y-map"] = new YMap();
        map0["boolean1"] = true;
        map0["boolean0"] = false;

        var map = map0["y-map"].ToObject() as YMap;
        Assert.IsNotNull(map);
        map["y-array"] = new YArray();
        var array = map["y-array"].ToObject() as YArray;
        Assert.IsNotNull(array);

        array.Insert(0, 0);
        array.Insert(0, -1);

        Assert.AreEqual(1, map0["number"].ToObject());
        Assert.AreEqual("hello Y", map0["string"].ToObject());
        Assert.AreEqual(false, map0["boolean0"].ToObject());
        Assert.AreEqual(true, map0["boolean1"].ToObject());
        Assert.AreEqual("value", (map0["object"].ToObject() as IList<object>)[0]);
        Assert.AreEqual(-1, ((map0["y-map"].ToObject() as YMap)["y-array"].ToObject() as YArray)[0].ToObject());
        Assert.AreEqual(6, map0.Keys.Count());

        Users[2].Connect();
        Connector.FlushAllMessages();

        Assert.AreEqual(1, map1["number"].ToObject());
        Assert.AreEqual("hello Y", map1["string"].ToObject());
        Assert.AreEqual(false, map1["boolean0"].ToObject());
        Assert.AreEqual(true, map1["boolean1"].ToObject());

        Assert.AreEqual("value", (map1["object"].ToObject() as IList<object>)[0]);
        Assert.AreEqual(-1, ((map1["y-map"].ToObject() as YMap)["y-array"].ToObject() as YArray)[0].ToObject());
        Assert.AreEqual(6, map1.Keys.Count());

        // Compare disconnected user.
        Assert.AreEqual(1, map2["number"].ToObject());
        Assert.AreEqual("hello Y", map2["string"].ToObject());
        Assert.AreEqual(false, map2["boolean0"].ToObject());
        Assert.AreEqual(true, map2["boolean1"].ToObject());
        Assert.AreEqual("value", (map2["object"].ToObject() as IList<object>)[0]);
        Assert.AreEqual(-1, ((map2["y-map"].ToObject() as YMap)["y-array"].ToObject() as YArray)[0].ToObject());
        Assert.AreEqual(6, map2.Keys.Count());

        CompareUsers();
    }

    [TestMethod]
    public void TestGetAndSetOfMapProperty()
    {
        Init(users: 2);
        var map0 = Maps[Users[0]];

        map0["stuff"] = "stuffy";
        map0["null"] = YValue.Null;
        Assert.AreEqual("stuffy", map0["stuff"].ToObject());
        Assert.AreEqual(null, map0["null"].ToObject());

        Connector.FlushAllMessages();

        foreach (var user in Users)
        {
            var u = user.GetMap("map");
            Assert.AreEqual("stuffy", u["stuff"].ToObject());
            Assert.AreEqual(null, u["null"].ToObject());
        }

        CompareUsers();
    }

    [TestMethod]
    public void TestYMapSetsYMap()
    {
        Init(users: 2);
        var map0 = Maps[Users[0]];
        var map = new YMap();
        map0["map"] = map;

        Assert.AreSame(map, map0["map"].ToObject());
        map["one"] = 1;
        Assert.AreEqual(1, map["one"].ToObject());

        CompareUsers();
    }

    [TestMethod]
    public void TestYMapSetsYArray()
    {
        Init(users: 2);
        var map0 = Maps[Users[0]];

        var array = new YArray();
        map0["array"] = array;
        Assert.AreSame(array, map0["array"].ToObject());

        array.Insert(0, 1, 2, 3);
        CollectionAssert.AreEqual(new object[] { 1, 2, 3 }, (map0["array"].ToObject() as YArray).ToArray().Select(v => v.ToObject()).ToList());

        CompareUsers();
    }

    [TestMethod]
    public void TestGetAndSetOfMapPropertySyncs()
    {
        Init(users: 2);
        var map0 = Maps[Users[0]];

        map0["stuff"] = "stuffy";
        Assert.AreEqual("stuffy", map0["stuff"].ToObject());
        Connector.FlushAllMessages();

        foreach (var user in Users)
        {
            var u = user.GetMap("map");
            Assert.AreEqual("stuffy", u["stuff"].ToObject());
        }

        CompareUsers();
    }

    [TestMethod]
    public void TestGetAndSetOfMapPropertyWithConflict()
    {
        Init(users: 3);
        var map0 = Maps[Users[0]];
        var map1 = Maps[Users[1]];

        map0["stuff"] = "c0";
        map1["stuff"] = "c1";
        Connector.FlushAllMessages();

        foreach (var user in Users)
        {
            var u = user.GetMap("map");
            Assert.AreEqual("c1", u["stuff"].ToObject());
        }

        CompareUsers();
    }

    [TestMethod]
    public void TestSizeAndDeleteOfMapProperty()
    {
        Init(users: 1);
        var map0 = Maps[Users[0]];

        map0["stuff"] = "c0";
        map0["otherStuff"] = "c1";
        Assert.AreEqual(2, map0.Keys.Count());

        map0.Remove("stuff");
        Assert.AreEqual(1, map0.Keys.Count());

        map0.Remove("otherStuff");
        Assert.AreEqual(0, map0.Keys.Count());

        CompareUsers();
    }

    [TestMethod]
    public void TestGetAndSetAndDeleteOfMapProperty()
    {
        Init(users: 3);
        var map0 = Maps[Users[0]];
        var map1 = Maps[Users[1]];

        map0["stuff"] = "c0";
        map1["stuff"] = "c1";
        map1.Remove("stuff");

        Connector.FlushAllMessages();

        foreach (var user in Users)
        {
            var u = user.GetMap("map");
            Assert.IsFalse(u.ContainsKey("stuff"));
        }

        CompareUsers();
    }

    [TestMethod]
    public void TestGetAndSetOfMapPropertyWithThreeConflicts()
    {
        Init(users: 3);
        var map0 = Maps[Users[0]];
        var map1 = Maps[Users[1]];
        var map2 = Maps[Users[2]];

        map0["stuff"] = "c0";
        map1["stuff"] = "c1";
        map1["stuff"] = "c2";
        map2["stuff"] = "c3";

        Connector.FlushAllMessages();

        foreach (var user in Users)
        {
            var u = user.GetMap("map");
            Assert.AreEqual("c3", u["stuff"].ToObject());
        }

        CompareUsers();
    }

    [TestMethod]
    public void TestGetAndSetAndDeleteOfMapPropertyWithThreeConflicts()
    {
        Init(users: 4);
        var map0 = Maps[Users[0]];
        var map1 = Maps[Users[1]];
        var map2 = Maps[Users[2]];
        var map3 = Maps[Users[3]];

        map0["stuff"] = "c0";
        map1["stuff"] = "c1";
        map1["stuff"] = "c2";
        map2["stuff"] = "c3";

        Connector.FlushAllMessages();

        map0["stuff"] = "deleteMe";
        map1["stuff"] = "c1";
        map2["stuff"] = "c2";
        map3["stuff"] = "c3";
        map3.Remove("stuff");

        Connector.FlushAllMessages();

        foreach (var user in Users)
        {
            var u = user.GetMap("map");
            Assert.IsFalse(u.ContainsKey("stuff"));
        }

        CompareUsers();
    }

    [TestMethod]
    public void TestObserveDeepProperties()
    {
        Init(users: 4);
        var map0 = Maps[Users[0]];
        var map1 = Maps[Users[1]];
        var map2 = Maps[Users[2]];
        var map3 = Maps[Users[3]];

        var _map1 = new YMap();
        map1["map"] = _map1;

        int calls = 0;
        ItemId? dmapId = null;

        map1.ObserveDeep((deepEvents, _) =>
        {
            foreach (var evt in deepEvents)
            {
                calls++;

                Assert.IsTrue(evt.Changes.Keys.ContainsKey("deepmap"));
                Assert.AreEqual(1, evt.Path.Count);
                Assert.AreEqual("map", evt.Path[0].Key);
                dmapId = ((evt.Target as YMap)["deepmap"].ToObject() as YMap).OwnerIdForTests;
            }
        });

        Connector.FlushAllMessages();

        var _map3 = map3["map"].ToObject() as YMap;
        _map3["deepmap"] = new YMap();
        Connector.FlushAllMessages();

        var _map2 = map2["map"].ToObject() as YMap;
        _map2["deepmap"] = new YMap();
        Connector.FlushAllMessages();

        var dmap1 = _map1["deepmap"].ToObject() as YMap;
        var dmap2 = _map2["deepmap"].ToObject() as YMap;
        var dmap3 = _map3["deepmap"].ToObject() as YMap;

        Assert.IsTrue(calls > 0);
        Assert.IsTrue(dmap1.OwnerIdForTests == dmap2.OwnerIdForTests);
        Assert.IsTrue(dmap1.OwnerIdForTests == dmap3.OwnerIdForTests);
        Assert.IsTrue(dmap1.OwnerIdForTests == dmapId);

        CompareUsers();
    }

    [TestMethod]
    public void TestObserversUsingObserveDeep()
    {
        Init(users: 2);
        var map0 = Maps[Users[0]];

        var paths = new List<IReadOnlyList<PathSegment>>();
        int calls = 0;

        map0.ObserveDeep((deepEvents, _) =>
        {
            foreach (var evt in deepEvents)
            {
                paths.Add(evt.Path);
            }

            calls++;
        });

        map0["map"] = new YMap();
        (map0["map"].ToObject() as YMap)["array"] = new YArray();
        ((map0["map"].ToObject() as YMap)["array"].ToObject() as YArray).Insert(0, "content");
        Assert.AreEqual(3, calls);
        Assert.AreEqual(3, paths.Count);
        Assert.AreEqual(0, paths[0].Count);
        CollectionAssert.AreEqual(new[] { new PathSegment("map", 0) }, paths[1].ToArray());
        CollectionAssert.AreEqual(new[] { new PathSegment("map", 0), new PathSegment("array", 0) }, paths[2].ToArray());

        CompareUsers();
    }

    [TestMethod]
    public void TestThrowsAddAndUpdateAndDeleteEvents()
    {
        Init(users: 2);
        var map0 = Maps[Users[0]];

        YMapEvent evt = null;

        map0.Observe(e =>
        {
            evt = e as YMapEvent;

            // Collect changes while data is not GC'ed.
            var changes = evt.Changes;
            Assert.IsNotNull(changes);
        });

        map0["stuff"] = 4;
        Assert.AreSame(map0, evt.Target);
        CollectionAssert.AreEqual(new[] { "stuff" }, evt.KeysChanged.ToList());
        evt = null;

        // Update, oldValue is in contents.
        map0["stuff"] = new YArray();
        Assert.AreSame(map0, evt.Target);
        CollectionAssert.AreEqual(new[] { "stuff" }, evt.KeysChanged.ToList());
        evt = null;

        // Update, oldValue is in opContents.
        map0["stuff"] = 5;
        map0.Remove("stuff");
        Assert.AreSame(map0, evt.Target);
        CollectionAssert.AreEqual(new[] { "stuff" }, evt.KeysChanged.ToList());
        evt = null;

        CompareUsers();
    }

    [TestMethod]
    public void TestChangeEvent()
    {
        Init(users: 2);
        var map0 = Maps[Users[0]];

        YChanges changes = null;
        KeyChange keyChange;

        map0.Observe(e =>
        {
            changes = e.Changes;
        });

        map0["a"] = 1;
        Assert.IsTrue(changes.Keys.TryGetValue("a", out keyChange));
        Assert.AreEqual(KeyChangeAction.Add, keyChange.Action);
        Assert.IsNull(keyChange.OldValue);
        changes = null;

        map0["a"] = 2;
        Assert.IsTrue(changes.Keys.TryGetValue("a", out keyChange));
        Assert.AreEqual(KeyChangeAction.Update, keyChange.Action);
        Assert.AreEqual(1, keyChange.OldValue);
        changes = null;

        Users[0].Transact(tr =>
        {
            map0["a"] = 3;
            map0["a"] = 4;
        });

        Assert.IsTrue(changes.Keys.TryGetValue("a", out keyChange));
        Assert.AreEqual(KeyChangeAction.Update, keyChange.Action);
        Assert.AreEqual(2, keyChange.OldValue);
        changes = null;

        Users[0].Transact(tr =>
        {
            map0["b"] = 1;
            map0["b"] = 2;
        });

        Assert.IsTrue(changes.Keys.TryGetValue("b", out keyChange));
        Assert.AreEqual(KeyChangeAction.Add, keyChange.Action);
        Assert.IsNull(keyChange.OldValue);
        changes = null;

        Users[0].Transact(tr =>
        {
            map0["c"] = 1;
            map0.Remove("c");
        });

        Assert.AreEqual(0, changes.Keys.Count);
        changes = null;

        Users[0].Transact(tr =>
        {
            map0["d"] = 1;
            map0["d"] = 2;
        });

        Assert.IsTrue(changes.Keys.TryGetValue("d", out keyChange));
        Assert.AreEqual(KeyChangeAction.Add, keyChange.Action);
        Assert.IsNull(keyChange.OldValue);
        changes = null;

        CompareUsers();
    }

    [TestMethod]
    public void TestYMapEventExceptionsShouldCompleteTransaction()
    {
        Init(users: 1);
        var doc = Users[0];
        var map = Maps[Users[0]];

        bool updateCalled = false;
        bool throwingObserverCalled = false;
        bool throwingDeepObserverCalled = false;

        doc.UpdatedV2 += (s, e) =>
        {
            updateCalled = true;
        };

        void throwingObserver(YMapEvent args)
        {
            throwingObserverCalled = true;
            throw new InvalidOperationException("Failure");
        }

        void throwingDeepObserver(IReadOnlyList<YEvent> args, Transaction tr)
        {
            throwingDeepObserverCalled = true;
            throw new InvalidOperationException("Deep failure");
        }

        map.Observe(throwingObserver);
        map.ObserveDeep(throwingDeepObserver);

        Assert.ThrowsExactly<InvalidOperationException>(() =>
        {
            map["y"] = "2";
        });

        Assert.IsTrue(updateCalled);
        Assert.IsTrue(throwingObserverCalled);
        Assert.IsTrue(throwingDeepObserverCalled);

        // Check if it works again.
        updateCalled = false;
        throwingObserverCalled = false;
        throwingDeepObserverCalled = false;

        Assert.ThrowsExactly<InvalidOperationException>(() =>
        {
            map["z"] = "3";
        });

        Assert.IsTrue(updateCalled);
        Assert.IsTrue(throwingObserverCalled);
        Assert.IsTrue(throwingDeepObserverCalled);

        Assert.AreEqual("3", map["z"].ToObject());
    }

    [TestMethod]
    [DataRow(5, 20)]
    [DataRow(5, 40)]
    [DataRow(5, 42)]
    [DataRow(5, 43)]
    [DataRow(5, 44)]
    [DataRow(5, 45)]
    [DataRow(5, 46)]
    [DataRow(5, 300)]
    [DataRow(5, 400)]
    [DataRow(5, 500)]
    [DataRow(5, 600)]
    [DataRow(5, 1_000)]
    [DataRow(5, 1_800)]
    /*
    [DataRow(5, 3_000)]
    [DataRow(5, 5_000)]
    [DataRow(5, 15_000)]
    */
    public void TestRepeatingGeneratingYMapTests(int users, int iterations)
    {
        RandomTests(new List<Action<TestYInstance, Random>>
        {
            // Set
            (user, rand) =>
            {
                var key = new[] { "one", "two" }[rand.Next(0, 2)];
                var value = Guid.NewGuid().ToString();
                user.GetMap("map")[key] = value;
            },

            // Set type
            (user, rand) =>
            {
                var key = new[] { "one", "two" }[rand.Next(0, 2)];
                var type = new object[] { new YArray(), new YMap() }[rand.Next(0, 2)];
                user.GetMap("map")[key] = YValue.FromObject(type);

                if (type is YArray yarr)
                {
                    yarr.Insert(0, 1, 2, 3, 4);
                }
                else if (type is YMap ymap)
                {
                    ymap["deepkey"] = "deepvalue";
                }
                else
                {
                    Assert.Fail("Unexpected");
                }
            },

            // Delete
            (user, rand) =>
            {
                var key = new[] { "one", "two" }[rand.Next(0, 2)];
                user.GetMap("map").Remove(key);
            }
        }, users, iterations);
    }
}
