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
        var merged = YUpdate.Merge([B(GcThenItemFirst), B(GcThenItemSecond)], format: UpdateFormat.V1);

        Assert.AreEqual("AQMHACEBAW0BYQEAAwQBAXQFaGVsbG8BBwEABA==", S(merged));
    }

    [TestMethod]
    public void MergeUpdatesV2_GcFollowedByItem_KeepsTheItem()
    {
        var merged = YUpdate.Merge([
            YUpdate.Convert(B(GcThenItemFirst), UpdateFormat.V1, UpdateFormat.V2),
            YUpdate.Convert(B(GcThenItemSecond), UpdateFormat.V1, UpdateFormat.V2),
        ], format: UpdateFormat.V2);

        Assert.AreEqual(
            "AQMHACEBAW0BYQEAAwQBAXQFaGVsbG8BBwEABA==",
            S(YUpdate.Convert(merged, UpdateFormat.V2, UpdateFormat.V1)));
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
            a.Insert(0, "a", new YMap(YValue.FromObject(new Dictionary<string, object> { ["k2"] = "a" }).EnumerateObject()), "b");
            ((YMap)a[1].ToObject())["k2"] = new YMap(YValue.FromObject(new Dictionary<string, object> { ["k3"] = new Dictionary<string, object> { ["x"] = 1 } }).EnumerateObject());
            ((YMap)a[1].ToObject())["k2"] = YValue.FromObject(new List<object> { 1, 2 });
            m.Remove("k2");
        });
        doc.Transact(_ =>
        {
            m["k1"] = "b";
            ((YMap)a[1].ToObject()).Remove("k2");
        });
        doc.Transact(_ =>
        {
            a.RemoveRange(1, 2);
            a.Insert(0, YValue.FromObject(new Dictionary<string, object> { ["x"] = 1 }), 1);
            a.RemoveRange(0, 2);
        });

        Assert.AreEqual("AQcBAAgBAWEBdwFhgQEAAQABgQEBAQADKAEBbQJrMQF3AWJBAQACAQECAQYIAg==", S(doc.EncodeStateAsUpdate()));
    }

    [TestMethod]
    public void Cleanup_NestedMapWithOverwrittenKeyDeleted_BecomesOneGc()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 2 });
        var r = doc.GetMap("r");
        var n = new YMap();
        r["n"] = n;
        n["k"] = 1;
        n["k"] = 2;
        n["k"] = 3;
        n["k"] = 4;

        r.Remove("n");

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
            (S(doc.EncodeStateAsUpdate()), S(doc.EncodeStateAsUpdate(format: UpdateFormat.V2))));
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
        doc.EncodeStateAsUpdate(v2, format: UpdateFormat.V2);

        Assert.AreEqual(
            (S(doc.EncodeStateAsUpdate()), S(doc.EncodeStateAsUpdate(format: UpdateFormat.V2))),
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

        doc.ApplyUpdate(B(update), format: UpdateFormat.V2);

        Assert.AreEqual((stateVector, update), (S(doc.EncodeStateVector()), S(doc.EncodeStateAsUpdate(format: UpdateFormat.V2))));
    }

    [TestMethod]
    public void UpdateOperations_ManyStructsInFewBytes_AreAccepted()
    {
        var update = B("AAAESZ6cAQAFAAOcnAEDAQBBAwFhAQEBAARBnpwBAaCcAQABCQEAn5wB");

        var v1 = YUpdate.Convert(update, UpdateFormat.V2, UpdateFormat.V1);

        Assert.AreEqual(S(update), S(YUpdate.Convert(v1, UpdateFormat.V1, UpdateFormat.V2)));
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
            doc.ApplyUpdate(B(MaxClientV2), format: UpdateFormat.V2);
        }
        else
        {
            doc.ApplyUpdate(B(MaxClientV1));
        }

        Assert.AreEqual(
            ("ax", "Af////////8PBQ==", MaxClientV1, MaxClientV2),
            (doc.GetText("t").ToString(), S(doc.EncodeStateVector()), S(doc.EncodeStateAsUpdate()), S(doc.EncodeStateAsUpdate(format: UpdateFormat.V2))));
    }

    [TestMethod]
    public void ClientId_Of53Bits_WritesYjsBytes()
    {
        var doc = new YDoc(new YDocOptions { ClientId = (1L << 53) - 1 });

        doc.GetText("t").Insert(0, "max");
        doc.GetText("t").Remove(0, 1);
        doc.GetMap("m")["a"] = 1;
        doc.GetMap("m")["a"] = 2;

        Assert.AreEqual((MaxClientV1, MaxClientV2), (S(doc.EncodeStateAsUpdate()), S(doc.EncodeStateAsUpdate(format: UpdateFormat.V2))));
    }

    [TestMethod]
    public void UpdateOperations_ClientIdOf40Bits_MergesAndConverts()
    {
        var update = B("AQGFgICAgCAABAEBdANiaWcA");

        var v2 = YUpdate.Convert(update, UpdateFormat.V1, UpdateFormat.V2);

        Assert.AreEqual(
            (S(update), "AYWAgICAIAM="),
            (S(YUpdate.Merge([update, YUpdate.Convert(v2, UpdateFormat.V2, UpdateFormat.V1)], format: UpdateFormat.V1)), S(YUpdate.EncodeStateVector(v2, format: UpdateFormat.V2))));
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
        var doc = new YDoc(new YDocOptions { GarbageCollection = false });
        doc.ApplyUpdate(B("AQEFAAQBAXQLaGVsbG8gd29ybGQA"));
        var snapshot = Snapshot.Decode(B("AgYBAAEFAQAFAgYDBQs="));

        var delta = doc.GetText("t").ToDelta(snapshot);

        Assert.AreEqual(" world", string.Concat(System.Linq.Enumerable.Select(delta, d => d.Inserted().GetString())));
    }

    private static (byte[] First, byte[] Second) LargeUpdateAfterItsBase()
    {
        var source = new YDoc(new YDocOptions { ClientId = 1 });
        var text = source.GetText("t");
        text.Insert(0, "a");
        var first = source.EncodeStateAsUpdate();
        var stateVector = source.EncodeStateVector();
        text.Insert(1, new string('b', 17 << 20));
        return (first, source.EncodeStateAsUpdate(stateVector));
    }

    [TestMethod]
    public void ApplyUpdate_LargeUpdateBeforeItsBase_IsRejectedByDefault()
    {
        var (_, second) = LargeUpdateAfterItsBase();

        Assert.IsFalse(new YDoc().TryApplyUpdate(second));
    }

    [TestMethod]
    public void ApplyUpdate_LargeUpdateBeforeItsBase_WaitsWithRaisedLimits()
    {
        var (first, second) = LargeUpdateAfterItsBase();
        var doc = new YDoc(new YDocOptions { Limits = UpdateLimits.Unlimited });

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
        var merged = YUpdate.Merge([.. GrowingTextStates.Select(B).ToList()], format: UpdateFormat.V1);

        Assert.AreEqual("AQYeAAQBAXQBYYQeAAdiY2RlZmdohB4HB2lqa2xtbm+EHg4HcHFyc3R1doQeFQF3hB4WAXgA", S(merged));
    }

    [TestMethod]
    public void MergeUpdatesV2_MoreThan16UpdatesWithTies_PicksTheDecodersAsYjs()
    {
        var merged = YUpdate.Merge([.. GrowingTextStates.Select(u => YUpdate.Convert(B(u), UpdateFormat.V1, UpdateFormat.V2)).ToList()], format: UpdateFormat.V2);

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
            root["k" + i] = kid;
            kids.Add(kid);
        }

        var order = new List<string>();
        root.ObserveDeep((deepEvents, _) => order.AddRange(deepEvents.Select(ev => "k" + kids.IndexOf((YMap)ev.Target))));

        doc.Transact(_ =>
        {
            for (var i = 39; i >= 0; i--)
            {
                kids[i]["v"] = i;
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
        var embed = (IDictionary<string, object>)doc.GetText("t").ToDelta()[0].Inserted().ToObject();

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

    [TestMethod]
    public void Get_RootDefinedAsAnotherType_Throws()
    {
        var doc = new YDoc();
        doc.GetXmlFragment("x");

        Assert.ThrowsExactly<InvalidOperationException>(() => doc.GetXmlText("x"));
    }

    [TestMethod]
    public void ApplyUpdate_ContentJsonWithUndefined_ReadsNullAndReEncodesUndefined()
    {
        // A legacy ContentJSON item in root map 'a': ['undefined', '{"k":1}'].
        const string update = "AQECAAIBAWECCXVuZGVmaW5lZAd7ImsiOjF9AA==";
        var doc = new YDoc();

        doc.ApplyUpdate(B(update));

        Assert.AreEqual(update, S(doc.EncodeStateAsUpdate()));
    }
}
