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
        doc.ApplyUpdateV1(TextA);
        AssertRejected(doc, () => doc.ApplyUpdateV1(FarClock), "");
    }

    [TestMethod]
    public void MergeUpdates_ClockBeyondRange_IsRejected()
    {
        Assert.ThrowsExactly<YUpdateException>(() => UpdateOperations.MergeUpdates(new[] { TextA, FarClock }));
    }

    // 10. Three GC structs of length 2^31 - 1 overflowed the merged length.

    [TestMethod]
    public void MergeUpdates_GcStructsBeyondClockRange_AreRejected()
    {
        // 1 client, 3 structs, client 1, clock 0, then three GC structs of length 2^31 - 1.
        var update = Concat(Hex("01 03 01 00"), Repeat("00 FF FF FF FF 07", 3), Hex("00"));
        Assert.ThrowsExactly<YUpdateException>(() => UpdateOperations.MergeUpdates(new[] { update, ValidA }));
        Assert.ThrowsExactly<YUpdateException>(() => new YDoc().ApplyUpdateV1(update));
    }

    // 2. Unbounded recursion in ReadAny: an uncatchable stack overflow.

    [TestMethod]
    public void ApplyUpdate_DeeplyNestedAnyValue_IsRejected()
    {
        var update = Concat(Hex("01 01 05 00 08 01 01 61 01"), Repeat("75 01", 40000), Hex("7E 00"));
        AssertRejected(new YDoc(), () => new YDoc().ApplyUpdateV1(update));
    }

    [TestMethod]
    public void Set_ValueNestedBeyondLimit_Throws()
    {
        object value = 1;
        for (var i = 0; i < 300; i++)
        {
            value = new List<object> { value };
        }

        var map = new YDoc().GetMap("m");
        Assert.ThrowsExactly<ArgumentException>(() => map.Set("k", value));
    }

    [TestMethod]
    public void Set_ValueThatContainsItself_Throws()
    {
        var list = new List<object>();
        list.Add(list);
        var map = new YDoc().GetMap("m");
        Assert.ThrowsExactly<ArgumentException>(() => map.Set("k", list));
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
        AssertRejected(doc, () => doc.ApplyUpdateV1(NestedArrays(20000)));
        doc.ApplyUpdateV1(DeleteFirstOfClient5);
    }

    [TestMethod]
    public void ApplyUpdate_TypesNestedUpToLimit_DeleteAndCollect()
    {
        var doc = new YDoc();
        doc.ApplyUpdateV1(NestedArrays(256));
        doc.ApplyUpdateV1(DeleteFirstOfClient5);
        Assert.AreEqual(0, doc.GetArray("a").Length);
    }

    [TestMethod]
    public void Insert_TypeNestedBeyondLimit_Throws()
    {
        var doc = new YDoc();
        var array = doc.GetArray("a");
        for (var i = 0; i < 256; i++)
        {
            var inner = new YArray();
            array.Insert(0, new object[] { inner });
            array = inner;
        }

        Assert.ThrowsExactly<InvalidOperationException>(() => array.Insert(0, new object[] { new YArray() }));
        Assert.ThrowsExactly<InvalidOperationException>(() => array.Insert(0, new object[] { new YMap() }));
    }

    // 4. Structs of length 0 broke every later encoding, or the transaction state.

    [TestMethod]
    public void ApplyUpdate_ZeroLengthGc_IsRejected()
    {
        var doc = new YDoc();
        AssertRejected(doc, () => doc.ApplyUpdateV1(Hex("01 01 05 00 00 00 00")));
        doc.EncodeStateAsUpdate();
    }

    [TestMethod]
    public void ApplyUpdate_ZeroLengthDeleted_IsRejectedAndUpdatesStillFire()
    {
        var doc = new YDoc();
        var updates = 0;
        doc.Update += (_, _) => updates++;

        AssertRejected(doc, () => doc.ApplyUpdateV1(Hex("01 01 05 00 01 01 01 74 00 00")), "t");
        doc.ApplyUpdateV1(ValidA);

        Assert.AreEqual(1, updates);
    }

    [TestMethod]
    public void ApplyUpdate_ObserverThrows_DocumentKeepsWorking()
    {
        var doc = new YDoc();
        var updates = 0;
        var fail = true;
        doc.Update += (_, _) => updates++;
        doc.GetText("").EventHandler += (_, _) =>
        {
            if (fail)
            {
                throw new InvalidOperationException("observer");
            }
        };

        Assert.ThrowsExactly<InvalidOperationException>(() => doc.ApplyUpdateV1(ValidA));
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
        Assert.IsLessThan(1L << 20, AllocatedBy(() => new YDoc().ApplyUpdateV1(update)));
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
        Assert.IsLessThan(1L << 20, AllocatedBy(() => SyncProtocol.ReadSyncMessage(new MemoryStream(message), new MemoryStream(), doc, null)));
        Assert.ThrowsExactly<YUpdateException>(() => SyncProtocol.ReadSyncMessage(new MemoryStream(message), new MemoryStream(), doc, null));
    }

    [TestMethod]
    public void ReadSyncMessage_LargeUpdate_Applies()
    {
        var source = new YDoc();
        source.GetText("t").Insert(0, new string('x', 200_000));
        var stream = new MemoryStream();
        SyncProtocol.WriteUpdate(stream, source.EncodeStateAsUpdate());
        stream.Position = 0;

        var doc = new YDoc();
        SyncProtocol.ReadSyncMessage(stream, new MemoryStream(), doc, null);

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
        textA.Delete(250, 1);
        b.GetText("t").Insert(0, "12345");
        var remote = b.EncodeStateAsUpdate(a.EncodeStateVector());

        a.Transact(_ => a.ApplyUpdateV1(remote));
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
            doc.ApplyUpdateV1(ValidA);
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
        var doc = new YDoc();
        var stopwatch = Stopwatch.StartNew();
        for (var client = 100; client < 8100; client++)
        {
            doc.ApplyUpdateV1(WaitingForClient99(client));
        }

        Assert.IsLessThan(2000, stopwatch.ElapsedMilliseconds);
    }

    [TestMethod]
    public void ApplyUpdate_PendingBeyondLimit_IsRejected()
    {
        var doc = new YDoc(new YDocOptions { MaxPendingBytes = 1024 });
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
        var doc = new YDoc(new YDocOptions { MaxPendingBytes = 256 });
        var delete = Hex("00 01 09 01 00 01");
        for (var i = 0; i < 8000; i++)
        {
            doc.ApplyUpdateV1(delete);
        }

        Assert.IsLessThanOrEqualTo(256L, doc.Store.PendingBytes);
    }

    [TestMethod]
    public void ApplyUpdate_PendingDeleteThenItsStruct_Deletes()
    {
        var doc = new YDoc();
        doc.ApplyUpdateV1(Hex("00 01 01 01 00 01"));
        doc.ApplyUpdateV1(ValidA);

        Assert.AreEqual(string.Empty, doc.GetText("").ToString());
        Assert.AreEqual(0, doc.Store.PendingDs.Count);
    }

    [TestMethod]
    public void ApplyUpdate_RetryAfterManyPendingUpdates_IntegratesThemAll()
    {
        var source = new YDoc(new YDocOptions { ClientId = 7 });
        var updates = new List<byte[]>();
        source.Update += (_, e) => updates.Add(e.data);
        var text = source.GetText("t");
        for (var i = 0; i < 500; i++)
        {
            text.Insert(i / 2, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var doc = new YDoc();
        for (var i = updates.Count - 1; i >= 0; i--)
        {
            doc.ApplyUpdateV1(updates[i]);
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
        AssertRejected(doc, () => doc.ApplyUpdateV1(update), "");
    }

    [TestMethod]
    public void ApplyUpdate_OriginOfOwnClientAtLaterClock_IsRejected()
    {
        // Client 1: 'a' at clock 0, then 'b' at clock 1 whose left origin is (1, 5).
        var doc = new YDoc();
        AssertRejected(doc, () => doc.ApplyUpdateV1(Hex("01 02 01 00 04 01 00 01 61 84 01 05 01 62 00")), "");
    }

    [TestMethod]
    public void ApplyUpdate_ClientListedTwice_IsRejected()
    {
        var doc = new YDoc();
        AssertRejected(doc, () => doc.ApplyUpdateV1(Hex("02 01 01 00 04 01 00 01 61 01 01 00 04 01 00 01 62 00")), "");
    }

    [TestMethod]
    public void TryApplyUpdate_Malformed_ReturnsFalse()
    {
        var doc = new YDoc();
        Assert.IsFalse(doc.TryApplyUpdate(ValidA[..^2]));
        Assert.IsFalse(doc.TryApplyUpdateV2(ValidA));
        Assert.IsTrue(doc.TryApplyUpdate(ValidA));
        Assert.AreEqual("a", doc.GetText("").ToString());
    }

    [TestMethod]
    public void UpdateOperations_Malformed_ThrowYUpdateException()
    {
        var bad = ValidA[..^2];
        Assert.ThrowsExactly<YUpdateException>(() => UpdateOperations.MergeUpdates(new[] { bad, ValidA }));
        Assert.ThrowsExactly<YUpdateException>(() => UpdateOperations.DiffUpdate(bad, new byte[] { 0 }));
        Assert.ThrowsExactly<YUpdateException>(() => UpdateOperations.DiffUpdate(ValidA, Hex("01 01")));
        Assert.ThrowsExactly<YUpdateException>(() => UpdateOperations.EncodeStateVectorFromUpdate(bad));
        Assert.ThrowsExactly<YUpdateException>(() => UpdateOperations.ParseUpdateMeta(bad));
        Assert.ThrowsExactly<YUpdateException>(() => UpdateOperations.DecodeUpdate(bad));
        Assert.ThrowsExactly<YUpdateException>(() => UpdateOperations.ConvertUpdateFormatV1ToV2(bad));
        Assert.ThrowsExactly<YUpdateException>(() => UpdateOperations.MergeUpdatesV2(new[] { ValidA, ValidA }));
        Assert.ThrowsExactly<YUpdateException>(() => Snapshot.DecodeSnapshot(Hex("01 01 01")));
        Assert.ThrowsExactly<YUpdateException>(() => RelativePosition.Read(Hex("07")));
    }

    // 9. A subdocument's options of the wrong type.

    /// <summary>A v1 update: a subdocument with guid "g" and the given options ('any' encoded) in root map 'm' under key 'k'.</summary>
    private static byte[] Subdocument(string optionsHex) =>
        Concat(Hex("01 01 05 00 29 01 01 6D 01 6B 01 67"), Hex(optionsHex), Hex("00"));

    [TestMethod]
    [DataRow("7E")] // null
    [DataRow("77 01 78")] // a string
    [DataRow("76 01 02 67 63 77 01 78")] // gc: "x"
    [DataRow("76 01 04 6D 65 74 61 7D 01")] // meta: 1
    public void ApplyUpdate_SubdocumentOptionsOfWrongType_AreRejected(string options)
    {
        var doc = new YDoc();
        AssertRejected(doc, () => doc.ApplyUpdateV1(Subdocument(options)));
    }

    [TestMethod]
    public void ApplyUpdate_SubdocumentOptionsWithMeta_ReadsMeta()
    {
        var doc = new YDoc();
        doc.ApplyUpdateV1(Subdocument("76 02 02 67 63 79 04 6D 65 74 61 76 01 01 61 7D 01"));
        var subdoc = (YDoc)doc.GetMap("m").Get("k");

        Assert.IsFalse(subdoc.Gc);
        Assert.AreEqual(1, subdoc.Meta["a"]);
    }

    [TestMethod]
    public void EncodeStateAsUpdate_DeletedSubdocumentWithoutGc_Encodes()
    {
        var doc = new YDoc(new YDocOptions { Gc = false });
        var map = doc.GetMap("m");
        var subdoc = new YDoc();
        map.Set("k", subdoc);
        map.Delete("k");

        var copy = new YDoc(new YDocOptions { Gc = false });
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

        var decoder = new UpdateDecoderV2(new Lib0Reader(writer.ToArray()));
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
        Assert.ThrowsExactly<YUpdateException>(() => new YDoc().ApplyUpdateV2(update));
        Assert.ThrowsExactly<YUpdateException>(() => UpdateOperations.MergeUpdatesV2(new[] { update, update }));
        Assert.IsLessThan(1000, stopwatch.ElapsedMilliseconds);
    }
}
