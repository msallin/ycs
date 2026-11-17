using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class SyncProtocolTests
{
    private static readonly byte[] Garbage = [0xff];

    private static byte[] UpdateMessage(ReadOnlySpan<byte> update)
    {
        var writer = new ArrayBufferWriter<byte>();
        SyncProtocol.WriteUpdate(writer, update);
        return writer.WrittenSpan.ToArray();
    }

    // The messages the Stream-based SyncProtocol wrote for the same documents (Fixtures/sync-messages.json).
    [TestMethod]
    public void Write_CorpusDocuments_BytesAreThoseOfTheFixture()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sync-messages.json")));
        var entries = fixture.RootElement.EnumerateArray().ToList();
        var peer = new YDoc(new YDocOptions { ClientId = 9 });
        peer.GetText("t").Insert(0, "hey");
        var vectors = new System.Collections.Generic.Dictionary<string, byte[]> { ["none"] = new YDoc().EncodeStateVector(), ["peer"] = peer.EncodeStateVector() };
        var actual = new System.Collections.Generic.List<string>();
        var expected = new System.Collections.Generic.List<string>();

        foreach (var (name, doc) in SyncGoldenDocs.All())
        {
            foreach (var entry in entries.Where(e => e.GetProperty("doc").GetString() == name))
            {
                if (entry.TryGetProperty("update", out var update))
                {
                    actual.Add($"{name} update {SyncGoldenDocs.Describe(doc.EncodeStateAsUpdate())} {SyncGoldenDocs.Describe(UpdateMessage(doc.EncodeStateAsUpdate()))}");
                    expected.Add($"{name} update {update.GetString()} {entry.GetProperty("message").GetString()}");
                    continue;
                }

                var v2 = entry.GetProperty("v2").GetBoolean();
                var step1 = new ArrayBufferWriter<byte>();
                SyncProtocol.WriteSyncStep1(step1, doc);
                var step2 = new ArrayBufferWriter<byte>();
                SyncProtocol.WriteSyncStep2(step2, doc, vectors[entry.GetProperty("sv").GetString()!], v2 ? UpdateFormat.V2 : UpdateFormat.V1);
                actual.Add($"{name} {v2} {entry.GetProperty("sv")} {SyncGoldenDocs.Describe(step1.WrittenSpan)} {SyncGoldenDocs.Describe(step2.WrittenSpan)}");
                expected.Add($"{name} {v2} {entry.GetProperty("sv")} {entry.GetProperty("step1").GetString()} {entry.GetProperty("step2").GetString()}");
            }
        }

        CollectionAssert.AreEqual(expected, actual);
        Assert.IsGreaterThan(20, actual.Count);
    }

    [TestMethod]
    public void ReadSyncMessage_Step1_AnswersWithStep2ThatBringsThePeerUpToDate()
    {
        var server = new YDoc(new YDocOptions { ClientId = 1 });
        server.GetText("t").Insert(0, "ok");
        var client = new YDoc(new YDocOptions { ClientId = 2 });
        var step1 = new ArrayBufferWriter<byte>();
        SyncProtocol.WriteSyncStep1(step1, client);
        var reply = new ArrayBufferWriter<byte>();

        var first = SyncProtocol.ReadSyncMessage(step1.WrittenSpan, reply, server, "peer");
        var second = SyncProtocol.ReadSyncMessage(reply.WrittenSpan, new ArrayBufferWriter<byte>(), client, "server");

        Assert.AreEqual((new SyncReadResult(SyncMessageType.SyncStep1, true), new SyncReadResult(SyncMessageType.SyncStep2, false), "ok"), (first, second, client.GetText("t").ToString()));
    }

    [TestMethod]
    public void ReadSyncMessage_UpdateV2_AppliesItAndWritesNoReply()
    {
        var source = new YDoc(new YDocOptions { ClientId = 1 });
        source.GetText("t").Insert(0, "v2");
        var message = UpdateMessage(source.EncodeStateAsUpdate(format: UpdateFormat.V2));
        var doc = new YDoc();
        var reply = new ArrayBufferWriter<byte>();

        var type = SyncProtocol.ReadSyncMessage(message, reply, doc, null, format: UpdateFormat.V2);

        Assert.AreEqual((new SyncReadResult(SyncMessageType.Update, false), 0, "v2"), (type, reply.WrittenCount, doc.GetText("t").ToString()));
    }

    [TestMethod]
    public void ReadSyncMessage_SequenceInSeveralSegments_ReadsItLikeASpan()
    {
        var source = new YDoc(new YDocOptions { ClientId = 1 });
        source.GetText("t").Insert(0, "split");
        var message = UpdateMessage(source.EncodeStateAsUpdate());
        var first = new Segment(message.AsMemory(0, 2), null);
        var last = new Segment(message.AsMemory(2), first);
        var doc = new YDoc();

        SyncProtocol.ReadSyncMessage(new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length), new ArrayBufferWriter<byte>(), doc, null);
        var oneSegment = new YDoc();
        SyncProtocol.ReadSyncMessage(new ReadOnlySequence<byte>(message), new ArrayBufferWriter<byte>(), oneSegment, null);

        Assert.AreEqual(("split", "split"), (doc.GetText("t").ToString(), oneSegment.GetText("t").ToString()));
    }

    [TestMethod]
    public void ReadSyncMessage_BytesAfterTheMessage_AreIgnored()
    {
        var source = new YDoc(new YDocOptions { ClientId = 1 });
        source.GetText("t").Insert(0, "ok");
        var message = UpdateMessage(source.EncodeStateAsUpdate()).Concat(new byte[] { 9, 9 }).ToArray();
        var doc = new YDoc();

        SyncProtocol.ReadSyncMessage(message, new ArrayBufferWriter<byte>(), doc, null);

        Assert.AreEqual("ok", doc.GetText("t").ToString());
    }

    [TestMethod]
    public void ReadSyncMessage_MalformedUpdate_ThrowsAndLeavesTheDocument()
    {
        var doc = new YDoc();

        Assert.ThrowsExactly<YUpdateException>(() => SyncProtocol.ReadSyncMessage(UpdateMessage(Garbage), new ArrayBufferWriter<byte>(), doc, "peer"));
    }

    [TestMethod]
    public void ReadSyncMessage_ObserverThrows_PropagatesItAfterTheApply()
    {
        var source = new YDoc(new YDocOptions { ClientId = 1 });
        source.GetText("t").Insert(0, "ok");
        var doc = new YDoc();
        doc.GetText("t").Observe(_ => throw new InvalidOperationException("observer"));

        var error = Assert.ThrowsExactly<InvalidOperationException>(() =>
            SyncProtocol.ReadSyncMessage(UpdateMessage(source.EncodeStateAsUpdate()), new ArrayBufferWriter<byte>(), doc, "peer"));

        Assert.AreEqual(("observer", "ok"), (error.Message, doc.GetText("t").ToString()));
    }

    [TestMethod]
    [DataRow(new byte[] { }, DisplayName = "empty")]
    [DataRow(new byte[] { 2 }, DisplayName = "no length")]
    [DataRow(new byte[] { 2, 5, 1 }, DisplayName = "payload cut short")]
    [DataRow(new byte[] { 3, 0 }, DisplayName = "unknown type")]
    [DataRow(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01 }, DisplayName = "type too long")]
    [DataRow(new byte[] { 2, 0xff, 0xff, 0xff, 0xff, 0x0f }, DisplayName = "length beyond the message")]
    [DataRow(new byte[] { 2, 0xff, 0xff, 0xff, 0xff, 0x1f }, DisplayName = "length beyond 32 bits")]
    public void ReadSyncMessage_MalformedMessage_Throws(byte[] message)
    {
        Assert.ThrowsExactly<YUpdateException>(() => SyncProtocol.ReadSyncMessage(message, new ArrayBufferWriter<byte>(), new YDoc(), null));
    }

    [TestMethod]
    public void Write_AnyWriter_AppendsToWhatIsThere()
    {
        var writer = new ArrayBufferWriter<byte>();
        writer.Write(new byte[] { 7 }.AsSpan());

        SyncProtocol.WriteUpdate(writer, new byte[] { 1, 2 });

        CollectionAssert.AreEqual(new byte[] { 7, 2, 2, 1, 2 }, writer.WrittenSpan.ToArray());
    }

    [TestMethod]
    public void MessageType_WrittenThenRead_StripsItAndKeepsTheBody()
    {
        var frame = new ArrayBufferWriter<byte>();
        SyncProtocol.WriteMessageType(frame, SyncProtocol.MessageAwareness);
        SyncProtocol.WriteMessageType(frame, 300);

        var type = SyncProtocol.ReadMessageType(frame.WrittenSpan, out var body);

        Assert.AreEqual((SyncProtocol.MessageAwareness, "AC02"), (type, Convert.ToHexString(body)));
    }

    [TestMethod]
    public void ReadMessageType_Empty_Throws()
    {
        Assert.ThrowsExactly<YUpdateException>(() => SyncProtocol.ReadMessageType([], out _));
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory, Segment? previous)
        {
            Memory = memory;
            if (previous is not null)
            {
                RunningIndex = previous.RunningIndex + previous.Memory.Length;
                previous.Next = this;
            }
        }
    }
}
