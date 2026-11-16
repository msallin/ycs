// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Tests for <see cref="UpdateOperations"/> (ported from Yjs' <c>tests/updates.tests.js</c>,
/// Yjs 13.6.32), covering both the v1 and v2 wire formats.
/// </summary>
[TestClass]
public class UpdateOperationsTests
{
    /// <summary>
    /// Bundles the v1 or v2 flavor of every operation under test, mirroring the 'Enc' fixture
    /// in Yjs' own updates.tests.js.
    /// </summary>
    private sealed class Enc
    {
        public bool IsV2;
        public Func<IList<byte[]>, byte[]> MergeUpdates;
        public Func<YDoc, byte[]> EncodeStateAsUpdate;
        public Action<YDoc, byte[]> ApplyUpdate;
        public Func<byte[], byte[]> EncodeStateVectorFromUpdate;
        public Func<YDoc, byte[]> EncodeStateVector;
        public Func<byte[], UpdateMeta> ParseUpdateMeta;
        public Func<byte[], byte[], byte[]> DiffUpdate;
        public Func<byte[], DecodedUpdate> DecodeUpdate;
        public Action<YDoc, EventHandler<(byte[] data, object origin, Transaction transaction)>> Subscribe;
    }

    private static Enc EncV1 => new Enc
    {
        IsV2 = false,
        MergeUpdates = u => UpdateOperations.MergeUpdates(u),
        EncodeStateAsUpdate = doc => doc.EncodeStateAsUpdateV1(),
        ApplyUpdate = (doc, u) => doc.ApplyUpdateV1(u),
        EncodeStateVectorFromUpdate = u => UpdateOperations.EncodeStateVectorFromUpdate(u),
        EncodeStateVector = doc => doc.EncodeStateVectorV1(),
        ParseUpdateMeta = u => UpdateOperations.ParseUpdateMeta(u),
        DiffUpdate = (u, sv) => UpdateOperations.DiffUpdate(u, sv),
        DecodeUpdate = u => UpdateOperations.DecodeUpdate(u),
        Subscribe = (doc, handler) => doc.Update += handler,
    };

    private static Enc EncV2 => new Enc
    {
        IsV2 = true,
        MergeUpdates = u => UpdateOperations.MergeUpdatesV2(u),
        EncodeStateAsUpdate = doc => doc.EncodeStateAsUpdateV2(),
        ApplyUpdate = (doc, u) => doc.ApplyUpdateV2(u),
        EncodeStateVectorFromUpdate = u => UpdateOperations.EncodeStateVectorFromUpdateV2(u),
        EncodeStateVector = doc => doc.EncodeStateVectorV2(),
        ParseUpdateMeta = u => UpdateOperations.ParseUpdateMetaV2(u),
        DiffUpdate = (u, sv) => UpdateOperations.DiffUpdateV2(u, sv),
        DecodeUpdate = u => UpdateOperations.DecodeUpdateV2(u),
        Subscribe = (doc, handler) => doc.UpdateV2 += handler,
    };

    private static IEnumerable<Enc> Encoders => new[] { EncV1, EncV2 };

    #region MergeUpdates - basic

    [TestMethod]
    public void MergeUpdates_TwoIndependentDocs_ConvergeOnBoth()
    {
        foreach (var enc in Encoders)
        {
            var doc1 = new YDoc(new YDocOptions { Gc = false });
            var doc2 = new YDoc(new YDocOptions { Gc = false });

            doc1.GetArray("array").Insert(0, new List<object> { 1 });
            doc2.GetArray("array").Insert(0, new List<object> { 2 });

            var update1 = enc.EncodeStateAsUpdate(doc1);
            var update2 = enc.EncodeStateAsUpdate(doc2);

            var merged = enc.MergeUpdates(new[] { update1, update2 });

            var doc3 = new YDoc(new YDocOptions { Gc = false });
            enc.ApplyUpdate(doc3, merged);

            var result = doc3.GetArray("array").ToArray();
            Assert.AreEqual(2, result.Count);
            CollectionAssert.Contains((System.Collections.ICollection)result, 1);
            CollectionAssert.Contains((System.Collections.ICollection)result, 2);
        }
    }

    [TestMethod]
    public void MergeUpdates_SingleUpdate_ReturnsItUnchanged()
    {
        foreach (var enc in Encoders)
        {
            var doc = new YDoc(new YDocOptions { Gc = false });
            doc.GetArray("array").Insert(0, new List<object> { "x" });
            var update = enc.EncodeStateAsUpdate(doc);

            var merged = enc.MergeUpdates(new[] { update });

            CollectionAssert.AreEqual(update, merged);
        }
    }

    #endregion

    #region MergeUpdates - the 5-case matrix (ported from Yjs' checkUpdateCases)

    [TestMethod]
    public void MergeUpdates_FourArrayInserts_NoDeletes_AllCasesConverge()
    {
        foreach (var enc in Encoders)
        {
            var (doc, updates) = CaptureUpdates(enc, array =>
            {
                array.Insert(0, new List<object> { 1 });
                array.Insert(0, new List<object> { 2 });
                array.Insert(0, new List<object> { 3 });
                array.Insert(0, new List<object> { 4 });
            });

            CheckUpdateCases(enc, doc, updates);
        }
    }

    [TestMethod]
    public void MergeUpdates_InsertsAndDeletes_AllCasesConverge()
    {
        foreach (var enc in Encoders)
        {
            var (doc, updates) = CaptureUpdates(enc, array =>
            {
                array.Insert(0, new List<object> { 1, 2 });
                array.Delete(1, 1);
                array.Insert(0, new List<object> { 3, 4 });
                array.Delete(1, 2);
            });

            CheckUpdateCases(enc, doc, updates);
        }
    }

    private static (YDoc Doc, List<byte[]> Updates) CaptureUpdates(Enc enc, Action<YArray> ops)
    {
        var doc = new YDoc(new YDocOptions { Gc = false });
        var updates = new List<byte[]>();

        enc.Subscribe(doc, (s, e) => updates.Add(e.data));

        ops(doc.GetArray("array"));

        Assert.IsTrue(updates.Count >= 4, "Expected at least 4 incremental updates.");
        return (doc, updates);
    }

    private static void CheckUpdateCases(Enc enc, YDoc targetDoc, List<byte[]> updates)
    {
        var cases = new List<byte[]>
        {
            // Case 1: simply merge everything.
            enc.MergeUpdates(updates),

            // Case 2: overlapping updates.
            enc.MergeUpdates(new[]
            {
                enc.MergeUpdates(updates.Skip(2).ToList()),
                enc.MergeUpdates(updates.Take(2).ToList()),
            }),

            // Case 3: overlapping updates.
            enc.MergeUpdates(new[]
            {
                enc.MergeUpdates(updates.Skip(2).ToList()),
                enc.MergeUpdates(updates.Skip(1).Take(2).ToList()),
                updates[0],
            }),

            // Case 4: separated updates - the intermediate merges contain 'Skip' gaps.
            enc.MergeUpdates(new[]
            {
                enc.MergeUpdates(new[] { updates[0], updates[2] }),
                enc.MergeUpdates(new[] { updates[1], updates[3] }),
                enc.MergeUpdates(updates.Skip(4).ToList()),
            }),
        };

        // Case 5: overlapping with many duplicates.
        cases.Add(enc.MergeUpdates(cases));

        foreach (var mergedUpdate in cases)
        {
            var merged = new YDoc(new YDocOptions { Gc = false });
            enc.ApplyUpdate(merged, mergedUpdate);

            CollectionAssert.AreEqual(
                targetDoc.GetArray("array").ToArray().ToList(),
                merged.GetArray("array").ToArray().ToList());

            AssertStateVectorsEqual(enc.EncodeStateVector(merged), enc.EncodeStateVectorFromUpdate(mergedUpdate));

            var meta = enc.ParseUpdateMeta(mergedUpdate);
            foreach (var kvp in meta.From)
            {
                Assert.AreEqual(0, kvp.Value, "Every client should start at clock 0.");
            }

            foreach (var kvp in meta.To)
            {
                Assert.AreEqual(merged.Store.GetState(kvp.Key), kvp.Value);
            }
        }
    }

    #endregion

    #region DiffUpdate

    [TestMethod]
    public void DiffUpdate_AgainstEmptyStateVector_EqualsWholeUpdate()
    {
        foreach (var enc in Encoders)
        {
            var doc = new YDoc(new YDocOptions { Gc = false });
            doc.GetArray("array").Insert(0, new List<object> { 1, 2, 3 });
            var update = enc.EncodeStateAsUpdate(doc);

            var diffed = enc.DiffUpdate(update, EncodeEmptyStateVector());

            var applied = new YDoc(new YDocOptions { Gc = false });
            enc.ApplyUpdate(applied, diffed);

            CollectionAssert.AreEqual(new object[] { 1, 2, 3 }, (System.Collections.ICollection)applied.GetArray("array").ToArray());
        }
    }

    [TestMethod]
    public void DiffUpdate_AgainstFullStateVector_IsEmptyOfStructs()
    {
        foreach (var enc in Encoders)
        {
            var doc = new YDoc(new YDocOptions { Gc = false });
            doc.GetArray("array").Insert(0, new List<object> { 1, 2, 3 });
            var update = enc.EncodeStateAsUpdate(doc);
            var fullSv = enc.EncodeStateVector(doc);

            var diffed = enc.DiffUpdate(update, fullSv);
            var decoded = enc.DecodeUpdate(diffed);

            Assert.AreEqual(0, decoded.Structs.Count);
        }
    }

    [TestMethod]
    public void DiffUpdate_MatchesMergeThenMetaEquivalence()
    {
        foreach (var enc in Encoders)
        {
            var (_, updates) = CaptureUpdates(enc, array =>
            {
                array.Insert(0, new List<object> { 1 });
                array.Insert(0, new List<object> { 2 });
                array.Insert(0, new List<object> { 3 });
                array.Insert(0, new List<object> { 4 });
            });

            var mergedAll = enc.MergeUpdates(updates);

            for (var j = 1; j < updates.Count; j++)
            {
                var partMerged = enc.MergeUpdates(updates.Skip(j).ToList());
                var partMeta = enc.ParseUpdateMeta(partMerged);

                var targetSv = enc.EncodeStateVectorFromUpdate(enc.MergeUpdates(updates.Take(j).ToList()));
                var diffed = enc.DiffUpdate(mergedAll, targetSv);
                var diffedMeta = enc.ParseUpdateMeta(diffed);

                AssertMetaEqual(partMeta, diffedMeta);
            }
        }
    }

    #endregion

    #region EncodeStateVectorFromUpdate / ParseUpdateMeta

    [TestMethod]
    public void EncodeStateVectorFromUpdate_MatchesDocStateVector()
    {
        foreach (var enc in Encoders)
        {
            var doc = new YDoc(new YDocOptions { Gc = false });
            doc.GetArray("array").Insert(0, new List<object> { 1, 2, 3 });
            doc.GetMap("map").Set("a", "b");

            var update = enc.EncodeStateAsUpdate(doc);
            AssertStateVectorsEqual(enc.EncodeStateVector(doc), enc.EncodeStateVectorFromUpdate(update));
        }
    }

    [TestMethod]
    public void ParseUpdateMeta_SingleClient_FromZeroToLength()
    {
        foreach (var enc in Encoders)
        {
            var doc = new YDoc(new YDocOptions { Gc = false });
            doc.GetArray("array").Insert(0, new List<object> { 1, 2, 3, 4, 5 });
            var update = enc.EncodeStateAsUpdate(doc);

            var meta = enc.ParseUpdateMeta(update);

            Assert.AreEqual(1, meta.From.Count);
            Assert.AreEqual(1, meta.To.Count);

            var client = meta.From.Keys.Single();
            Assert.AreEqual(0, meta.From[client]);
            Assert.AreEqual(5, meta.To[client]);
        }
    }

    #endregion

    #region ConvertUpdateFormat

    [TestMethod]
    public void ConvertUpdateFormat_V1ToV2ToApply_MatchesOriginal()
    {
        var doc = new YDoc(new YDocOptions { Gc = false });
        doc.GetArray("array").Insert(0, new List<object> { 1, "two", true });
        doc.GetMap("map").Set("key", "value");

        var v1 = doc.EncodeStateAsUpdateV1();
        var convertedToV2 = UpdateOperations.ConvertUpdateFormatV1ToV2(v1);

        var applied = new YDoc();
        applied.ApplyUpdateV2(convertedToV2);

        CollectionAssert.AreEqual((System.Collections.ICollection)doc.GetArray("array").ToArray(), (System.Collections.ICollection)applied.GetArray("array").ToArray());
        Assert.AreEqual("value", applied.GetMap("map").Get("key"));
    }

    [TestMethod]
    public void ConvertUpdateFormat_V2ToV1ToApply_MatchesOriginal()
    {
        var doc = new YDoc(new YDocOptions { Gc = false });
        doc.GetArray("array").Insert(0, new List<object> { 1, "two", true });

        var v2 = doc.EncodeStateAsUpdateV2();
        var convertedToV1 = UpdateOperations.ConvertUpdateFormatV2ToV1(v2);

        var applied = new YDoc();
        applied.ApplyUpdateV1(convertedToV1);

        CollectionAssert.AreEqual((System.Collections.ICollection)doc.GetArray("array").ToArray(), (System.Collections.ICollection)applied.GetArray("array").ToArray());
    }

    [TestMethod]
    public void ConvertUpdateFormat_RoundTrip_IsByteIdentical()
    {
        var doc = new YDoc(new YDocOptions { Gc = false });
        doc.GetArray("array").Insert(0, new List<object> { 1, 2, 3 });

        var v1 = doc.EncodeStateAsUpdateV1();
        var v2 = UpdateOperations.ConvertUpdateFormatV1ToV2(v1);
        var backToV1 = UpdateOperations.ConvertUpdateFormatV2ToV1(v2);

        CollectionAssert.AreEqual(v1, backToV1);
    }

    #endregion

    #region DecodeUpdate / Skip

    [TestMethod]
    public void DecodeUpdate_StructAndDeleteSetCounts()
    {
        foreach (var enc in Encoders)
        {
            var doc = new YDoc(new YDocOptions { Gc = false });
            var array = doc.GetArray("array");
            array.Insert(0, new List<object> { 1, 2, 3 });
            array.Delete(1, 1);

            var update = enc.EncodeStateAsUpdate(doc);
            var decoded = enc.DecodeUpdate(update);

            Assert.IsTrue(decoded.Structs.Count > 0);
            Assert.AreEqual(1, decoded.DeleteSet.Count);

            var client = decoded.DeleteSet.Keys.Single();
            Assert.AreEqual(1, decoded.DeleteSet[client].Count);
            Assert.AreEqual(1, decoded.DeleteSet[client][0].Length);
        }
    }

    [TestMethod]
    public void DecodeUpdate_MergeWithGap_ProducesSkipStruct()
    {
        // Merging updates[0] and updates[2] out of a 4-op sequence from the same client
        // leaves a genuine gap - the merged update should contain a 'Skip' struct for it,
        // exactly like Yjs' own mergeUpdatesV2 does.
        foreach (var enc in Encoders)
        {
            var (_, updates) = CaptureUpdates(enc, array =>
            {
                array.Insert(0, new List<object> { 1 });
                array.Insert(0, new List<object> { 2 });
                array.Insert(0, new List<object> { 3 });
                array.Insert(0, new List<object> { 4 });
            });

            var gapped = enc.MergeUpdates(new[] { updates[0], updates[2] });
            var decoded = enc.DecodeUpdate(gapped);

            Assert.IsTrue(decoded.Structs.Any(s => s.Kind == ItemKind.Skip), "Expected a Skip struct for the missing middle op.");
        }
    }

    #endregion

    #region Describe / log

    [TestMethod]
    public void DescribeUpdate_ReturnsNonEmptyString_AndDoesNotThrow()
    {
        var doc = new YDoc(new YDocOptions { Gc = false });
        var array = doc.GetArray("array");
        array.Insert(0, new List<object> { 1, 2, 3 });
        array.Delete(1, 1);

        var v1 = doc.EncodeStateAsUpdateV1();
        var v2 = doc.EncodeStateAsUpdateV2();

        var describedV1 = UpdateOperations.DescribeUpdate(v1);
        var describedV2 = UpdateOperations.DescribeUpdateV2(v2);

        Assert.IsFalse(string.IsNullOrWhiteSpace(describedV1));
        Assert.IsFalse(string.IsNullOrWhiteSpace(describedV2));
        StringAssert.Contains(describedV1, "Structs");
        StringAssert.Contains(describedV1, "DeleteSet");

        // Just make sure the Console-writing wrappers don't throw.
        UpdateOperations.LogUpdate(v1);
        UpdateOperations.LogUpdateV2(v2);
    }

    #endregion

    #region Helpers

    private static byte[] EncodeEmptyStateVector()
    {
        var ms = new Lib0Writer();
        ms.WriteVarUint(0);
        return ms.ToArray();
    }

    private static void AssertStateVectorsEqual(byte[] a, byte[] b)
    {
        var svA = EncodingUtils.DecodeStateVector(new Lib0Reader(a));
        var svB = EncodingUtils.DecodeStateVector(new Lib0Reader(b));

        Assert.AreEqual(svA.Count, svB.Count, "State vectors have a different number of clients.");
        foreach (var kvp in svA)
        {
            Assert.IsTrue(svB.TryGetValue(kvp.Key, out var clock), $"State vector B is missing client {kvp.Key}.");
            Assert.AreEqual(kvp.Value, clock, $"Clock mismatch for client {kvp.Key}.");
        }
    }

    private static void AssertMetaEqual(UpdateMeta a, UpdateMeta b)
    {
        Assert.AreEqual(a.From.Count, b.From.Count);
        foreach (var kvp in a.From)
        {
            Assert.IsTrue(b.From.TryGetValue(kvp.Key, out var v));
            Assert.AreEqual(kvp.Value, v);
        }

        Assert.AreEqual(a.To.Count, b.To.Count);
        foreach (var kvp in a.To)
        {
            Assert.IsTrue(b.To.TryGetValue(kvp.Key, out var v));
            Assert.AreEqual(kvp.Value, v);
        }
    }

    #endregion

    [TestMethod]
    public void ConvertUpdateFormatV1ToV2_OverlappingDeleteRanges_AppliesAsTheV1Update()
    {
        // The property engine's convert-v1-to-v2-overlapping-delete-set: client 11's ranges [0, 3] and [0, 2] overlap, which
        // is valid v1; v2 needs each range at or after the end of the one before, so the conversion merges them.
        var v1 = Convert.FromBase64String("AgGsAgIEAKwCAQN4w6kC8KIECoesAgEGx/CiBAqsAgEDA2RpdgILAgADAAKsAgEBAg==");
        var fromV1 = new YDoc(new YDocOptions { Gc = false });
        fromV1.ApplyUpdateV1(v1);
        var fromV2 = new YDoc(new YDocOptions { Gc = false });

        fromV2.ApplyUpdateV2(UpdateOperations.ConvertUpdateFormatV1ToV2(v1));

        Assert.AreEqual(Convert.ToBase64String(fromV1.EncodeStateAsUpdateV1()), Convert.ToBase64String(fromV2.EncodeStateAsUpdateV1()));
    }

    private static readonly Lazy<JsonDocument> MergeConflicts = new(() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "merge-conflicts.json"))));

    public static IEnumerable<object[]> MergeConflictCases =>
        Enumerable.Range(0, MergeConflicts.Value.RootElement.GetProperty("cases").GetArrayLength()).Select(i => new object[] { i });

    /// <summary>
    /// Updates that hold a GC struct and an item for one clock range, in every order (Fixtures/merge-conflicts.json,
    /// recorded from Yjs 13.6.32 by interop/js/merge-conflicts.mjs): the merge keeps the struct Yjs keeps, in v1 and v2.
    /// </summary>
    [TestMethod]
    [DynamicData(nameof(MergeConflictCases))]
    public void MergeUpdates_GcStructAndItemForOneClock_KeepsTheStructYjsKeeps(int index)
    {
        var c = MergeConflicts.Value.RootElement.GetProperty("cases")[index];
        var v1 = c.GetProperty("updates").EnumerateArray().Select(u => Convert.FromHexString(u.GetString())).ToList();
        var v2 = v1.Select(u => UpdateOperations.ConvertUpdateFormatV1ToV2(u)).ToList();

        var merged = (Hex(UpdateOperations.MergeUpdates(v1)), Hex(UpdateOperations.MergeUpdatesV2(v2)));

        Assert.AreEqual((c.GetProperty("merge").GetString(), c.GetProperty("mergeV2").GetString()), merged, $"{c.GetProperty("name")} {c.GetProperty("order")}");
    }

    /// <summary>
    /// The same updates applied to a document that lacks what they need: the state it encodes, of which the pending
    /// structs are part, is Yjs', which merges every new remainder into what waits.
    /// </summary>
    [TestMethod]
    [DynamicData(nameof(MergeConflictCases))]
    public void ApplyUpdate_GcStructAndItemForOneClockPending_EncodesTheStructYjsKeeps(int index)
    {
        var c = MergeConflicts.Value.RootElement.GetProperty("cases")[index];
        var v1 = c.GetProperty("updates").EnumerateArray().Select(u => Convert.FromHexString(u.GetString())).ToList();
        var fromV1 = new YDoc(new YDocOptions { ClientId = 9, Gc = false });
        var fromV2 = new YDoc(new YDocOptions { ClientId = 9, Gc = false });

        foreach (var u in v1)
        {
            fromV1.ApplyUpdateV1(u);
            fromV2.ApplyUpdateV2(UpdateOperations.ConvertUpdateFormatV1ToV2(u));
        }

        Assert.AreEqual((c.GetProperty("pending").GetString(), c.GetProperty("pendingV2").GetString()), (Hex(fromV1.EncodeStateAsUpdateV1()), Hex(fromV2.EncodeStateAsUpdateV2())), $"{c.GetProperty("name")} {c.GetProperty("order")}");
    }

    private static readonly Lazy<JsonDocument> PendingOrders = new(() =>
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pending-orders.json.gz"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return JsonDocument.Parse(gzip);
    });

    public static IEnumerable<object[]> PendingOrderSets =>
        PendingOrders.Value.RootElement.GetProperty("sets").EnumerateArray().Select(s => new object[] { s.GetProperty("name").GetString() });

    /// <summary>
    /// Sets of 3 to 5 updates that wait for a struct that never arrives and overlap by clock, applied in every order
    /// (Fixtures/pending-orders.json.gz, recorded from Yjs 13.6.32 by interop/js/pending-orders.mjs): the state, of
    /// which the pending structs are part, is Yjs', which merges every remainder into what waits, in v1 and v2.
    /// </summary>
    [TestMethod]
    [DynamicData(nameof(PendingOrderSets))]
    public void ApplyUpdate_OverlappingPendingInEveryOrder_EncodesWhatYjsEncodes(string name)
    {
        var set = PendingOrders.Value.RootElement.GetProperty("sets").EnumerateArray().Single(s => s.GetProperty("name").GetString() == name);
        var updates = set.GetProperty("updates").EnumerateArray().Select(u => Convert.FromHexString(u.GetString())).ToList();
        var orders = set.GetProperty("orders").EnumerateArray().ToList();

        var actual = orders.Select(o =>
        {
            var order = o.GetProperty("order").GetString().Select(c => updates[c - '0']).ToList();
            var fromV1 = new YDoc(new YDocOptions { ClientId = 9, Gc = false });
            var fromV2 = new YDoc(new YDocOptions { ClientId = 9, Gc = false });
            foreach (var u in order)
            {
                fromV1.ApplyUpdateV1(u);
                fromV2.ApplyUpdateV2(UpdateOperations.ConvertUpdateFormatV1ToV2(u));
            }

            return $"{o.GetProperty("order").GetString()} {Hex(fromV1.EncodeStateAsUpdateV1())} {Hex(fromV2.EncodeStateAsUpdateV2())}";
        });

        var expected = orders.Select(o => $"{o.GetProperty("order").GetString()} {o.GetProperty("pending").GetString()} {o.GetProperty("pendingV2").GetString()}");
        Assert.AreEqual(string.Join("\n", expected), string.Join("\n", actual));
    }

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
