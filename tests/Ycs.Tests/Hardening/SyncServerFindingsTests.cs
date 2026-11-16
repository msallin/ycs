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
}
