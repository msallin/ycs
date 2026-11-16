using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Cases a sync server found (docs/api.md, "Limits"): what waits, how deep waiting types may nest, and updates
/// that make a document and a client that joins it disagree.
/// </summary>
[TestClass]
public class SyncServerFindingsTests
{
    private static readonly UpdateLimits Small = new() { MaxPendingStructs = 300, MaxPendingBytes = 1 << 20 };

    // The update of one transaction of doc: what its client would send.
    private static byte[] Edit(YDoc doc, Action<YDoc> edit)
    {
        byte[] update = null;
        void Capture(object sender, (byte[] Data, object Origin, Transaction Transaction) e) => update = e.Data;
        doc.Update += Capture;
        try
        {
            doc.Transact(_ => edit(doc));
        }
        finally
        {
            doc.Update -= Capture;
        }

        return update ?? throw new InvalidOperationException("The edit changed nothing.");
    }

    // A struct of the client that is never sent, and that many structs waiting for it.
    private static (byte[] Missing, byte[] Waiting) Waiting(long client, int structs)
    {
        var forger = new YDoc(new YDocOptions { ClientId = client });
        var missing = Edit(forger, d => d.GetText("text").Insert(0, "m"));
        var waiting = Edit(forger, d =>
        {
            for (var i = 0; i < structs; i++)
            {
                d.GetText("text").Insert(1, "w");
            }
        });
        return (missing, waiting);
    }

    // What waits may reach twice MaxPendingStructs (an update that lets 150 integrate brings 290 new).
    private static YDoc RoomWithTooMuchWaiting(out YDoc client)
    {
        var b = Waiting(7, 150);
        var c = Waiting(8, 150);
        var e = Waiting(9, 290);
        var room = new YDoc(new YDocOptions { Limits = Small });
        room.ApplyUpdateV1(b.Waiting);
        room.ApplyUpdateV1(c.Waiting);
        room.ApplyUpdateV1(UpdateOperations.MergeUpdates([b.Missing, e.Waiting]));
        client = new YDoc(new YDocOptions { ClientId = 20 });
        client.ApplyUpdateV1(room.EncodeStateAsUpdateV1());
        return room;
    }

    [TestMethod]
    public void ApplyUpdate_WaitingStructsOverTheLimitSentBack_AreAccepted()
    {
        var room = RoomWithTooMuchWaiting(out var client);
        client.GetText("other").Insert(0, "offline");

        var accepted = room.TryApplyUpdate(client.EncodeStateAsUpdateV1(room.EncodeStateVectorV1()));

        Assert.AreEqual((true, "offline"), (accepted, room.GetText("other").ToString()));
    }

    [TestMethod]
    public void ApplyUpdate_WaitingStructsOverTheLimitSentBack_AddNothingToWhatWaits()
    {
        var room = RoomWithTooMuchWaiting(out var client);
        var before = room.Store.PendingStructCount;

        room.ApplyUpdateV1(client.EncodeStateAsUpdateV1(room.EncodeStateVectorV1()));
        room.ApplyUpdateV1(client.EncodeStateAsUpdateV1());

        Assert.AreEqual(before, room.Store.PendingStructCount);
    }

    [TestMethod]
    public void ApplyUpdate_NewWaitingStructsWhileOverTheLimit_AreRefused()
    {
        var room = RoomWithTooMuchWaiting(out _);
        var more = Waiting(11, 2);

        var accepted = room.TryApplyUpdate(more.Waiting);

        Assert.IsFalse(accepted);
    }

    [TestMethod]
    public void ApplyUpdate_WaitingStructsOverTheLimitWithNewOnes_AreRefusedAndTheDocumentIsUnchanged()
    {
        var room = RoomWithTooMuchWaiting(out var client);
        var more = Waiting(11, 2);
        var before = Convert.ToHexString(room.EncodeStateAsUpdateV1());

        var accepted = room.TryApplyUpdate(UpdateOperations.MergeUpdates([client.EncodeStateAsUpdateV1(), more.Waiting]));

        Assert.AreEqual((false, before), (accepted, Convert.ToHexString(room.EncodeStateAsUpdateV1())));
    }

    // Nested types deeper than MaxDepth, waiting for a struct of their client: when it arrives, the types are dropped
    // (kept, they would fail every later retry), which the caller is told, as every client that joined meanwhile got
    // them as waiting structs and, with other limits (Yjs has none), integrates them.
    [TestMethod]
    public void ApplyUpdate_WaitingTypesDeeperThanTheLimitResolved_ReportsThatTheyWereDropped()
    {
        var forger = new YDoc(new YDocOptions { ClientId = 7 });
        var missing = Edit(forger, d => d.GetArray("array").Insert(0, [1]));
        var deep = Edit(forger, d =>
        {
            var array = d.GetArray("array");
            for (var i = 0; i < 40; i++)
            {
                var inner = new YArray();
                array.Insert(0, [inner]);
                array = inner;
            }
        });
        var room = new YDoc(new YDocOptions { Limits = new UpdateLimits { MaxDepth = 32 } });
        room.ApplyUpdateV1(deep);

        var e = Assert.ThrowsExactly<YUpdateException>(() => room.ApplyUpdateV1(missing));

        Assert.AreEqual((true, false, 1L), (e.PartlyApplied, room.HasPending, room.Store.GetState(7)));
    }

    private static void WriteVarUint(System.IO.Stream stream, ulong value)
    {
        while (value > 0x7F)
        {
            stream.WriteByte((byte)(0x80 | (value & 0x7F)));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }

    // Client 2 inserts "z" with origin (a, 4) and right origin (a, 1): Yjs never writes an item whose right origin
    // precedes its origin, and gives the two texts of a document that receives it and of one that joins it.
    private static byte[] ZBetweenEAndB(long a)
    {
        var crafted = new System.IO.MemoryStream();
        foreach (var value in new ulong[] { 1, 1, 2, 0 })
        {
            WriteVarUint(crafted, value);
        }

        crafted.WriteByte(0xC4);
        foreach (var value in new ulong[] { (ulong)a, 4, (ulong)a, 1, 1 })
        {
            WriteVarUint(crafted, value);
        }

        crafted.Write("z\0"u8);
        return crafted.ToArray();
    }

    [TestMethod]
    public void ApplyUpdate_ItemWhoseRightOriginPrecedesItsOrigin_IsRefusedAndAJoinerSeesTheSameText()
    {
        var a = new YDoc(new YDocOptions { ClientId = 1L << 40 });
        a.GetText("text").Insert(0, "abcdef");
        var room = new YDoc();
        room.ApplyUpdateV1(a.EncodeStateAsUpdateV1());

        var e = Assert.ThrowsExactly<YUpdateException>(() => room.ApplyUpdateV1(ZBetweenEAndB(1L << 40)));
        var joiner = new YDoc();
        joiner.ApplyUpdateV1(room.EncodeStateAsUpdateV1());

        Assert.AreEqual((false, "abcdef", "abcdef"), (e.PartlyApplied, room.GetText("text").ToString(), joiner.GetText("text").ToString()));
    }

    [TestMethod]
    public void ApplyUpdate_ItemWhoseRightOriginFollowsItsOrigin_IsAccepted()
    {
        // The honest case that looks alike: "ba" typed backwards, then an item between them (origin a, which has the
        // higher clock; right origin b, the lower one).
        var a = new YDoc(new YDocOptions { ClientId = 1 });
        var text = a.GetText("text");
        text.Insert(0, "b");
        text.Insert(0, "a");
        text.Insert(1, "m");
        var joiner = new YDoc();

        joiner.ApplyUpdateV1(a.EncodeStateAsUpdateV1());

        Assert.AreEqual("amb", joiner.GetText("text").ToString());
    }

    // Two structs under one id, the first waiting for a client nobody sends (an update cannot hold two: a client is listed
    // once): the structs that follow the first stay waiting, also once the second is integrated.
    [TestMethod]
    public void ApplyUpdate_TwoStructsUnderOneId_AJoinerIsNotAheadOfTheRoom()
    {
        var a = new YDoc(new YDocOptions { ClientId = 1L << 40 });
        a.GetText("text").Insert(0, "ab");
        var unknown = new YDoc(new YDocOptions { ClientId = 999 });
        unknown.ApplyUpdateV1(a.EncodeStateAsUpdateV1());
        unknown.GetText("text").Insert(1, "z");
        var waiting = new YDoc(new YDocOptions { ClientId = 7 });
        waiting.ApplyUpdateV1(unknown.EncodeStateAsUpdateV1());
        var first = Edit(waiting, d => d.GetText("text").Insert(1, "u"));
        var behind = Edit(waiting, d => d.GetMap("map").Set("k", 1));
        var integrating = new YDoc(new YDocOptions { ClientId = 7 });
        integrating.ApplyUpdateV1(a.EncodeStateAsUpdateV1());
        var second = Edit(integrating, d => d.GetText("text").Insert(1, "u"));
        var room = new YDoc();
        room.ApplyUpdateV1(a.EncodeStateAsUpdateV1());
        room.ApplyUpdateV1(behind);
        room.ApplyUpdateV1(first);

        var e = Assert.ThrowsExactly<YUpdateException>(() => room.ApplyUpdateV1(second));
        var joiner = new YDoc();
        joiner.ApplyUpdateV1(room.EncodeStateAsUpdateV1());

        Assert.AreEqual((true, 1L, 1L, false), (e.PartlyApplied, room.Store.GetState(7), joiner.Store.GetState(7), room.HasPending));
    }

    [TestMethod]
    public void ApplyUpdate_StructWaitingUnderTheIdOfOneTheDocumentHas_IsRefusedAndTheDocumentIsUnchanged()
    {
        var a = new YDoc(new YDocOptions { ClientId = 1L << 40 });
        a.GetText("text").Insert(0, "ab");
        var unknown = new YDoc(new YDocOptions { ClientId = 999 });
        unknown.ApplyUpdateV1(a.EncodeStateAsUpdateV1());
        unknown.GetText("text").Insert(1, "z");
        var waiting = new YDoc(new YDocOptions { ClientId = 7 });
        waiting.ApplyUpdateV1(unknown.EncodeStateAsUpdateV1());
        var first = Edit(waiting, d => d.GetText("text").Insert(1, "u"));
        var integrating = new YDoc(new YDocOptions { ClientId = 7 });
        integrating.ApplyUpdateV1(a.EncodeStateAsUpdateV1());
        var room = new YDoc();
        room.ApplyUpdateV1(a.EncodeStateAsUpdateV1());
        room.ApplyUpdateV1(Edit(integrating, d => d.GetText("text").Insert(1, "u")));
        var before = Convert.ToHexString(room.EncodeStateAsUpdateV1());

        Assert.ThrowsExactly<YUpdateException>(() => room.ApplyUpdateV1(first));

        Assert.AreEqual((before, false), (Convert.ToHexString(room.EncodeStateAsUpdateV1()), room.HasPending));
    }
}
