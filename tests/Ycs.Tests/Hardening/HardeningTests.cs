using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Regression tests for malformed and hostile updates (the security review of the decoders): each
/// must be rejected with <see cref="YUpdateException"/>, quickly and without a large allocation, and
/// leave the document as it was.
/// </summary>
[TestClass]
public class HardeningTests
{
    public TestContext TestContext { get; set; } = null!;

    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", string.Empty, StringComparison.Ordinal));

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] Repeat(string hex, int count) => Enumerable.Repeat(Hex(hex), count).SelectMany(p => p).ToArray();

    /// <summary>Text of the given roots, state vector and v1 encoding.</summary>
    private static string StateOf(YDoc doc, params string[] texts) =>
        string.Join("|", texts.Select(t => doc.GetText(t).ToString()))
        + "|" + Convert.ToHexString(doc.EncodeStateVector())
        + "|" + Convert.ToHexString(doc.EncodeStateAsUpdate());

    private static void AssertRejected(YDoc doc, Action apply, params string[] texts)
    {
        var before = StateOf(doc, texts);
        Assert.ThrowsExactly<YUpdateException>(apply);
        Assert.AreEqual(before, StateOf(doc, texts));
    }

    private static long AllocatedBy(Action action)
    {
        var before = System.GC.GetAllocatedBytesForCurrentThread();
        try
        {
            action();
        }
        catch (YUpdateException)
        {
            // Expected for the hostile inputs measured here.
        }

        return System.GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>Client 1 inserts "a" into root text "" at clock 0.</summary>
    private static readonly byte[] ValidA = Hex("01 01 01 00 04 01 00 01 61 00");

    // 1. A clock gap beyond 2^31 made MergeUpdates' Skip length negative and looped forever.

    /// <summary>Client 1 at clock 1, after (1, 0): waits for clock 0.</summary>
    private static readonly byte[] TextA = Hex("01 01 01 01 84 01 00 01 61 00");
    private static readonly byte[] FarClock = Convert.FromBase64String("AQEB/////wuEAQoBcgA=");

    [TestMethod]
    public void ApplyUpdate_ClockBeyondRange_IsRejected()
    {
        var doc = new YDoc();
        doc.ApplyUpdate(TextA);
        AssertRejected(doc, () => doc.ApplyUpdate(FarClock), "");
    }

    [TestMethod]
    public void MergeUpdates_ClockBeyondRange_IsRejected()
    {
        Assert.ThrowsExactly<YUpdateException>(() => YUpdate.Merge([TextA, FarClock], format: UpdateFormat.V1));
    }

    // 10. Three GC structs of length 2^31 - 1 overflowed the merged length.

    [TestMethod]
    public void MergeUpdates_GcStructsBeyondClockRange_AreRejected()
    {
        // 1 client, 3 structs, client 1, clock 0, then three GC structs of length 2^31 - 1.
        var update = Concat(Hex("01 03 01 00"), Repeat("00 FF FF FF FF 07", 3), Hex("00"));
        Assert.ThrowsExactly<YUpdateException>(() => YUpdate.Merge([update, ValidA], format: UpdateFormat.V1));
        Assert.ThrowsExactly<YUpdateException>(() => new YDoc().ApplyUpdate(update));
    }

    // 2. Unbounded recursion in ReadAny: an uncatchable stack overflow.

    [TestMethod]
    public void ApplyUpdate_DeeplyNestedAnyValue_IsRejected()
    {
        var update = Concat(Hex("01 01 05 00 08 01 01 61 01"), Repeat("75 01", 40000), Hex("7E 00"));
        AssertRejected(new YDoc(), () => new YDoc().ApplyUpdate(update));
    }

    [TestMethod]
    public void Set_ValueNestedBeyondLimit_Throws()
    {
        object value = 1;
        for (var i = 0; i <= UpdateLimits.Default.MaxDepth; i++)
        {
            value = new List<object> { value };
        }

        var map = new YDoc().GetMap("m");
        Assert.ThrowsExactly<ArgumentException>(() => map["k"] = YValue.FromObject(value));
    }

    [TestMethod]
    public void Set_ValueThatContainsItself_Throws()
    {
        var list = new List<object>();
        list.Add(list);
        var map = new YDoc().GetMap("m");
        Assert.ThrowsExactly<ArgumentException>(() => map["k"] = YValue.FromObject(list));
    }

    // 3. Deleting (and garbage-collecting) deeply nested types recursed once per level.

    /// <summary>A v1 update of client 5: <paramref name="depth"/> arrays, each nested in the one before, the first in root 'a'.</summary>
    private static byte[] NestedArrays(int depth)
    {
        var writer = new Lib0Writer();
        writer.WriteVarUint(1);
        writer.WriteVarUint((ulong)depth);
        writer.WriteVarUint(5);
        writer.WriteVarUint(0);
        for (var i = 0; i < depth; i++)
        {
            writer.WriteByte(7); // ContentType, no origins
            if (i == 0)
            {
                writer.WriteVarUint(1);
                writer.WriteVarString("a");
            }
            else
            {
                writer.WriteVarUint(0);
                writer.WriteVarUint(5);
                writer.WriteVarUint((ulong)(i - 1));
            }

            writer.WriteVarUint(YArray.YArrayRefId);
        }

        writer.WriteVarUint(0);
        return writer.ToArray();
    }

    private static readonly byte[] DeleteFirstOfClient5 = Hex("00 01 05 01 00 01");

    [TestMethod]
    public void ApplyUpdate_TypesNestedBeyondLimit_AreRejected()
    {
        var doc = new YDoc();
        AssertRejected(doc, () => doc.ApplyUpdate(NestedArrays(UpdateLimits.Default.MaxDepth + 1)));
        doc.ApplyUpdate(DeleteFirstOfClient5);
    }

    [TestMethod]
    public void ApplyUpdate_TypesNestedUpToLimit_DeleteAndCollect()
    {
        var doc = new YDoc();
        doc.ApplyUpdate(NestedArrays(UpdateLimits.Default.MaxDepth));
        doc.ApplyUpdate(DeleteFirstOfClient5);
        Assert.AreEqual(0, doc.GetArray("a").Length);
    }

    [TestMethod]
    public void Insert_TypeNestedBeyondLimit_Throws()
    {
        var doc = new YDoc();
        var array = doc.GetArray("a");
        for (var i = 0; i < UpdateLimits.Default.MaxDepth; i++)
        {
            var inner = new YArray();
            array.Insert(0, inner);
            array = inner;
        }

        Assert.ThrowsExactly<InvalidOperationException>(() => array.Insert(0, new YArray()));
        Assert.ThrowsExactly<InvalidOperationException>(() => array.Insert(0, new YMap()));
    }

    // 4. Structs of length 0 broke every later encoding, or the transaction state.

    [TestMethod]
    public void ApplyUpdate_ZeroLengthGc_IsRejected()
    {
        var doc = new YDoc();
        AssertRejected(doc, () => doc.ApplyUpdate(Hex("01 01 05 00 00 00 00")));
        doc.EncodeStateAsUpdate();
    }

    [TestMethod]
    public void ApplyUpdate_ZeroLengthDeleted_IsRejectedAndUpdatesStillFire()
    {
        var doc = new YDoc();
        var updates = 0;
        doc.Updated += (_, _) => updates++;

        AssertRejected(doc, () => doc.ApplyUpdate(Hex("01 01 05 00 01 01 01 74 00 00")), "t");
        doc.ApplyUpdate(ValidA);

        Assert.AreEqual(1, updates);
    }

    [TestMethod]
    public void ApplyUpdate_ObserverThrows_DocumentKeepsWorking()
    {
        var doc = new YDoc();
        var updates = 0;
        var fail = true;
        doc.Updated += (_, _) => updates++;
        doc.GetText("").Observe(_ =>
        {
            if (fail)
            {
                throw new InvalidOperationException("observer");
            }
        });

        Assert.ThrowsExactly<InvalidOperationException>(() => doc.ApplyUpdate(ValidA));
        fail = false;
        doc.GetText("").Insert(1, "b");

        Assert.AreEqual("ab", doc.GetText("").ToString());
        Assert.AreEqual(2, updates);
        Assert.IsNull(doc._transaction);
        Assert.AreEqual(0, doc._transactionCleanups.Count);
    }

    // 5. Capacities taken from the wire.

    [TestMethod]
    [DataRow("01 80 80 80 80 02")] // struct count
    [DataRow("01 80 80 80 08")] // 2^24 structs
    [DataRow("01 01 00 00 48 00 00 FF FF FF 78")] // ContentAny length
    [DataRow("00 FF FF FF FF 0F")] // delete set clients
    [DataRow("00 01 01 FF FF FF FF 0F")] // delete set ranges
    public void ApplyUpdate_HugeDecodedCount_AllocatesLittle(string hex)
    {
        var update = Hex(hex);
        Assert.IsLessThan(1L << 20, AllocatedBy(() => new YDoc().ApplyUpdate(update)));
    }

    [TestMethod]
    public void EncodeStateAsUpdate_HugeStateVectorCount_AllocatesLittle()
    {
        var doc = new YDoc();
        var sv = Hex("FF FF FF FF 0F 01");
        Assert.IsLessThan(1L << 20, AllocatedBy(() => doc.EncodeStateAsUpdate(sv)));
        Assert.ThrowsExactly<YUpdateException>(() => doc.EncodeStateAsUpdate(sv));
    }

    [TestMethod]
    public void ReadSyncMessage_HugeLengthShortStream_AllocatesLittle()
    {
        var doc = new YDoc();
        var message = Hex("02 FF FF FF FF 0F 00 00");
        Assert.IsLessThan(1L << 20, AllocatedBy(() => SyncProtocol.ReadSyncMessage(message, new System.Buffers.ArrayBufferWriter<byte>(), doc, null)));
        Assert.ThrowsExactly<YUpdateException>(() => SyncProtocol.ReadSyncMessage(message, new System.Buffers.ArrayBufferWriter<byte>(), doc, null));
    }

    [TestMethod]
    public void ReadSyncMessage_LargeUpdate_Applies()
    {
        var source = new YDoc();
        source.GetText("t").Insert(0, new string('x', 200_000));
        var message = new System.Buffers.ArrayBufferWriter<byte>();
        SyncProtocol.WriteUpdate(message, source.EncodeStateAsUpdate());

        var doc = new YDoc();
        SyncProtocol.ReadSyncMessage(message.WrittenSpan, new System.Buffers.ArrayBufferWriter<byte>(), doc, null);

        Assert.AreEqual(200_000, doc.GetText("t").Length);
    }

    // 6. Applying an update inside a local transaction left stale search markers.

    [TestMethod]
    public void ApplyUpdate_InsideLocalTransaction_SearchMarkersAreReset()
    {
        var a = new YDoc(new YDocOptions { ClientId = 1 });
        var b = new YDoc(new YDocOptions { ClientId = 2 });
        var textA = a.GetText("t");
        for (var i = 0; i < 300; i++)
        {
            // Separate items (each inserted before the last), so that index lookups use search markers.
            textA.Insert(0, i % 2 == 0 ? "x" : "y");
        }

        b.ApplyUpdate(a.EncodeStateAsUpdate());
        textA.Insert(250, "-");
        textA.Remove(250, 1);
        b.GetText("t").Insert(0, "12345");
        var remote = b.EncodeStateAsUpdate(a.EncodeStateVector());

        a.Transact(_ => a.ApplyUpdate(remote));
        textA.Insert(200, "Z");

        Assert.AreEqual(200, textA.ToString().IndexOf('Z', StringComparison.Ordinal));
    }

    [TestMethod]
    public void ApplyUpdate_InsideLocalTransaction_TransactionIsNotLocal()
    {
        var doc = new YDoc();
        var local = true;
        doc.Transact(tr =>
        {
            doc.ApplyUpdate(ValidA);
            local = tr.Local;
        });

        Assert.IsFalse(local);
    }

    // 7. Pending data: quadratic, and unbounded.

    /// <summary>A v1 update of client <paramref name="client"/> at clock 0: one character after an item of client 99, which never arrives.</summary>
    private static byte[] WaitingForClient99(int client)
    {
        var writer = new Lib0Writer();
        writer.WriteVarUint(1);
        writer.WriteVarUint(1);
        writer.WriteVarUint((ulong)client);
        writer.WriteVarUint(0);
        writer.WriteByte(0x84); // string, left origin
        writer.WriteVarUint(99);
        writer.WriteVarUint(0);
        writer.WriteVarString("c");
        writer.WriteVarUint(0);
        return writer.ToArray();
    }

    [TestMethod]
    public void ApplyUpdate_ManyUpdatesWaitingForAMissingClient_IsFast()
    {
        ScalingProbe.AssertNotQuadratic(
            n =>
            {
                var updates = Enumerable.Range(100, n).Select(WaitingForClient99).ToList();
                var doc = new YDoc();
                return () => updates.ForEach(update => doc.ApplyUpdate(update));
            },
            8_000,
            TestContext);
    }

    /// <summary>A v1 update of <paramref name="clients"/> clients (ids 1 to n), each one character after the item of the client below it: the walk stacks all of them and hits the wall at client 0.</summary>
    private static byte[] ChainOfWaitingClients(int clients)
    {
        var writer = new Lib0Writer();
        writer.WriteVarUint((ulong)clients);
        for (var client = clients; client >= 1; client--)
        {
            writer.WriteVarUint(1);
            writer.WriteVarUint((ulong)client);
            writer.WriteVarUint(0);
            writer.WriteByte(0x84); // string, left origin
            writer.WriteVarUint((ulong)(client - 1));
            writer.WriteVarUint(0);
            writer.WriteVarString("c");
        }

        writer.WriteVarUint(0);
        return writer.ToArray();
    }

    [TestMethod]
    public void ApplyUpdate_ManyClientsHittingTheWallAtOnce_IsLinear()
    {
        ScalingProbe.AssertNotQuadratic(
            n =>
            {
                var update = ChainOfWaitingClients(n);
                var doc = new YDoc(new YDocOptions { Limits = new UpdateLimits { MaxStructsPerUpdate = 1_000_000, MaxPendingStructs = 1_000_000, MaxPendingBytes = 1 << 30 } });
                return () => doc.ApplyUpdate(update);
            },
            400_000,
            TestContext);
    }

    [TestMethod]
    public void ApplyUpdate_PendingBeyondLimit_IsRejected()
    {
        var doc = new YDoc(new YDocOptions { Limits = new UpdateLimits { MaxPendingBytes = 1024 } });
        var client = 100;
        while (true)
        {
            var update = WaitingForClient99(client++);
            var before = StateOf(doc);
            if (!doc.TryApplyUpdate(update))
            {
                Assert.AreEqual(before, StateOf(doc));
                break;
            }

            Assert.IsLessThan(10_000, client, "the limit never applied");
        }

        Assert.IsLessThanOrEqualTo(1024L, doc.Store.PendingBytes);
    }

    [TestMethod]
    public void ApplyUpdate_SamePendingDeleteManyTimes_StaysSmall()
    {
        var doc = new YDoc(new YDocOptions { Limits = new UpdateLimits { MaxPendingBytes = 256 } });
        var delete = Hex("00 01 09 01 00 01");
        for (var i = 0; i < 8000; i++)
        {
            doc.ApplyUpdate(delete);
        }

        Assert.IsLessThanOrEqualTo(256L, doc.Store.PendingBytes);
    }

    [TestMethod]
    public void ApplyUpdate_PendingDeleteThenItsStruct_Deletes()
    {
        var doc = new YDoc();
        doc.ApplyUpdate(Hex("00 01 01 01 00 01"));
        doc.ApplyUpdate(ValidA);

        Assert.AreEqual(string.Empty, doc.GetText("").ToString());
        Assert.AreEqual(0, doc.Store.PendingDs.Count);
    }

    [TestMethod]
    public void ApplyUpdate_RetryAfterManyPendingUpdates_IntegratesThemAll()
    {
        var source = new YDoc(new YDocOptions { ClientId = 7 });
        var updates = new List<byte[]>();
        source.Updated += (_, e) => updates.Add(e.Update.ToArray());
        var text = source.GetText("t");
        for (var i = 0; i < 500; i++)
        {
            text.Insert(i / 2, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var doc = new YDoc();
        for (var i = updates.Count - 1; i >= 0; i--)
        {
            doc.ApplyUpdate(updates[i]);
        }

        Assert.AreEqual(text.ToString(), doc.GetText("t").ToString());
        Assert.IsNull(doc.Store.Pending);
    }

    // 8. Atomicity, and one exception type.

    [TestMethod]
    public void ApplyUpdate_TruncatedDeleteSet_IntegratesNothing()
    {
        var doc = new YDoc();
        var update = ValidA[..^1].Concat(Hex("01 01")).ToArray();
        AssertRejected(doc, () => doc.ApplyUpdate(update), "");
    }

    [TestMethod]
    public void ApplyUpdate_OriginOfOwnClientAtLaterClock_IsRejected()
    {
        // Client 1: 'a' at clock 0, then 'b' at clock 1 whose left origin is (1, 5).
        var doc = new YDoc();
        AssertRejected(doc, () => doc.ApplyUpdate(Hex("01 02 01 00 04 01 00 01 61 84 01 05 01 62 00")), "");
    }

    [TestMethod]
    public void ApplyUpdate_ClientListedTwice_IsRejected()
    {
        var doc = new YDoc();
        AssertRejected(doc, () => doc.ApplyUpdate(Hex("02 01 01 00 04 01 00 01 61 01 01 00 04 01 00 01 62 00")), "");
    }

    [TestMethod]
    public void TryApplyUpdate_Malformed_ReturnsFalse()
    {
        var doc = new YDoc();
        Assert.IsFalse(doc.TryApplyUpdate(ValidA.AsSpan(..^2)));
        Assert.IsFalse(doc.TryApplyUpdate(ValidA, format: UpdateFormat.V2));
        Assert.IsTrue(doc.TryApplyUpdate(ValidA));
        Assert.AreEqual("a", doc.GetText("").ToString());
    }

    [TestMethod]
    public void UpdateOperations_Malformed_ThrowYUpdateException()
    {
        var bad = ValidA[..^2];
        Assert.ThrowsExactly<YUpdateException>(() => YUpdate.Merge([bad, ValidA], format: UpdateFormat.V1));
        Assert.ThrowsExactly<YUpdateException>(() => YUpdate.Diff(bad, new byte[] { 0 }, format: UpdateFormat.V1));
        Assert.ThrowsExactly<YUpdateException>(() => YUpdate.Diff(ValidA, Hex("01 01"), format: UpdateFormat.V1));
        Assert.ThrowsExactly<YUpdateException>(() => YUpdate.EncodeStateVector(bad, format: UpdateFormat.V1));
        Assert.ThrowsExactly<YUpdateException>(() => YUpdate.ParseMeta(bad, format: UpdateFormat.V1));
        Assert.ThrowsExactly<YUpdateException>(() => YUpdate.Decode(bad, format: UpdateFormat.V1));
        Assert.ThrowsExactly<YUpdateException>(() => YUpdate.Convert(bad, UpdateFormat.V1, UpdateFormat.V2));
        Assert.ThrowsExactly<YUpdateException>(() => YUpdate.Merge([ValidA, ValidA], format: UpdateFormat.V2));
        Assert.ThrowsExactly<YUpdateException>(() => Snapshot.Decode(Hex("01 01 01")));
        Assert.ThrowsExactly<YUpdateException>(() => RelativePosition.Decode(Hex("07")));
    }

    // 9. A subdocument's options of the wrong type.

    /// <summary>A v1 update: a subdocument with guid "g" and the given options ('any' encoded) in root map 'm' under key 'k'.</summary>
    private static byte[] Subdocument(string optionsHex) =>
        Concat(Hex("01 01 05 00 29 01 01 6D 01 6B 01 67"), Hex(optionsHex), Hex("00"));

    // Yjs spreads the options into an object: only null throws there, and options are only tested.
    [TestMethod]
    [DataRow("7E")] // null
    [DataRow("76 01 04 67 75 69 64 7D 01")] // guid: 1
    public void ApplyUpdate_SubdocumentOptionsOfWrongType_AreRejected(string options)
    {
        var doc = new YDoc();
        AssertRejected(doc, () => doc.ApplyUpdate(Subdocument(options)));
    }

    [TestMethod]
    public void ApplyUpdate_SubdocumentOptionsWithMeta_ReadsMeta()
    {
        var doc = new YDoc();
        doc.ApplyUpdate(Subdocument("76 02 02 67 63 79 04 6D 65 74 61 76 01 01 61 7D 01"));
        var subdoc = (YDoc)doc.GetMap("m")["k"].ToObject();

        Assert.IsFalse(subdoc.GarbageCollection);
        Assert.AreEqual(1, subdoc.Meta["a"]);
    }

    [TestMethod]
    public void EncodeStateAsUpdate_DeletedSubdocumentWithoutGc_Encodes()
    {
        var doc = new YDoc(new YDocOptions { GarbageCollection = false });
        var map = doc.GetMap("m");
        var subdoc = new YDoc();
        map["k"] = subdoc;
        map.Remove("k");

        var copy = new YDoc(new YDocOptions { GarbageCollection = false });
        copy.ApplyUpdate(doc.EncodeStateAsUpdate());

        Assert.IsFalse(copy.GetMap("m").ContainsKey("k"));
    }

    // 11. v2 column decoders: values beyond their range.

    [TestMethod]
    public void UintOptRleDecoder_ValueBeyond32Bits_Throws()
    {
        var writer = new Lib0Writer();
        writer.WriteVarInt(1L << 40);
        Assert.ThrowsExactly<YUpdateException>(() => new UintOptRleDecoder(new Lib0Reader(writer.ToArray())).Read());
    }

    [TestMethod]
    public void UintOptRleDecoder_LongestRun_DoesNotWrap()
    {
        var writer = new Lib0Writer();
        writer.WriteVarInt(-3);
        writer.WriteVarUint(uint.MaxValue);
        var decoder = new UintOptRleDecoder(new Lib0Reader(writer.ToArray()));
        for (var i = 0; i < 10; i++)
        {
            Assert.AreEqual(3u, decoder.Read());
        }
    }

    [TestMethod]
    public void IntDiffOptRleDecoder_StateBeyondSafeIntegers_Throws()
    {
        var writer = new Lib0Writer();
        writer.WriteVarInt(((1L << 51) << 1) | 1); // diff 2^51, with a count
        writer.WriteVarUint(10);
        var decoder = new IntDiffOptRleDecoder(new Lib0Reader(writer.ToArray()));
        decoder.Read();
        decoder.Read();
        decoder.Read();
        Assert.ThrowsExactly<YUpdateException>(() => decoder.Read());
    }

    [TestMethod]
    public void ReadKey_NegativeIndex_Throws()
    {
        var keyClock = new Lib0Writer();
        keyClock.WriteVarInt(-2); // diff -1, no count
        var writer = new Lib0Writer();
        writer.WriteByte(0);
        writer.WriteVarUint8Array(keyClock.ToArray());
        for (var i = 0; i < 8; i++)
        {
            // The string column holds at least the (empty) concatenated string.
            writer.WriteVarUint8Array(i == 4 ? new byte[] { 0 } : ReadOnlySpan<byte>.Empty);
        }

        var decoder = new UpdateDecoderV2(new Lib0Reader(writer.ToArray()), long.MaxValue);
        Assert.ThrowsExactly<YUpdateException>(() => decoder.ReadKey());
    }

    [TestMethod]
    public void ApplyUpdateV2_RunOfManyStructs_IsRejected()
    {
        // A v2 update whose columns describe 2^32 - 1 GC structs in a few bytes.
        var lengths = new Lib0Writer();
        lengths.WriteVarInt(-1);
        lengths.WriteVarUint(uint.MaxValue - 2);
        var info = new Lib0Writer();
        info.WriteByte(0);
        var client = new Lib0Writer();
        client.WriteVarInt(1);
        var writer = new Lib0Writer();
        writer.WriteByte(0);
        writer.WriteVarUint8Array(ReadOnlySpan<byte>.Empty); // key clocks
        writer.WriteVarUint8Array(client.ToArray());
        writer.WriteVarUint8Array(ReadOnlySpan<byte>.Empty); // left clocks
        writer.WriteVarUint8Array(ReadOnlySpan<byte>.Empty); // right clocks
        writer.WriteVarUint8Array(info.ToArray());
        writer.WriteVarUint8Array(new byte[] { 0 }); // strings: an empty string column
        writer.WriteVarUint8Array(ReadOnlySpan<byte>.Empty); // parent info
        writer.WriteVarUint8Array(ReadOnlySpan<byte>.Empty); // type refs
        writer.WriteVarUint8Array(lengths.ToArray());
        writer.WriteVarUint(1); // one client
        writer.WriteVarUint(uint.MaxValue); // structs
        writer.WriteVarUint(0); // clock
        writer.WriteVarUint(0); // empty delete set
        var update = writer.ToArray();

        var stopwatch = Stopwatch.StartNew();
        Assert.ThrowsExactly<YUpdateException>(() => new YDoc().ApplyUpdate(update, format: UpdateFormat.V2));
        Assert.ThrowsExactly<YUpdateException>(() => YUpdate.Merge([update, update], format: UpdateFormat.V2));
        Assert.IsLessThan(10_000, stopwatch.ElapsedMilliseconds); // ten times the usual: a guard against the walk of 2^32 structs
    }

    /// <summary>A v2 update whose columns describe <paramref name="count"/> (at least 2) GC structs of length 1 in a few bytes.</summary>
    internal static byte[] RunOfGcStructsV2(uint count, int clientId = 1, uint clock = 0)
    {
        var lengths = new Lib0Writer();
        lengths.WriteVarInt(-1);
        lengths.WriteVarUint(count - 2);
        var info = new Lib0Writer();
        info.WriteByte(0);
        var client = new Lib0Writer();
        client.WriteVarInt(clientId);
        var writer = new Lib0Writer();
        writer.WriteByte(0);
        writer.WriteVarUint8Array(ReadOnlySpan<byte>.Empty); // key clocks
        writer.WriteVarUint8Array(client.ToArray());
        writer.WriteVarUint8Array(ReadOnlySpan<byte>.Empty); // left clocks
        writer.WriteVarUint8Array(ReadOnlySpan<byte>.Empty); // right clocks
        writer.WriteVarUint8Array(info.ToArray());
        writer.WriteVarUint8Array(new byte[] { 0 }); // strings: an empty string column
        writer.WriteVarUint8Array(ReadOnlySpan<byte>.Empty); // parent info
        writer.WriteVarUint8Array(ReadOnlySpan<byte>.Empty); // type refs
        writer.WriteVarUint8Array(lengths.ToArray());
        writer.WriteVarUint(1); // one client
        writer.WriteVarUint(count); // structs
        writer.WriteVarUint(clock); // clock
        writer.WriteVarUint(0); // empty delete set
        return writer.ToArray();
    }

    [TestMethod]
    public void ApplyUpdateV2_MoreStructsThanTheLimitWithinClockRange_IsRejectedQuickly()
    {
        var update = RunOfGcStructsV2(5_000_000);
        var doc = new YDoc();

        var stopwatch = Stopwatch.StartNew();
        AssertRejected(doc, () => doc.ApplyUpdate(update, format: UpdateFormat.V2), "");
        Assert.ThrowsExactly<YUpdateException>(() => YUpdate.Merge([update, update], format: UpdateFormat.V2));
        Assert.IsLessThan(10_000, stopwatch.ElapsedMilliseconds); // ten times the usual: a guard against the walk of 2^32 structs
    }
}
