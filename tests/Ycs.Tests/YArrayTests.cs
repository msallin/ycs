using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class YArrayTests : YTestBase
{
    [TestMethod]
    public void TestBasicUpdate()
    {
        var doc1 = new YDoc();
        var doc2 = new YDoc();
        var content = new List<object> { "hi" };
        doc1.GetArray("array").Insert(0, content.Select(YValue.FromObject).ToArray());
        var update = doc1.EncodeStateAsUpdate(format: UpdateFormat.V2);
        doc2.ApplyUpdate(update, format: UpdateFormat.V2);

        CollectionAssert.AreEqual(content, doc2.GetArray("array").ToArray().Select(v => v.ToObject()).ToList());
    }

    [TestMethod]
    public void TestDeleteInsert()
    {
        Init(users: 2);

        var array0 = Arrays[Users[0]];
        array0.RemoveRange(0, 0);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => array0.RemoveRange(1, 1));

        array0.Insert(0, "A");
        array0.RemoveRange(1, 0);

        CompareUsers();
    }

    [TestMethod]
    public void TestInsertThreeElementsTryRegetProperty()
    {
        Init(users: 2);

        var array0 = Arrays[Users[0]];
        array0.Insert(0, 1, true, false);
        CollectionAssert.AreEqual(new object[] { 1, true, false }, array0.ToArray().Select(v => v.ToObject()).ToList());

        Connector.FlushAllMessages();

        var array1 = Arrays[Users[1]];
        CollectionAssert.AreEqual(new object[] { 1, true, false }, array1.ToArray().Select(v => v.ToObject()).ToList());

        CompareUsers();
    }

    [TestMethod]
    public void TestConcurrentInsertWithThreeConflicts()
    {
        Init(users: 3);

        Arrays[Users[0]].Insert(0, 0);
        Arrays[Users[1]].Insert(0, 0);
        Arrays[Users[2]].Insert(0, 0);

        CompareUsers();
    }

    [TestMethod]
    public void TestConcurrentInsertDeleteWithThreeConflicts()
    {
        Init(users: 3);

        var array0 = Arrays[Users[0]];
        var array1 = Arrays[Users[1]];
        var array2 = Arrays[Users[2]];

        array0.Insert(0, "x", "y", "z");

        Connector.FlushAllMessages();

        array0.Insert(1, 0);
        array1.RemoveAt(0);
        array1.RemoveRange(1, 1);
        array2.Insert(1, 2);

        CompareUsers();
    }

    [TestMethod]
    public void TestInsertionsInLateSync()
    {
        Init(users: 3);

        var array0 = Arrays[Users[0]];
        var array1 = Arrays[Users[1]];
        var array2 = Arrays[Users[2]];

        array0.Insert(0, "x", "y");
        Connector.FlushAllMessages();

        Users[1].Disconnect();
        Users[2].Disconnect();

        array0.Insert(1, "user0");
        array1.Insert(1, "user1");
        array2.Insert(1, "user2");

        Users[1].Connect();
        Users[2].Connect();
        Connector.FlushAllMessages();

        CompareUsers();
    }

    [TestMethod]
    public void TestDisconnectReallyPreventsSendingMessages()
    {
        Init(users: 3);

        var array0 = Arrays[Users[0]];
        var array1 = Arrays[Users[1]];

        array0.Insert(0, "x", "y");
        Connector.FlushAllMessages();

        Users[1].Disconnect();
        Users[2].Disconnect();

        array0.Insert(1, "user0");
        array1.Insert(1, "user1");

        CollectionAssert.AreEqual(new[] { "x", "user0", "y" }, array0.ToArray().Select(v => v.ToObject()).ToList());
        CollectionAssert.AreEqual(new[] { "x", "user1", "y" }, array1.ToArray().Select(v => v.ToObject()).ToList());

        Users[1].Connect();
        Users[2].Connect();

        CompareUsers();
    }

    [TestMethod]
    public void TestDeletionsInLateSync()
    {
        Init(users: 2);

        var array0 = Arrays[Users[0]];
        var array1 = Arrays[Users[1]];

        array0.Insert(0, "x", "y");
        Connector.FlushAllMessages();
        Users[1].Disconnect();

        array0.RemoveRange(0, 2);
        array1.RemoveRange(1, 1);

        Users[1].Connect();

        CompareUsers();
    }

    [TestMethod]
    public void TestInsertThenMergeDeleteOnSync()
    {
        Init(users: 2);

        var array0 = Arrays[Users[0]];
        var array1 = Arrays[Users[1]];

        array0.Insert(0, "x", "y", "z");
        Connector.FlushAllMessages();
        Users[0].Disconnect();
        array1.RemoveRange(0, 3);
        Users[0].Connect();

        CompareUsers();
    }

    private static string Show(List<Dictionary<string, object>> delta) => "[" + string.Join(",", delta.Select(op => "{" + string.Join(",", op.Select(kv =>
        kv.Key + "=" + (kv.Value is IEnumerable<object> items ? "[" + string.Join(",", items) + "]" : kv.Value))) + "}")) + "]";

    [TestMethod]
    public void TestInsertAndDeleteEvents()
    {
        Init(users: 2);

        var array0 = Arrays[Users[0]];
        var deltas = new List<string>();

        array0.Observe(e => deltas.Add(Show(e.Changes.Delta.Select(d => d.ToPlain()).ToList())));

        array0.Insert(0, 0, 1, 2);
        array0.RemoveAt(0);
        array0.RemoveRange(0, 2);

        // The deltas of Yjs' y-array testInsertAndDeleteEvents.
        CollectionAssert.AreEqual(new[] { "[{insert=[0,1,2]}]", "[{delete=1}]", "[{delete=2}]" }, deltas);
        CompareUsers();
    }

    [TestMethod]
    public void TestNestedObserverEvents()
    {
        Init(users: 2);

        var array0 = Arrays[Users[0]];
        var values = new List<object>();

        array0.Observe(_ =>
        {
            if (array0.Length == 1)
            {
                // Inserting, will call this observer again.
                // We expect that this observer is called after this handler finishes.
                array0.Insert(1, 1);
                values.Add(0);
            }
            else
            {
                // This should be called the second time an element is inserted (above case).
                values.Add(1);
            }
        });

        array0.Insert(0, 0);

        CollectionAssert.AreEqual(new object[] { 0, 1 }, values);
        CollectionAssert.AreEqual(new object[] { 0, 1 }, array0.ToArray().Select(v => v.ToObject()).ToList());

        CompareUsers();
    }

    [TestMethod]
    public void TestInsertAndDeleteEventsForTypes()
    {
        Init(users: 2);

        var array0 = Arrays[Users[0]];
        var inserted = new List<object>();
        var deltas = new List<string>();

        array0.Observe(e =>
        {
            foreach (var delta in e.Changes.Delta)
            {
                if (delta is Delta.InsertItems items)
                {
                    inserted.AddRange(items.Items.Select(v => v.ToObject()));
                }
            }

            deltas.Add(Show(e.Changes.Delta.Select(d => d.ToPlain()).ToList()));
        });

        var inner = new YArray();
        array0.Insert(0, inner);
        array0.RemoveAt(0);

        CollectionAssert.AreEqual(new object[] { inner }, inserted);
        Assert.AreEqual("[{delete=1}]", deltas[1]);
        CompareUsers();
    }

    [TestMethod]
    public void TestObserveDeepEventOrder()
    {
        Init(users: 2);

        var array0 = Arrays[Users[0]];
        IReadOnlyList<YEvent> events = null;

        array0.ObserveDeep((deepEvents, _) =>
        {
            events = deepEvents;
        });

        array0.Insert(0, new YMap());

        Users[0].Transact(tr =>
        {
            ((YMap)array0[0].ToObject())["a"] = "a";
            array0.Insert(0, 0);
        });

        for (int i = 1; i < events.Count; i++)
        {
            Assert.IsTrue(events[i - 1].Path.Count <= events[i].Path.Count, "Path size increases, fire top-level events first.");
        }
    }

    [TestMethod]
    public void TestChangeEvent()
    {
        Init(users: 2);

        var array0 = Arrays[Users[0]];
        YChanges changes = null;

        array0.Observe(e =>
        {
            changes = e.Changes;
        });

        var newArr = new YArray();

        array0.Insert(0, newArr, 4, "dtrn");
        Assert.IsNotNull(changes);
        Assert.AreEqual(2, changes.Added.Count);
        Assert.AreEqual(0, changes.Deleted.Count);
        Assert.AreEqual(1, changes.Delta.Count);
        CollectionAssert.AreEqual(new object[] { newArr, 4, "dtrn" }, ((Delta.InsertItems)changes.Delta[0]).Items.Select(v => v.ToObject()).ToList());
        changes = null;

        array0.RemoveRange(0, 2);
        Assert.IsNotNull(changes);
        Assert.AreEqual(0, changes.Added.Count);
        Assert.AreEqual(2, changes.Deleted.Count);
        Assert.AreEqual(2, changes.Delta[0].Deleted());
        changes = null;

        array0.Insert(1, 0.1);
        Assert.IsNotNull(changes);
        Assert.AreEqual(1, changes.Added.Count);
        Assert.AreEqual(0, changes.Deleted.Count);
        Assert.AreEqual(2, changes.Delta.Count);
        Assert.AreEqual(1, changes.Delta[0].Retained());
        CollectionAssert.AreEqual(new object[] { 0.1 }, ((Delta.InsertItems)changes.Delta[1]).Items.Select(v => v.ToObject()).ToList());

        CompareUsers();
    }

    [TestMethod]
    public void TestInsertAndDeleteEventsForTypes2()
    {
        Init(users: 2);

        var array0 = Arrays[Users[0]];
        var events = new List<YArrayEvent>();

        array0.Observe(e =>
        {
            events.Add(e);
        });

        array0.Insert(0, "hi", new YMap());
        Assert.AreEqual(1, events.Count, "Event is triggered exactly once for insertion of two elements");
        array0.RemoveAt(1);
        Assert.AreEqual(2, events.Count, "Event is triggered exactly once for deletion");

        CompareUsers();
    }

    [TestMethod]
    public void TestNewChildDoesNotEmitEventInTransaction()
    {
        Init(users: 2);

        var array0 = Arrays[Users[0]];
        var fired = false;

        Users[0].Transact(tr =>
        {
            var newMap = new YMap();
            newMap.Observe(_ =>
            {
                fired = true;
            });

            array0.Insert(0, newMap);
            newMap["tst"] = 42;
        });

        Assert.IsFalse(fired, "Event does not trigger");
    }

    [TestMethod]
    public void TestGarbageCollector()
    {
        Init(users: 2);

        var array0 = Arrays[Users[0]];
        array0.Insert(0, "x", "y", "z");
        Connector.FlushAllMessages();

        Users[0].Disconnect();
        array0.RemoveRange(0, 3);
        Users[0].Connect();
        Connector.FlushAllMessages();

        CompareUsers();
    }

    [TestMethod]
    public void TestEventTargetIsSetCorrectlyOnLocal()
    {
        Init(users: 2);

        var array0 = Arrays[Users[0]];
        YEvent evt = null;

        array0.Observe(e =>
        {
            evt = e;
        });

        array0.Insert(0, "stuff");
        Assert.AreSame(array0, evt.Target, "Target property is set correctly");
    }

    [TestMethod]
    public void TestEventTargetIsSetCorrectlyOnRemote()
    {
        Init(users: 3);

        var array0 = Arrays[Users[0]];
        var array1 = Arrays[Users[1]];
        YEvent evt = null;

        array0.Observe(e =>
        {
            evt = e;
        });

        array1.Insert(0, "stuff");
        Connector.FlushAllMessages();

        Assert.AreSame(array0, evt.Target, "Target property is set correctly");

        CompareUsers();
    }

    [TestMethod]
    public void TestIteratingArrayContainingTypes()
    {
        Init(users: 1);
        var arr = Arrays[Users[0]];
        const int numItems = 10;

        for (int i = 0; i < numItems; i++)
        {
            var map = new YMap();
            map["value"] = i;
            arr.Add(map);
        }

        int cnt = 0;
        foreach (var item in arr)
        {
            Assert.AreEqual(cnt++, (item.ToObject() as YMap)["value"].ToObject());
        }
    }

    [TestMethod]
    public void TestSlice()
    {
        Init(users: 1);
        var arr = Arrays[Users[0]];

        arr.Insert(0, 1, 2, 3);
        CollectionAssert.AreEqual(new object[] { 1, 2, 3 }, arr.Slice(0, arr.Count).Select(v => v.ToObject()).ToArray());
        CollectionAssert.AreEqual(new object[] { 2, 3 }, arr.Slice(1, arr.Count - 1).Select(v => v.ToObject()).ToArray());
        CollectionAssert.AreEqual(new object[] { 1, 2 }, arr.Slice(0, arr.Count - 1).Select(v => v.ToObject()).ToArray());

        arr.Insert(0, 0);
        CollectionAssert.AreEqual(new object[] { 0, 1, 2, 3 }, arr.Slice(0, arr.Count).Select(v => v.ToObject()).ToArray());
        CollectionAssert.AreEqual(new object[] { 0, 1 }, arr.Slice(0, 2).Select(v => v.ToObject()).ToArray());
    }

    [TestMethod]
    [DataRow(5, 6)]
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
    [DataRow(5, 3_000)]
    /*
    [DataRow(5, 5_000)]
    [DataRow(5, 10_000)]
    [DataRow(5, 15_000)]
    */
    [TestCategory("Slow")]
    public void TestRepeatGeneratingYArrayTests(int users, int iterations)
    {
        RandomTests(new List<Action<TestYInstance, Random>>
        {
            // Insert
            (user, rand) =>
            {
                var yarray = Arrays[user];
                var uniqueNumber = GetUniqueNumber();
                var content = new List<object>();
                int len = rand.Next(1, 4);

                for (int i = 0; i < len; i++)
                {
                    content.Add(uniqueNumber);
                }

                int pos = rand.Next(0, yarray.Length + 1);
                var oldContent = yarray.ToArray().ToList();
                // Debug.WriteLine($"INSERT by {user.ClientId} at pos {pos}, oldContent: {string.Join(",", oldContent)}, adds: {string.Join(",", content)}");

                yarray.Insert(pos, content.Select(YValue.FromObject).ToArray());
                oldContent.InsertRange(pos, content.Select(YValue.FromObject));

                var newContent = yarray.ToArray();
                // Debug.WriteLine($"New content: {string.Join(",", newContent)}");
                CompareObjects(newContent, oldContent);
            },

            // Insert array
            (user, rand) =>
            {
                var yarray = Arrays[user];
                var pos = rand.Next(0, yarray.Length + 1);
                // Debug.WriteLine($"ARRAY by {user.ClientId} at pos {pos}");

                yarray.Insert(pos, new YArray());

                var arr = (YArray)yarray[pos].ToObject();
                arr.Insert(0, 1, 2, 3);
            },

            // Insert map
            (user, rand) =>
            {
                var yarray = Arrays[user];
                var pos = rand.Next(0, yarray.Length + 1);
                // Debug.WriteLine($"MAP by {user.ClientId} at pos {pos}");

                yarray.Insert(pos, new YMap());

                var map = (YMap)yarray[pos].ToObject();
                map["someprop"] = 42;
                map["someprop"] = 43;
                map["someprop"] = 44;
            },

            // Delete
            (user, rand) =>
            {
                var yarray = Arrays[user];
                int length = yarray.Length;

                if (length > 0)
                {
                    int somePos = rand.Next(0, length - 1);
                    int delLength = rand.Next(1, length - somePos);

                    var oldContent = yarray.ToArray().ToList();
                    // Debug.WriteLine($"DELETE by {user.ClientId} at pos {somePos}, len: {delLength}, oldContent: {string.Join(",", oldContent)}");

                    yarray.RemoveRange(somePos, delLength);
                    oldContent.RemoveRange(somePos, delLength);

                    var newContent = yarray.ToArray();
                    // Debug.WriteLine($"New content: {string.Join(",", newContent)}");
                    CompareObjects(newContent, oldContent);
                }
            }
        }, users, iterations);
    }

    [TestMethod]
    public void AddRange_ListOfValues_AppendsInOrder()
    {
        var doc = new YDoc();
        var array = doc.GetArray("a");
        array.Add(1);

        array.AddRange(new List<YValue> { 2, 3 });
        array.AddRange(new YValue[] { 4 });

        CollectionAssert.AreEqual(new object[] { 1, 2, 3, 4 }, array.Select(v => v.ToObject()).ToArray());
    }

    [TestMethod]
    public void InsertRange_LazySequenceBeforePrelim_InsertsAtIndex()
    {
        var array = new YArray();
        array.Add(1, 4);

        array.InsertRange(1, Enumerable.Range(2, 2).Select(i => (YValue)i));

        CollectionAssert.AreEqual(new object[] { 1, 2, 3, 4 }, array.Select(v => v.ToObject()).ToArray());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => array.InsertRange(5, new List<YValue> { 1 }));
    }

    [TestMethod]
    public void GetEnumerator_ObserverEditsDuringEnumeration_SeesTheCopyOfTheRun()
    {
        var doc = new YDoc();
        var array = doc.GetArray("a");
        array.Add(1, 2, 3);
        var seen = new List<object?>();

        foreach (var value in array)
        {
            if (seen.Count == 0)
            {
                array.RemoveRange(0, 3);
            }

            seen.Add(value.ToObject());
        }

        CollectionAssert.AreEqual(new object[] { 1, 2, 3 }, seen);
    }

    [TestMethod]
    public void GetEnumerator_MixedRuns_SkipsDeletedAndEnumeratesNested()
    {
        var doc = new YDoc();
        var array = doc.GetArray("a");
        array.Add(1, "x", new YMap());
        array.Add(2);
        array.RemoveRange(1, 1);
        IEnumerable<YValue> sequence = array;

        var seen = sequence.Select(v => v.Kind).ToArray();

        CollectionAssert.AreEqual(new[] { YValueKind.Number, YValueKind.SharedType, YValueKind.Number }, seen);
    }

    [TestMethod]
    public void GetEnumerator_EveryStoredKind_GivesItsValueAndBytesStayPrivate()
    {
        var doc = new YDoc();
        var array = doc.GetArray("a");
        array.Add(7, 0.5, "text", true, YValue.Null, 1L << 40, YValue.BigInt(3), new byte[] { 1, 2 });

        var seen = array.Select(v => v.Kind).ToArray();
        var copy = (byte[])array[7];
        copy[0] = 9;

        CollectionAssert.AreEqual(
            new[] { YValueKind.Number, YValueKind.Number, YValueKind.String, YValueKind.Boolean, YValueKind.Null, YValueKind.Number, YValueKind.BigInt, YValueKind.Bytes },
            seen);
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, (byte[])array[7]);
    }
}
