using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Scenarios of the core (structs, store, updates) whose expected bytes and states were produced
/// by Yjs 13.6.32 and are pinned inline.
/// </summary>
[TestClass]
public class CoreParityTests
{
    private static byte[] B(string base64) => Convert.FromBase64String(base64);

    private static string S(byte[] bytes) => Convert.ToBase64String(bytes);

    // Client 7: m.set('a', new Y.Array), arr.insert(0, [1, 2, 3]), m.delete('a'), then t.insert(0, 'hello').
    private const string GcThenItemFirst = "AQIHACEBAW0BYQEAAwEHAQAE";
    private const string GcThenItemSecond = "AQEHBAQBAXQFaGVsbG8BBwEABA==";

    [TestMethod]
    public void MergeUpdates_GcFollowedByItem_KeepsTheItem()
    {
        var merged = UpdateOperations.MergeUpdates([B(GcThenItemFirst), B(GcThenItemSecond)]);

        Assert.AreEqual("AQMHACEBAW0BYQEAAwQBAXQFaGVsbG8BBwEABA==", S(merged));
    }

    [TestMethod]
    public void MergeUpdatesV2_GcFollowedByItem_KeepsTheItem()
    {
        var merged = UpdateOperations.MergeUpdatesV2([
            UpdateOperations.ConvertUpdateFormatV1ToV2(B(GcThenItemFirst)),
            UpdateOperations.ConvertUpdateFormatV1ToV2(B(GcThenItemSecond)),
        ]);

        Assert.AreEqual(
            "AQMHACEBAW0BYQEAAwQBAXQFaGVsbG8BBwEABA==",
            S(UpdateOperations.ConvertUpdateFormatV2ToV1(merged)));
    }

    [TestMethod]
    [DataRow(new[] { "B", "A", "C" })]
    [DataRow(new[] { "A", "B", "C" })]
    public void ApplyUpdate_GcAndItemPendingTogether_KeepsTheItem(string[] order)
    {
        // A = GC@1+3, B = Item@4 "hello", C = Item@0 (the array type); Yjs after any order: "hello".
        var updates = new System.Collections.Generic.Dictionary<string, string>
        {
            ["A"] = "AQEHAQADAQcBAAQ=",
            ["B"] = "AQEHBAQBAXQFaGVsbG8BBwEABA==",
            ["C"] = "AQEHACcBAW0BYQAA",
        };
        var doc = new YDoc();

        foreach (var name in order)
        {
            doc.ApplyUpdate(B(updates[name]));
        }

        Assert.AreEqual(
            ("hello", "AQMHACEBAW0BYQEAAwQBAXQFaGVsbG8BBwEABA=="),
            (doc.GetText("t").ToString(), S(doc.EncodeStateAsUpdate())));
    }

    [TestMethod]
    public void Cleanup_DeletedStructsAcrossTransactions_MergeToTheLeftAsFarAsPossible()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var m = doc.GetMap("m");
        var a = doc.GetArray("a");

        doc.Transact(_ =>
        {
            a.Insert(0, new object[] { "a", new YMap(new Dictionary<string, object> { ["k2"] = "a" }), "b" });
            ((YMap)a.Get(1)).Set("k2", new YMap(new Dictionary<string, object> { ["k3"] = new Dictionary<string, object> { ["x"] = 1 } }));
            ((YMap)a.Get(1)).Set("k2", new List<object> { 1, 2 });
            m.Delete("k2");
        });
        doc.Transact(_ =>
        {
            m.Set("k1", "b");
            ((YMap)a.Get(1)).Delete("k2");
        });
        doc.Transact(_ =>
        {
            a.Delete(1, 2);
            a.Insert(0, new object[] { new Dictionary<string, object> { ["x"] = 1 }, 1 });
            a.Delete(0, 2);
        });

        Assert.AreEqual("AQcBAAgBAWEBdwFhgQEAAQABgQEBAQADKAEBbQJrMQF3AWJBAQACAQECAQYIAg==", S(doc.EncodeStateAsUpdate()));
    }

    [TestMethod]
    public void Cleanup_NestedMapWithOverwrittenKeyDeleted_BecomesOneGc()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 2 });
        var r = doc.GetMap("r");
        var n = new YMap();
        r.Set("n", n);
        n.Set("k", 1);
        n.Set("k", 2);
        n.Set("k", 3);
        n.Set("k", 4);

        r.Delete("n");

        Assert.AreEqual("AQICACEBAXIBbgEABAECAQAF", S(doc.EncodeStateAsUpdate()));
    }
}
