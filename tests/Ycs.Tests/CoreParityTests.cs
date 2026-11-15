using System;
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
}
