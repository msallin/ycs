// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Reuse and pooling in the encoding layer: writers and encoders that are reset and reused, pooled
/// buffers returned on every path, the caller's <see cref="IBufferWriter{T}"/> written directly, and
/// the thread-local <see cref="UpdateReader"/>.
/// </summary>
[TestClass]
public class CodecReuseTests
{
    [TestMethod]
    public void Lib0Writer_PooledReset_ReturnsTheBuffer()
    {
        var before = BufferPool<byte>.OutstandingOnCurrentThread;
        var writer = new Lib0Writer(pooled: true);
        writer.WriteBytes(new byte[5000]);
        Assert.AreEqual(before + 1, BufferPool<byte>.OutstandingOnCurrentThread, "one buffer, the smaller ones returned while growing");

        writer.Reset();
        writer.Reset();

        Assert.AreEqual(before, BufferPool<byte>.OutstandingOnCurrentThread);
    }

    [TestMethod]
    public void Lib0Writer_Destination_WritesWhatAnOwnedBufferHolds()
    {
        var owned = new Lib0Writer();
        WriteSample(owned);
        var destination = new ArrayBufferWriter<byte>(1);
        var direct = new Lib0Writer();
        direct.StartWritingTo(destination);

        WriteSample(direct);
        direct.Flush();

        CollectionAssert.AreEqual(owned.ToArray(), destination.WrittenSpan.ToArray());
    }

    [TestMethod]
    public void Lib0Writer_Destination_HasNoWrittenSpan()
    {
        var writer = new Lib0Writer();
        writer.StartWritingTo(new ArrayBufferWriter<byte>());

        Assert.ThrowsExactly<InvalidOperationException>(() => writer.ToArray());
    }

    [TestMethod]
    public void StreamEncoder_GetWrittenSpanTwice_FlushesTheRunOnce()
    {
        var encoder = new UintOptRleEncoder();
        encoder.Write(5);
        encoder.Write(5);
        encoder.Write(5);

        var first = encoder.GetWrittenSpan().ToArray();

        CollectionAssert.AreEqual(first, encoder.GetWrittenSpan().ToArray());
    }

    [TestMethod]
    public void StreamEncoder_WriteAfterFlush_Throws()
    {
        var encoder = new IntDiffOptRleEncoder();
        encoder.Write(1);
        _ = encoder.GetWrittenSpan();

        Assert.ThrowsExactly<InvalidOperationException>(() => encoder.Write(2));
    }

    [TestMethod]
    public void StreamEncoder_Reset_EncodesLikeANewEncoder()
    {
        var reused = new RleEncoder(pooled: true);
        reused.Write(7);
        _ = reused.GetWrittenSpan();
        reused.Reset();
        var fresh = new RleEncoder();

        foreach (var encoder in new[] { reused, fresh })
        {
            encoder.Write(1);
            encoder.Write(1);
            encoder.Write(2);
        }

        CollectionAssert.AreEqual(fresh.ToArray(), reused.ToArray());
        reused.Reset();
    }

    [TestMethod]
    public void StringEncoder_SurrogatePairSplitBetweenStrings_EncodesTheConcatenation()
    {
        var encoder = new StringEncoder();
        encoder.Write("a\uD83D");
        encoder.Write("\uDE00b");

        var decoder = new StringDecoder(new Lib0Reader(encoder.ToArray()));

        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("a😀b"), encoder.GetUtf8().ToArray());
        Assert.AreEqual("a\uD83D", decoder.Read());
        Assert.AreEqual("\uDE00b", decoder.Read());
    }

    [TestMethod]
    public void StringEncoder_LoneHighSurrogateAtTheEnd_EncodesAsUtf8Does()
    {
        var encoder = new StringEncoder();
        encoder.Write("x\uD800");

        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("x\uD800"), encoder.GetUtf8().ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UpdateEncoder_Reset_EncodesTheNextUpdateLikeANewEncoder(bool v2)
    {
        var first = SampleDoc(1);
        var second = SampleDoc(2);
        IUpdateEncoder reused = v2 ? new UpdateEncoderV2() : new UpdateEncoderV1();
        EncodingUtils.WriteStateAsUpdate(reused, first.Store, new Dictionary<long, long>());
        _ = reused.ToArray();

        reused.Reset();
        EncodingUtils.WriteStateAsUpdate(reused, second.Store, new Dictionary<long, long>());

        CollectionAssert.AreEqual(v2 ? second.EncodeStateAsUpdateV2() : second.EncodeStateAsUpdateV1(), reused.ToArray());
    }

    [TestMethod]
    public void UpdateEncoder_Return_ResetsAndReturnsTheBuffers()
    {
        var before = BufferPool<byte>.OutstandingOnCurrentThread;
        var encoder = UpdateEncoderV2.Rent();
        EncodingUtils.WriteStateAsUpdate(encoder, SampleDoc(1).Store, new Dictionary<long, long>());

        encoder.Return();

        Assert.AreEqual(before, BufferPool<byte>.OutstandingOnCurrentThread);
    }

    [TestMethod]
    public void UpdateEncoder_ReturnedTwice_Throws()
    {
        var encoder = UpdateEncoderV1.Rent();
        encoder.Return();

        Assert.ThrowsExactly<InvalidOperationException>(encoder.Return);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void EncodeStateAsUpdate_MalformedStateVector_ThrowsWritesNothingAndReturnsEverything(bool v2)
    {
        var doc = SampleDoc(1);
        var destination = new ArrayBufferWriter<byte>();
        var before = BufferPool<byte>.OutstandingOnCurrentThread;

        Assert.ThrowsExactly<YUpdateException>(() => EncodingUtils.EncodeStateAsUpdate(doc.Store, destination, new byte[] { 3, 1 }, v2));

        Assert.AreEqual(0, destination.WrittenCount);
        Assert.AreEqual(before, BufferPool<byte>.OutstandingOnCurrentThread);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void EncodeStateAsUpdate_ManyClients_WritesTheHighestClientFirst(bool v2)
    {
        var doc = new YDoc(new YDocOptions { ClientId = 0 });
        for (var client = 1; client <= 100; client++)
        {
            var peer = new YDoc(new YDocOptions { ClientId = client });
            peer.GetText("text").Insert(0, $"{client} ");
            peer.GetText("text").Delete(0, 1);
            doc.ApplyUpdate(peer.EncodeStateAsUpdate());
        }

        var before = BufferPool<long>.OutstandingOnCurrentThread;
        var update = EncodingUtils.EncodeStateAsUpdate(doc.Store, default, v2);
        var decoded = v2 ? UpdateOperations.DecodeUpdateV2(update) : UpdateOperations.DecodeUpdate(update);

        CollectionAssert.AreEqual(Enumerable.Range(1, 100).Reverse().Select(c => (long)c).ToList(), decoded.Structs.Select(s => s.Id.Client).Distinct().ToList());
        Assert.HasCount(100, decoded.DeleteSet);
        Assert.AreEqual(before, BufferPool<long>.OutstandingOnCurrentThread);
    }

    [TestMethod]
    public void EncodeStateAsUpdate_LargeDocumentIntoBufferWriter_BytesEqualTheArray()
    {
        var doc = SampleDoc(1);
        doc.GetText("text").Insert(0, new string('x', 100_000));
        var destination = new ArrayBufferWriter<byte>(1);

        EncodingUtils.EncodeStateAsUpdate(doc.Store, destination, default, v2: false);

        CollectionAssert.AreEqual(EncodingUtils.EncodeStateAsUpdate(doc.Store, default, v2: false), destination.WrittenSpan.ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void EncodeStateAsUpdate_DiffIntoBufferWriter_AllocatesNothingOnceWarm(bool v2)
    {
#if DEBUG
        Assert.Inconclusive("Allocations are measured with -c Release (a Debug build of Ycs allocates more).");
#endif
        // Only items with an origin: a root-parented item looks its root's name up in YDoc, which allocates.
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var text = doc.GetText("text");
        text.Insert(0, "hello");
        var stateVector = doc.EncodeStateVector();
        text.Insert(5, " world");
        text.Delete(0, 1);
        var destination = new ArrayBufferWriter<byte>(4096);

        var allocated = Allocated(() =>
        {
            destination.ResetWrittenCount();
            EncodingUtils.EncodeStateAsUpdate(doc.Store, destination, stateVector, v2);
        });

        Assert.AreEqual(0, allocated);
    }

    [TestMethod]
    public void EncodeStateVector_IntoBufferWriter_AllocatesNothingOnceWarm()
    {
#if DEBUG
        Assert.Inconclusive("Allocations are measured with -c Release (a Debug build of Ycs allocates more).");
#endif
        var doc = SampleDoc(1);
        var destination = new ArrayBufferWriter<byte>(256);

        var allocated = Allocated(() =>
        {
            destination.ResetWrittenCount();
            EncodingUtils.EncodeStateVector(doc.Store, destination);
        });

        Assert.AreEqual(0, allocated);
        CollectionAssert.AreEqual(doc.EncodeStateVector(), destination.WrittenSpan.ToArray());
    }

    [TestMethod]
    public void UpdateReader_SecondRead_ReusesTheDecoders()
    {
        var update = SampleDoc(1).EncodeStateAsUpdateV2();

        var reused = Allocated(() =>
        {
            using var lease = UpdateReader.Read(update, 0, update.Length, v2: true, long.MaxValue);
        });
        var fresh = Allocated(() =>
        {
            var decoder = new UpdateDecoderV2(new Lib0Reader(update), long.MaxValue);
            EncodingUtils.ReadClientStructRefs(decoder);
            DeleteSet.Read(decoder);
        });

        // The decoded structs are new either way; the decoders, their readers and the collections are not.
        Assert.IsLessThan(fresh - 800, reused, $"reused {reused} B, fresh {fresh} B");
    }

    [TestMethod]
    public void UpdateReader_MalformedUpdate_ThrowsAndStaysUsable()
    {
        var update = SampleDoc(1).EncodeStateAsUpdateV1();

        Assert.ThrowsExactly<YUpdateException>(() => UpdateReader.Read(update, 0, update.Length - 3, v2: false, long.MaxValue));
        using var lease = UpdateReader.Read(update, 0, update.Length, v2: false, long.MaxValue);

        Assert.AreEqual(Describe(EncodingUtils.ReadClientStructRefs(new UpdateDecoderV1(new Lib0Reader(update), long.MaxValue))), Describe(lease.Structs));
    }

    [TestMethod]
    public void UpdateReader_ReadWhileALeaseIsOpen_GetsItsOwnReader()
    {
        var first = SampleDoc(1).EncodeStateAsUpdateV1();
        var second = SampleDoc(2).EncodeStateAsUpdateV1();

        using var outer = UpdateReader.Read(first, 0, first.Length, v2: false, long.MaxValue);
        var expected = Describe(outer.Structs);
        using (var inner = UpdateReader.Read(second, 0, second.Length, v2: false, long.MaxValue))
        {
            Assert.AreNotEqual(expected, Describe(inner.Structs));
        }

        Assert.AreEqual(expected, Describe(outer.Structs));
    }

    [TestMethod]
    public void UpdateReader_LeaseDisposedTwice_StaysReleased()
    {
        var update = SampleDoc(1).EncodeStateAsUpdateV1();
        var lease = UpdateReader.Read(update, 0, update.Length, v2: false, long.MaxValue);
        lease.Dispose();
        using var next = UpdateReader.Read(update, 0, update.Length, v2: false, long.MaxValue);

        lease.Dispose();

        Assert.IsNotEmpty(next.Structs, "the second dispose must not release the reader the next lease holds");
        Assert.ThrowsExactly<ObjectDisposedException>(() => lease.Structs);
    }

    [TestMethod]
    public void ApplyUpdate_ObserverThrows_ReleasesTheDecodedUpdateAndTheCopy()
    {
        var update = SampleDoc(1).EncodeStateAsUpdateV1();
        var doc = new YDoc();
        doc.GetText("text").EventHandler += (_, _) => throw new InvalidOperationException("observer");
        var outstanding = BufferPool<byte>.OutstandingOnCurrentThread;

        Assert.ThrowsExactly<InvalidOperationException>(() => doc.ApplyUpdate(update.AsSpan()));

        Assert.AreEqual($"True|{outstanding}", $"{UpdateReader.IsFreeOnCurrentThread}|{BufferPool<byte>.OutstandingOnCurrentThread}");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TryApplyUpdate_Malformed_ReleasesTheDecodedUpdate(bool v2)
    {
        var update = v2 ? SampleDoc(1).EncodeStateAsUpdateV2() : SampleDoc(1).EncodeStateAsUpdateV1();
        var doc = new YDoc();

        var applied = v2 ? doc.TryApplyUpdateV2(update.AsSpan(0, update.Length - 3)) : doc.TryApplyUpdate(update.AsSpan(0, update.Length - 3));

        Assert.AreEqual("False|True", $"{applied}|{UpdateReader.IsFreeOnCurrentThread}");
    }

    [TestMethod]
    public void ApplyUpdate_ObserverAppliesAnotherUpdate_AppliesBothAndReleasesThem()
    {
        var first = SampleDoc(1).EncodeStateAsUpdateV1();
        var second = SampleDoc(2).EncodeStateAsUpdateV1();
        var doc = new YDoc();
        var text = doc.GetText("text");
        var applied = false;
        text.EventHandler += (_, _) =>
        {
            if (!applied)
            {
                applied = true;
                doc.ApplyUpdate(second);
            }
        };

        doc.ApplyUpdate(first);

        var expected = new YDoc();
        expected.ApplyUpdate(first);
        expected.ApplyUpdate(second);
        Assert.AreEqual($"{expected.GetText("text")}|True", $"{text}|{UpdateReader.IsFreeOnCurrentThread}");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ApplyUpdate_PendingUpdateThenTheMissingOne_IntegratesBothAndReleasesThem(bool v2)
    {
        var source = new YDoc(new YDocOptions { ClientId = 1 });
        var updates = new List<byte[]>();
        source.Update += (_, e) => updates.Add(e.data);
        source.UpdateV2 += (_, e) => updates.Add(e.data);
        var text = source.GetText("text");
        text.Insert(0, "abc");
        text.Insert(3, "def");
        text.Delete(1, 2);
        var doc = new YDoc();
        var pick = v2 ? 1 : 0;

        for (var i = updates.Count - 2 + pick; i >= 0; i -= 2)
        {
            if (v2)
            {
                doc.ApplyUpdateV2(updates[i]);
            }
            else
            {
                doc.ApplyUpdate(updates[i]);
            }
        }

        Assert.AreEqual("adef|False|True", $"{doc.GetText("text")}|{doc.HasPending}|{UpdateReader.IsFreeOnCurrentThread}");
    }

    [TestMethod]
    public void YDoc_EncodeStateVectorIntoBufferWriter_WritesTheArraysBytes()
    {
        var doc = SampleDoc(3);
        var destination = new ArrayBufferWriter<byte>(1);

        doc.EncodeStateVector(destination);

        CollectionAssert.AreEqual(doc.EncodeStateVector(), destination.WrittenSpan.ToArray());
    }

    [TestMethod]
    public void YDoc_EncodeStateVectorV2_IsTheV1Bytes()
    {
        var doc = SampleDoc(3);

        CollectionAssert.AreEqual(doc.EncodeStateVectorV1(), doc.EncodeStateVectorV2());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UpdateEvent_HandlerEncodesTheDocument_GetsItsOwnUpdateAndReturnsTheBuffers(bool v2)
    {
        var doc = new YDoc(new YDocOptions { ClientId = 7 });
        var received = new List<byte[]>();
        var encodedInTheHandler = new List<byte[]>();
        EventHandler<(byte[] data, object origin, Transaction transaction)> handler = (_, e) =>
        {
            received.Add(e.data);
            encodedInTheHandler.Add(v2 ? doc.EncodeStateAsUpdateV2() : doc.EncodeStateAsUpdateV1());
        };
        if (v2)
        {
            doc.UpdateV2 += handler;
        }
        else
        {
            doc.Update += handler;
        }

        var outstanding = BufferPool<byte>.OutstandingOnCurrentThread;
        doc.GetText("text").Insert(0, "abc");

        // The first update of a document is the whole document.
        CollectionAssert.AreEqual(encodedInTheHandler[0], received[0]);
        Assert.AreEqual(outstanding, BufferPool<byte>.OutstandingOnCurrentThread);
    }

    [TestMethod]
    public void UpdateReader_DamagedUpdates_ReadsWhatFreshDecodersRead()
    {
        // Reused after every failure, the reader must still read (or reject) each update exactly as new decoders do.
        var random = new Random(7);
        var docs = new[] { SampleDoc(1), SampleDoc(2), ManyItemsDoc() };
        var updates = docs.SelectMany(d => new[] { (V2: false, Bytes: d.EncodeStateAsUpdateV1()), (V2: true, Bytes: d.EncodeStateAsUpdateV2()) }).ToArray();

        for (var i = 0; i < 20_000; i++)
        {
            var (v2, original) = updates[random.Next(updates.Length)];
            var bytes = Damage(original, random);

            var fresh = Outcome(() =>
            {
                IUpdateDecoder decoder = v2 ? new UpdateDecoderV2(new Lib0Reader(bytes), long.MaxValue) : new UpdateDecoderV1(new Lib0Reader(bytes), long.MaxValue);
                return Describe(EncodingUtils.ReadClientStructRefs(decoder)) + " | " + Describe(DeleteSet.Read(decoder));
            });
            var reused = Outcome(() =>
            {
                using var lease = UpdateReader.Read(bytes, 0, bytes.Length, v2, long.MaxValue);
                return Describe(lease.Structs) + " | " + Describe(lease.DeleteSet);
            });

            Assert.AreEqual(fresh, reused, $"input {i}: {Convert.ToHexString(bytes)}");
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MergeUpdates_MalformedUpdate_ReturnsEverything(bool v2)
    {
        var doc = SampleDoc(1);
        var update = v2 ? doc.EncodeStateAsUpdateV2() : doc.EncodeStateAsUpdateV1();
        var truncated = update.AsSpan(0, update.Length - 2).ToArray();
        var before = BufferPool<byte>.OutstandingOnCurrentThread;

        Assert.ThrowsExactly<YUpdateException>(() => _ = v2 ? UpdateOperations.MergeUpdatesV2([update, truncated]) : UpdateOperations.MergeUpdates([update, truncated]));

        Assert.AreEqual(before, BufferPool<byte>.OutstandingOnCurrentThread);
    }

    private static YDoc SampleDoc(int clientId)
    {
        var doc = new YDoc(new YDocOptions { ClientId = clientId });
        var text = doc.GetText("text");
        text.Insert(0, $"hello {clientId} 😀 world");
        text.Format(0, 5, new Dictionary<string, object> { ["bold"] = true });
        text.Delete(2, 2);
        var map = doc.GetMap("map");
        map.Set("number", clientId);
        map.Set("list", new List<object> { "a", 1, true });
        var nested = new YArray();
        map.Set("nested", nested);
        nested.Insert(0, new object[] { "x", "y" });
        return doc;
    }

    private static YDoc ManyItemsDoc()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 3 });
        var text = doc.GetText("text");
        var random = new Random(3);
        for (var i = 0; i < 200; i++)
        {
            text.Insert(random.Next(text.Length + 1), "ab" + i);
            if (i % 5 == 0)
            {
                text.Delete(random.Next(text.Length - 2), 2);
            }
        }

        var peer = new YDoc(new YDocOptions { ClientId = 4 });
        peer.ApplyUpdate(doc.EncodeStateAsUpdate());
        peer.GetMap("map").Set("k", "v");
        peer.GetText("text").Delete(1, 3);
        doc.ApplyUpdate(peer.EncodeStateAsUpdate());
        return doc;
    }

    private static byte[] Damage(byte[] update, Random random)
    {
        var bytes = update.ToList();
        for (var n = 1 + random.Next(3); n > 0 && bytes.Count > 0; n--)
        {
            var at = random.Next(bytes.Count);
            switch (random.Next(4))
            {
                case 0:
                    bytes[at] ^= (byte)(1 << random.Next(8));
                    break;
                case 1:
                    bytes.RemoveRange(at, bytes.Count - at);
                    break;
                case 2:
                    bytes.Insert(at, (byte)random.Next(256));
                    break;
                default:
                    bytes[at] = (byte)random.Next(256);
                    break;
            }
        }

        return bytes.ToArray();
    }

    private static string Outcome(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is not AssertFailedException)
        {
            return $"{e.GetType().Name}: {e.Message}";
        }
    }

    private static string Describe(DeleteSet ds) =>
        string.Join(";", ds.Clients.OrderBy(kvp => kvp.Key).Select(kvp =>
            $"{kvp.Key}:" + string.Join(",", kvp.Value.Select(d => $"{d.Clock}+{d.Length}"))));

    private static void WriteSample(Lib0Writer writer)
    {
        for (var i = 0; i < 2000; i++)
        {
            writer.WriteVarUint((ulong)i * 7919);
            writer.WriteVarInt(-i);
            writer.WriteByte((byte)i);
            writer.WriteVarString(i % 100 == 0 ? new string('s', 700) : "key" + i);
        }

        writer.WriteBytes(new byte[3000]);
        writer.WriteFloat64(1.5);
    }

    /// <summary>Bytes allocated on this thread per call (averaged over 100), after 10 to warm up.</summary>
    private static long Allocated(Action action)
    {
        for (var i = 0; i < 10; i++)
        {
            action();
        }

        var before = System.GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            action();
        }

        return (System.GC.GetAllocatedBytesForCurrentThread() - before) / 100;
    }

    private static string Describe(Dictionary<long, List<AbstractStruct>> structs) =>
        string.Join(";", structs.OrderBy(kvp => kvp.Key).Select(kvp =>
            $"{kvp.Key}:" + string.Join(",", kvp.Value.Select(s => $"{s.GetType().Name}@{s.Id.Clock}+{s.Length}"))));
}
