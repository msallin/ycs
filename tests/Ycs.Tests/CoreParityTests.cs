using System;
using System.Collections.Generic;
using System.Linq;
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

    // Client 20: "one" (u1), " two" (u2), delete clock 0 (u3); client 21: "B". A server has B, u2 and u3.
    private static YDoc ServerWithPending()
    {
        var doc = new YDoc();
        doc.ApplyUpdate(B("AQEVAAQBAXQBQgA="));
        doc.ApplyUpdate(B("AQEUA4QUAgQgdHdvAA=="));
        doc.ApplyUpdate(B("AAEUAQAB"));
        return doc;
    }

    [TestMethod]
    public void EncodeStateAsUpdate_WithPending_IncludesPendingStructsAndDeletions()
    {
        var doc = ServerWithPending();

        Assert.AreEqual(
            ("AgEVAAQBAXQBQgEUA4QUAgQgdHdvARQBAAE=", "AAADFVQAAQQAAwQAhAoGdEIgdHdvQQAEAQEAAAIBAAEDARQBAAA="),
            (S(doc.EncodeStateAsUpdate()), S(doc.EncodeStateAsUpdateV2())));
    }

    [TestMethod]
    [DataRow("ARQD", "AgEVAAQBAXQBQgEUA4QUAgQgdHdvARQBAAE=")]
    [DataRow("ARQH", "AQEVAAQBAXQBQgEUAQAB")]
    public void EncodeStateAsUpdate_WithPendingAndTargetStateVector_DiffsOnlyThePendingStructs(string target, string expected)
    {
        var doc = ServerWithPending();

        Assert.AreEqual(expected, S(doc.EncodeStateAsUpdate(B(target))));
    }

    [TestMethod]
    public void EncodeStateAsUpdate_WithPending_IntoBufferWriterEqualsTheArray()
    {
        var doc = ServerWithPending();
        var v1 = new System.Buffers.ArrayBufferWriter<byte>();
        var v2 = new System.Buffers.ArrayBufferWriter<byte>();

        doc.EncodeStateAsUpdate(v1);
        doc.EncodeStateAsUpdateV2(v2);

        Assert.AreEqual(
            (S(doc.EncodeStateAsUpdate()), S(doc.EncodeStateAsUpdateV2())),
            (S(v1.WrittenSpan.ToArray()), S(v2.WrittenSpan.ToArray())));
    }

    [TestMethod]
    public void EncodeStateAsUpdate_PersistedWithPendingThenRestarted_KeepsWhatWasPending()
    {
        var restarted = new YDoc();
        restarted.ApplyUpdate(ServerWithPending().EncodeStateAsUpdate());

        restarted.ApplyUpdate(B("AQEUAAQBAXQDb25lAA=="));

        Assert.AreEqual("ne twoB", restarted.GetText("t").ToString());
    }

    [TestMethod]
    public void ApplyUpdate_ItemWithGcOrigin_BecomesGcAsInYjs()
    {
        // Client 2's item: origin (1,1) is collected, right origin (1,3) an item of root array 'r'.
        var doc = new YDoc();
        doc.ApplyUpdate(B("AQMBACEBAW0BYQEAAQgBAXICdwFMdwFSAQEBAAI="));

        doc.ApplyUpdate(B("AQECAMgBAQEDAXcBWgA="));

        Assert.AreEqual(
            (2, "AgECAAABAwEAIQEBbQFhAQABCAEBcgJ3AUx3AVICAgEAAQEBAAI="),
            (doc.GetArray("r").Length, S(doc.EncodeStateAsUpdate())));
    }

    [TestMethod]
    public void ApplyUpdate_ItemWithOriginsInTwoParents_TakesTheLeftOriginsParent()
    {
        // p = [a], q = [b]; client 2's item has its origin in p and its right origin in q.
        var doc = new YDoc();
        doc.ApplyUpdate(B("AQIBAAgBAXABdwFhCAEBcQF3AWIA"));

        doc.ApplyUpdate(B("AQECAMgBAAEBAXcBWgA="));

        Assert.AreEqual(
            (2, 1, "AgECAMgBAAEBAXcBWgIBAAgBAXABdwFhCAEBcQF3AWIA"),
            (doc.GetArray("p").Length, doc.GetArray("q").Length, S(doc.EncodeStateAsUpdate())));
    }

    [TestMethod]
    [DataRow("AAAESZ6cAQAFAAOcnAEDAQBBAwFhAQEBAARBnpwBAaCcAQABCQEAn5wB", "AQmgnAE=")] // 20,000 items inserted at 0, all deleted: 42 bytes
    [DataRow("AAAESp6cAQAFAAOcnAEHAQBBm5wBSAMBcQEBAQAEQZ6cAQGgnAEAfQF9AX0BAQoBAJycAQ==", "AQqgnAE=")] // a list used as a stack: 52 bytes
    public void ApplyUpdateV2_ManyStructsInFewBytes_AppliesAndReEncodes(string update, string stateVector)
    {
        var doc = new YDoc();

        doc.ApplyUpdateV2(B(update));

        Assert.AreEqual((stateVector, update), (S(doc.EncodeStateVector()), S(doc.EncodeStateAsUpdateV2())));
    }

    [TestMethod]
    public void UpdateOperations_ManyStructsInFewBytes_AreAccepted()
    {
        var update = B("AAAESZ6cAQAFAAOcnAEDAQBBAwFhAQEBAARBnpwBAaCcAQABCQEAn5wB");

        var v1 = UpdateOperations.ConvertUpdateFormatV2ToV1(update);

        Assert.AreEqual(S(update), S(UpdateOperations.ConvertUpdateFormatV1ToV2(v1)));
    }

    // Client 2^53 - 1: t.insert(0, 'max'), t.delete(0, 1), m.set('a', 1), m.set('a', 2).
    private const string MaxClientV1 = "AQT/////////DwABAQF0AYT/////////DwACYXghAQFtAWEBqP////////8PAwF9AgH/////////DwIAAQMB";
    private const string MaxClientV2 = "AAAJ/////////x8BAgAGAAcBAIQAIQCoCgV0YXhtYQECQQABAQACQQEBBAB9AgH/////////DwIAAAIA";

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ApplyUpdate_ClientIdOf53Bits_AppliesAndReEncodes(bool v2)
    {
        var doc = new YDoc();

        if (v2)
        {
            doc.ApplyUpdateV2(B(MaxClientV2));
        }
        else
        {
            doc.ApplyUpdate(B(MaxClientV1));
        }

        Assert.AreEqual(
            ("ax", "Af////////8PBQ==", MaxClientV1, MaxClientV2),
            (doc.GetText("t").ToString(), S(doc.EncodeStateVector()), S(doc.EncodeStateAsUpdate()), S(doc.EncodeStateAsUpdateV2())));
    }

    [TestMethod]
    public void ClientId_Of53Bits_WritesYjsBytes()
    {
        var doc = new YDoc(new YDocOptions { ClientId = (1L << 53) - 1 });

        doc.GetText("t").Insert(0, "max");
        doc.GetText("t").Delete(0, 1);
        doc.GetMap("m").Set("a", 1);
        doc.GetMap("m").Set("a", 2);

        Assert.AreEqual((MaxClientV1, MaxClientV2), (S(doc.EncodeStateAsUpdate()), S(doc.EncodeStateAsUpdateV2())));
    }

    [TestMethod]
    public void UpdateOperations_ClientIdOf40Bits_MergesAndConverts()
    {
        var update = B("AQGFgICAgCAABAEBdANiaWcA");

        var v2 = UpdateOperations.ConvertUpdateFormatV1ToV2(update);

        Assert.AreEqual(
            (S(update), "AYWAgICAIAM="),
            (S(UpdateOperations.MergeUpdates([update, UpdateOperations.ConvertUpdateFormatV2ToV1(v2)])), S(UpdateOperations.EncodeStateVectorFromUpdateV2(v2))));
    }

    [TestMethod]
    public void ApplyUpdate_ClientIdBeyond53Bits_IsRejected()
    {
        // Client 2^53: one struct.
        var doc = new YDoc();

        Assert.ThrowsExactly<YUpdateException>(() => doc.ApplyUpdate(B("AQGAgICAgICAEAAEAQF0A2JpZwA=")));
    }

    [TestMethod]
    public void ToDelta_SnapshotNewerThanTheDocument_SkipsWhatTheDocumentDoesNotHave()
    {
        // The snapshot deletes "hello" and clocks of a client 6 this document has never seen.
        var doc = new YDoc(new YDocOptions { Gc = false });
        doc.ApplyUpdate(B("AQEFAAQBAXQLaGVsbG8gd29ybGQA"));
        var snapshot = Snapshot.DecodeSnapshot(B("AgYBAAEFAQAFAgYDBQs="));

        var delta = doc.GetText("t").ToDelta(snapshot);

        Assert.AreEqual(" world", string.Concat(System.Linq.Enumerable.Select(delta, d => d.Insert)));
    }

    [TestMethod]
    public void ApplyUpdate_LargeUpdateBeforeItsBase_WaitsWithoutALimitByDefault()
    {
        var source = new YDoc(new YDocOptions { ClientId = 1 });
        var text = source.GetText("t");
        text.Insert(0, "a");
        var first = source.EncodeStateAsUpdate();
        var stateVector = source.EncodeStateVector();
        text.Insert(1, new string('b', 17 << 20));
        var second = source.EncodeStateAsUpdate(stateVector);
        var doc = new YDoc();

        doc.ApplyUpdate(second);
        doc.ApplyUpdate(first);

        Assert.AreEqual((17 << 20) + 1, doc.GetText("t").Length);
    }

    // 24 full states of a growing text of client 30, shuffled: several updates start at the same clock.
    private static readonly string[] GrowingTextStates =
        [
            "AQEeAAQBAXQBYQA=",
            "AQEeAAQBAXQIYWJjZGVmZ2gA",
            "AQEeAAQBAXQPYWJjZGVmZ2hpamtsbW5vAA==",
            "AQEeAAQBAXQWYWJjZGVmZ2hpamtsbW5vcHFyc3R1dgA=",
            "AQEeAAQBAXQFYWJjZGUA",
            "AQEeAAQBAXQMYWJjZGVmZ2hpamtsAA==",
            "AQEeAAQBAXQTYWJjZGVmZ2hpamtsbW5vcHFycwA=",
            "AQEeAAQBAXQCYWIA",
            "AQEeAAQBAXQJYWJjZGVmZ2hpAA==",
            "AQEeAAQBAXQQYWJjZGVmZ2hpamtsbW5vcAA=",
            "AQEeAAQBAXQXYWJjZGVmZ2hpamtsbW5vcHFyc3R1dncA",
            "AQEeAAQBAXQGYWJjZGVmAA==",
            "AQEeAAQBAXQNYWJjZGVmZ2hpamtsbQA=",
            "AQEeAAQBAXQUYWJjZGVmZ2hpamtsbW5vcHFyc3QA",
            "AQEeAAQBAXQDYWJjAA==",
            "AQEeAAQBAXQKYWJjZGVmZ2hpagA=",
            "AQEeAAQBAXQRYWJjZGVmZ2hpamtsbW5vcHEA",
            "AQEeAAQBAXQYYWJjZGVmZ2hpamtsbW5vcHFyc3R1dnd4AA==",
            "AQEeAAQBAXQHYWJjZGVmZwA=",
            "AQEeAAQBAXQOYWJjZGVmZ2hpamtsbW4A",
            "AQEeAAQBAXQVYWJjZGVmZ2hpamtsbW5vcHFyc3R1AA==",
            "AQEeAAQBAXQEYWJjZAA=",
            "AQEeAAQBAXQLYWJjZGVmZ2hpamsA",
            "AQEeAAQBAXQSYWJjZGVmZ2hpamtsbW5vcHFyAA==",
        ];

    [TestMethod]
    public void MergeUpdates_MoreThan16UpdatesWithTies_PicksTheDecodersAsYjs()
    {
        var merged = UpdateOperations.MergeUpdates(GrowingTextStates.Select(B).ToList());

        Assert.AreEqual("AQYeAAQBAXQBYYQeAAdiY2RlZmdohB4HB2lqa2xtbm+EHg4HcHFyc3R1doQeFQF3hB4WAXgA", S(merged));
    }

    [TestMethod]
    public void MergeUpdatesV2_MoreThan16UpdatesWithTies_PicksTheDecodersAsYjs()
    {
        var merged = UpdateOperations.MergeUpdatesV2(GrowingTextStates.Select(u => UpdateOperations.ConvertUpdateFormatV1ToV2(B(u))).ToList());

        Assert.AreEqual("AAACXgQEAA8BAgADBACEIBl0YWJjZGVmZ2hpamtsbW5vcHFyc3R1dnd4QQBHAUEAAQEAAAEGAAA=", S(merged));
    }

    [TestMethod]
    public void ObserveDeep_ManyNestedTypesChanged_EventsInTheOrderOfChange()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 4 });
        var root = doc.GetMap("root");
        var kids = new List<YMap>();
        for (var i = 0; i < 40; i++)
        {
            var kid = new YMap();
            root.Set("k" + i, kid);
            kids.Add(kid);
        }

        var order = new List<string>();
        root.ObserveDeep((_, e) => order.AddRange(e.Events.Select(ev => "k" + kids.IndexOf((YMap)ev.Target))));

        doc.Transact(_ =>
        {
            for (var i = 39; i >= 0; i--)
            {
                kids[i].Set("v", i);
            }
        });

        Assert.AreEqual(string.Join(",", Enumerable.Range(0, 40).Reverse().Select(i => "k" + i)), string.Join(",", order));
    }

    [TestMethod]
    public void ApplyUpdate_JsonEmbedWithLoneSurrogateEscapes_IsReadAndReEncodedUnchanged()
    {
        // Yjs: insertEmbed(0, { v: 'a\uD83Db', 'k\uDC00': 1 }), v1: the JSON holds "\ud83d" and "\udc00".
        const string update = "AQEBAAUBAXQceyJ2IjoiYVx1ZDgzZGIiLCJrXHVkYzAwIjoxfQA=";
        var doc = new YDoc();

        doc.ApplyUpdate(B(update));
        var embed = (IDictionary<string, object>)doc.GetText("t").ToDelta()[0].Insert;

        Assert.AreEqual(("a\uD83Db", 1, update), ((string)embed["v"], embed["k\uDC00"], S(doc.EncodeStateAsUpdate())));
    }

    [TestMethod]
    public void ApplyUpdate_StringNotValidUtf8_IsRejectedAsByYjs()
    {
        // A text item "\xFFri": lib0's TextDecoder is fatal.
        var doc = new YDoc();

        Assert.ThrowsExactly<YUpdateException>(() => doc.ApplyUpdate(B("AQEBAAQBAXQD/3JpAA==")));
        Assert.AreEqual(string.Empty, doc.GetText("t").ToString());
    }

    [TestMethod]
    public void ApplyUpdate_StringWithByteOrderMark_KeepsIt()
    {
        var source = new YDoc(new YDocOptions { ClientId = 1 });
        source.GetText("t").Insert(0, "\uFEFFx");
        var doc = new YDoc();

        doc.ApplyUpdate(source.EncodeStateAsUpdate());

        Assert.AreEqual("\uFEFFx", doc.GetText("t").ToString());
    }
}
