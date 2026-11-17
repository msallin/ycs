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
        room.ApplyUpdate(b.Waiting);
        room.ApplyUpdate(c.Waiting);
        room.ApplyUpdate(YUpdate.Merge([b.Missing, e.Waiting], format: UpdateFormat.V1));
        client = new YDoc(new YDocOptions { ClientId = 20 });
        client.ApplyUpdate(room.EncodeStateAsUpdate());
        return room;
    }

    [TestMethod]
    public void ApplyUpdate_WaitingStructsOverTheLimitSentBack_AreAccepted()
    {
        var room = RoomWithTooMuchWaiting(out var client);
        client.GetText("other").Insert(0, "offline");

        var accepted = room.TryApplyUpdate(client.EncodeStateAsUpdate(room.EncodeStateVector()));

        Assert.AreEqual((true, "offline"), (accepted, room.GetText("other").ToString()));
    }

    [TestMethod]
    public void ApplyUpdate_WaitingStructsOverTheLimitSentBack_AddNothingToWhatWaits()
    {
        var room = RoomWithTooMuchWaiting(out var client);
        var before = room.Store.PendingStructCount;

        room.ApplyUpdate(client.EncodeStateAsUpdate(room.EncodeStateVector()));
        room.ApplyUpdate(client.EncodeStateAsUpdate());

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
        var before = Convert.ToHexString(room.EncodeStateAsUpdate());

        var accepted = room.TryApplyUpdate(YUpdate.Merge([client.EncodeStateAsUpdate(), more.Waiting], format: UpdateFormat.V1));

        Assert.AreEqual((false, before), (accepted, Convert.ToHexString(room.EncodeStateAsUpdate())));
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
        room.ApplyUpdate(deep);

        var dropped = new List<PendingDrop>();
        room.PendingDropped += (_, drop) => dropped.Add(drop);

        room.ApplyUpdate(missing);

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

        joiner.ApplyUpdate(a.EncodeStateAsUpdate());

        Assert.AreEqual("amb", joiner.GetText("text").ToString());
    }

    // Two different structs under one id (crafted, or two clients that drew one id), the first waiting for a client nobody
    // sends: as in Yjs, the structs that follow the first stay waiting, also once the second is integrated, so a joiner
    // (which reads the document's state, the waiting structs included) is ahead of the document for good. A struct under
    // a known id with missing references cannot be told from an honest one (the test below), so none is rejected.
    [TestMethod]
    public void ApplyUpdate_TwoStructsUnderOneId_KeepsWhatWaitedAndAJoinerIsAheadAsInYjs()
    {
        var a = new YDoc(new YDocOptions { ClientId = 1L << 40 });
        a.GetText("text").Insert(0, "ab");
        var unknown = new YDoc(new YDocOptions { ClientId = 999 });
        unknown.ApplyUpdate(a.EncodeStateAsUpdate());
        unknown.GetText("text").Insert(1, "z");
        var waiting = new YDoc(new YDocOptions { ClientId = 7 });
        waiting.ApplyUpdate(unknown.EncodeStateAsUpdate());
        var first = Edit(waiting, d => d.GetText("text").Insert(1, "u"));
        var behind = Edit(waiting, d => d.GetMap("map")["k"] = 1);
        var integrating = new YDoc(new YDocOptions { ClientId = 7 });
        integrating.ApplyUpdate(a.EncodeStateAsUpdate());
        var second = Edit(integrating, d => d.GetText("text").Insert(1, "u"));
        var room = new YDoc();
        room.ApplyUpdate(a.EncodeStateAsUpdate());
        room.ApplyUpdate(behind);
        room.ApplyUpdate(first);

        var dropped = 0;
        room.PendingDropped += (_, _) => dropped++;

        room.ApplyUpdate(second);
        var joiner = new YDoc();
        joiner.ApplyUpdate(room.EncodeStateAsUpdate());

        Assert.AreEqual((0, 1L, 2L, true), (dropped, room.Store.GetState(7), joiner.Store.GetState(7), room.HasPending));
    }

    // An honest item under the id of a GC struct the document has, with a right origin the document lacks. A Yjs peer
    // writes the two: the update of a transaction that ran during another's observer calls (reading an event's delta opens
    // one) carries the item a later transaction of that cleanup created, whose own update carries it as a GC struct once
    // its type was collected; delivered out of order, the GC struct comes first. The item waits, as in Yjs (found by the
    // property soak: FixedFailures, known-id-with-missing-references-rejected), and is known when it is retried.
    [TestMethod]
    public void ApplyUpdate_ItemUnderTheIdOfAGcStructItHasWithMissingReferences_WaitsAsInYjs()
    {
        var origin = new YDoc(new YDocOptions { ClientId = 1 });
        origin.GetMap("m")["t"] = new YText("b");
        var originState = origin.EncodeStateAsUpdate();
        var typist = new YDoc(new YDocOptions { ClientId = 7, GarbageCollection = false });
        typist.ApplyUpdate(originState);
        static YText Text(YDoc d) => d.GetMap("m")["t"].TryGetSharedType<YText>(out var t) ? t : throw new InvalidOperationException();
        var item = Edit(typist, d => Text(d).Insert(0, "a"));
        var collector = new YDoc(new YDocOptions { ClientId = 7 });
        collector.ApplyUpdate(originState);
        var gc = Edit(collector, d =>
        {
            Text(d).Insert(0, "a");
            d.GetMap("m").Remove("t");
        });
        var room = new YDoc(new YDocOptions { GarbageCollection = false });
        room.ApplyUpdate(gc);

        room.ApplyUpdate(item);
        var waiting = (Convert.ToHexString(room.EncodeStateAsUpdate()), room.HasPending);
        room.ApplyUpdate(originState);

        // The bytes Yjs 13.6.32 writes: with the item waiting (the state holds it, not the GC struct), and once it was retried.
        Assert.AreEqual(
            (("010107004401010161020701000101010002", true), ("0201070000010201002701016D017402040001000162020701000101010002", false, 0)),
            (waiting, (Convert.ToHexString(room.EncodeStateAsUpdate()), room.HasPending, room.GetMap("m").Count)));
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

        var vector = typist.EncodeStateVector();
        for (var i = 0; i < 100; i++)
        {
            typist.GetText("t").Remove(i, 1);
        }

        var room = new YDoc(new YDocOptions { Limits = Small });
        room.ApplyUpdate(b.Waiting);
        room.ApplyUpdate(c.Waiting);
        room.ApplyUpdate(typist.EncodeStateAsUpdate(vector));
        room.ApplyUpdate(YUpdate.Merge([b.Missing, e.Waiting], format: UpdateFormat.V1));
        var client = new YDoc(new YDocOptions { ClientId = 20 });
        client.ApplyUpdate(room.EncodeStateAsUpdate());
        client.GetText("other").Insert(0, "offline");

        var accepted = room.TryApplyUpdate(client.EncodeStateAsUpdate(room.EncodeStateVector()));

        Assert.AreEqual((true, "offline"), (accepted, room.GetText("other").ToString()));
    }

    // A forged struct under the honest client's next ids (1:5, origin 99:0), waiting for a client nobody sends: when the
    // honest update arrives, it is applied whole, and the forged struct, known now, keeps waiting, as in Yjs.
    [TestMethod]
    public void ApplyUpdate_HonestUpdateRetriesAForgedStructUnderItsIds_StandsAndTheForgedOneKeepsWaiting()
    {
        var honest = new YDoc(new YDocOptions { ClientId = 1 });
        honest.GetText("text").Insert(0, "abc");
        var room = new YDoc();
        room.ApplyUpdate(honest.EncodeStateAsUpdate());
        room.ApplyUpdate(Convert.FromHexString("01010105846300015800"));
        var dropped = new List<PendingDrop>();
        room.PendingDropped += (_, drop) => dropped.Add(drop);

        room.ApplyUpdate(Edit(honest, d => d.GetText("text").Insert(3, "def")));

        Assert.AreEqual(("abcdef", true, 0), (room.GetText("text").ToString(), room.HasPending, dropped.Count));
    }
}
