using System;
using System.Collections.Generic;
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
        void Capture(object sender, UpdateEventArgs e) => update = e.Update.ToArray();
        doc.Updated += Capture;
        try
        {
            doc.Transact(_ => edit(doc));
        }
        finally
        {
            doc.Updated -= Capture;
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

        var dropped = new List<PendingDrop>();
        room.PendingDropped += (_, drop) => dropped.Add(drop);

        room.ApplyUpdateV1(missing);

        Assert.AreEqual((1, 7L, false, 1L), (dropped.Count, dropped[0].Clients[0], room.HasPending, room.Store.GetState(7)));
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

        var dropped = 0;
        room.PendingDropped += (_, _) => dropped++;

        room.ApplyUpdateV1(second);
        var joiner = new YDoc();
        joiner.ApplyUpdateV1(room.EncodeStateAsUpdateV1());

        Assert.AreEqual((1, 1L, 1L, false), (dropped, room.Store.GetState(7), joiner.Store.GetState(7), room.HasPending));
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

    // An honest client's step 2 carries back the deletions of unknown structs it was sent along with the waiting structs.
    [TestMethod]
    public void ApplyUpdate_WaitingStructsAndDeletionsOverTheLimitSentBack_AreAccepted()
    {
        var b = Waiting(7, 150);
        var c = Waiting(8, 150);
        var e = Waiting(9, 290);
        var typist = new YDoc(new YDocOptions { ClientId = 10 });
        for (var i = 0; i < 200; i++)
        {
            typist.GetText("t").Insert(0, "x");
        }

        var vector = typist.EncodeStateVectorV1();
        for (var i = 0; i < 100; i++)
        {
            typist.GetText("t").Remove(i, 1);
        }

        var room = new YDoc(new YDocOptions { Limits = Small });
        room.ApplyUpdateV1(b.Waiting);
        room.ApplyUpdateV1(c.Waiting);
        room.ApplyUpdateV1(typist.EncodeStateAsUpdateV1(vector));
        room.ApplyUpdateV1(UpdateOperations.MergeUpdates([b.Missing, e.Waiting]));
        var client = new YDoc(new YDocOptions { ClientId = 20 });
        client.ApplyUpdateV1(room.EncodeStateAsUpdateV1());
        client.GetText("other").Insert(0, "offline");

        var accepted = room.TryApplyUpdate(client.EncodeStateAsUpdateV1(room.EncodeStateVectorV1()));

        Assert.AreEqual((true, "offline"), (accepted, room.GetText("other").ToString()));
    }

    // A forged struct under the honest client's next ids, waiting for a client nobody sends: when the honest update
    // arrives, the forged one is dropped (known, with references that are missing), and the sender is not blamed.
    [TestMethod]
    public void ApplyUpdate_HonestUpdateRetriesAForgedStructUnderItsIds_StandsAndReportsTheDrop()
    {
        var honest = new YDoc(new YDocOptions { ClientId = 1 });
        honest.GetText("text").Insert(0, "abc");
        var room = new YDoc();
        room.ApplyUpdateV1(honest.EncodeStateAsUpdateV1());
        room.ApplyUpdateV1(Convert.FromHexString("01010105846300015800"));
        var dropped = new List<PendingDrop>();
        room.PendingDropped += (_, drop) => dropped.Add(drop);

        room.ApplyUpdateV1(Edit(honest, d => d.GetText("text").Insert(3, "def")));

        Assert.AreEqual(("abcdef", false, 1), (room.GetText("text").ToString(), room.HasPending, dropped.Count));
    }
}
