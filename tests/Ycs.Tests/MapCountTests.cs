using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// <see cref="YMap.Count"/> is kept by <see cref="ItemRef.Integrate"/> and <see cref="ItemRef.Delete"/>
/// (and recounted when a type is integrated); these tests hold it to the count that walks the map.
/// </summary>
[TestClass]
public class MapCountTests
{
    private static readonly string[] Keys = { "a", "b", "c", "d", "e" };

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Count_RandomEditsRemoteAppliesUndoAndGc_EqualsTheWalkedCount(bool gc)
    {
        for (var seed = 1; seed <= 30; seed++)
        {
            var mismatch = RunSession(seed, gc);

            Assert.IsNull(mismatch, $"seed {seed}: {mismatch}");
        }
    }

    [TestMethod]
    public void Count_RootMapReadFromAnUpdateBeforeItIsDefined_CountsItsKeys()
    {
        var source = new YDoc();
        var map = source.GetMap("m");
        map["a"] = 1;
        map["b"] = 2;
        map.Remove("a");
        var doc = new YDoc();
        doc.ApplyUpdate(source.EncodeStateAsUpdate());

        var realized = doc.GetMap("m");

        Assert.AreEqual(1, realized.Count);
    }

    [TestMethod]
    public void Count_ConcurrentSetsOfOneKey_CountsTheKeyOnce()
    {
        var doc1 = new YDoc(new YDocOptions { ClientId = 1 });
        var doc2 = new YDoc(new YDocOptions { ClientId = 2 });
        doc1.GetMap("m")["k"] = 1;
        doc2.GetMap("m")["k"] = 2;

        doc1.ApplyUpdate(doc2.EncodeStateAsUpdate());
        doc2.ApplyUpdate(doc1.EncodeStateAsUpdate());

        Assert.AreEqual("1|1", $"{doc1.GetMap("m").Count}|{doc2.GetMap("m").Count}");
    }

    [TestMethod]
    public void Count_NestedMapWhoseParentKeyIsDeleted_IsZero()
    {
        var doc = new YDoc();
        var root = doc.GetMap("m");
        var nested = new YMap();
        root["n"] = nested;
        nested["x"] = 1;

        root.Remove("n");

        Assert.AreEqual("0|0", $"{root.Count}|{nested.Count}");
    }

    private static string RunSession(int seed, bool gc)
    {
        var random = new Random(seed);
        var docs = new YDoc[3];
        var inboxes = new List<byte[]>[3];
        for (var i = 0; i < docs.Length; i++)
        {
            var index = i;
            docs[i] = new YDoc(new YDocOptions { ClientId = i + 1, GarbageCollection = gc });
            inboxes[i] = new List<byte[]>();
            docs[i].Updated += (_, e) =>
            {
                for (var j = 0; j < docs.Length; j++)
                {
                    if (j != index)
                    {
                        inboxes[j].Add(e.Update.ToArray());
                    }
                }
            };
        }

        var undo = new UndoManager(docs[0].GetMap("m"), new UndoManagerOptions { CaptureTimeout = TimeSpan.Zero });

        for (var step = 0; step < 150; step++)
        {
            var d = random.Next(docs.Length);
            var doc = docs[d];
            switch (random.Next(10))
            {
                case 0:
                case 1:
                case 2:
                    PickMap(doc, random)[Keys[random.Next(Keys.Length)]] = random.Next(100);
                    break;
                case 3:
                    PickMap(doc, random)[Keys[random.Next(Keys.Length)]] = new YMap(YValue.FromObject(new Dictionary<string, object> { ["x"] = 1, ["y"] = 2 }).EnumerateObject());
                    break;
                case 4:
                    PickMap(doc, random).Remove(Keys[random.Next(Keys.Length)]);
                    break;
                case 5:
                    if (random.Next(4) == 0)
                    {
                        PickMap(doc, random).Clear();
                    }

                    break;
                case 6:
                    // Deliver part of an inbox in a random order: some updates wait as pending.
                    var inbox = inboxes[d];
                    for (var n = random.Next(inbox.Count + 1); n > 0; n--)
                    {
                        var at = random.Next(inbox.Count);
                        doc.ApplyUpdate(inbox[at]);
                        inbox.RemoveAt(at);
                    }

                    break;
                case 7:
                    doc.Transact(_ =>
                    {
                        var map = PickMap(doc, random);
                        map["a"] = 1;
                        map.Remove("a");
                        map["b"] = "x";
                    });
                    break;
                case 8:
                    undo.Undo();
                    break;
                case 9:
                    undo.Redo();
                    break;
            }

            foreach (var each in docs)
            {
                var mismatch = Check(each.GetMap("m"));
                if (mismatch != null)
                {
                    return $"step {step}: {mismatch}";
                }
            }
        }

        // Converged: every update delivered.
        for (var i = 0; i < docs.Length; i++)
        {
            foreach (var update in inboxes[i])
            {
                docs[i].ApplyUpdate(update);
            }

            var mismatch = Check(docs[i].GetMap("m"));
            if (mismatch != null)
            {
                return $"after delivery, doc {i}: {mismatch}";
            }
        }

        return null;
    }

    private static YMap PickMap(YDoc doc, Random random)
    {
        var root = doc.GetMap("m");
        foreach (var key in Keys)
        {
            if (random.Next(3) == 0 && root.ContainsKey(key) && root[key].ToObject() is YMap nested)
            {
                return nested;
            }
        }

        return root;
    }

    private static string Check(YMap map)
    {
        if (map.Count != map.TypeMapCount())
        {
            return $"Count {map.Count}, walked {map.TypeMapCount()}";
        }

        foreach (var item in map.MapItems())
        {
            if (item.Content is ContentType { Type: YMap nested })
            {
                var mismatch = Check(nested);
                if (mismatch != null)
                {
                    return mismatch;
                }
            }
        }

        return null;
    }
}
