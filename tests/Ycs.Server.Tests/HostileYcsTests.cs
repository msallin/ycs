using Ycs.Server.Tests.Support;

namespace Ycs.Server.Tests;

/// <summary>Ycs behaviour the hostile fuzz found, reproduced without the server.</summary>
[TestClass]
public class HostileYcsTests
{
    private static readonly UpdateLimits Small = new() { MaxPendingStructs = 300, MaxPendingBytes = 1 << 20 };

    // A struct of the client that is never sent, and that many structs waiting for it.
    private static (byte[] Missing, byte[] Waiting) Waiting(long client, int structs)
    {
        var forger = new YDoc(new YDocOptions { ClientId = client });
        var missing = HostileUpdates.Edit(forger, d => d.GetText("text").Insert(0, "m"));
        var waiting = HostileUpdates.Edit(forger, d =>
        {
            for (var i = 0; i < structs; i++)
            {
                d.GetText("text").Insert(1, "w");
            }
        });
        return (missing, waiting);
    }

    // What waits may reach twice MaxPendingStructs (an update that lets 150 integrate brings 290 new); then every
    // update that carries what waits again, as a client's step 2 after it received the room's state, is refused,
    // although it adds nothing that waits.
    [TestMethod]
    [Ignore("ycs: an update whose waiting structs are all duplicates is refused once what waits exceeds the limit")]
    public void ApplyUpdate_WaitingStructsOverTheLimitSentBack_AreAccepted()
    {
        var b = Waiting(7, 150);
        var c = Waiting(8, 150);
        var e = Waiting(9, 290);
        var room = new YDoc(new YDocOptions { Limits = Small });
        room.ApplyUpdateV1(b.Waiting);
        room.ApplyUpdateV1(c.Waiting);
        room.ApplyUpdateV1(UpdateOperations.MergeUpdates([b.Missing, e.Waiting]));
        var client = new YDoc(new YDocOptions { ClientId = 20 });
        client.ApplyUpdateV1(room.EncodeStateAsUpdateV1());
        client.GetText("other").Insert(0, "offline");

        var accepted = room.TryApplyUpdate(client.EncodeStateAsUpdateV1(room.EncodeStateVectorV1()));

        Assert.AreEqual((true, "offline"), (accepted, room.GetText("other").ToString()));
    }

    [TestMethod]
    public void ApplyUpdate_WaitingStructsOverTheLimit_AnUpdateWithoutThemIsAccepted()
    {
        var b = Waiting(7, 150);
        var c = Waiting(8, 150);
        var e = Waiting(9, 290);
        var room = new YDoc(new YDocOptions { Limits = Small });
        room.ApplyUpdateV1(b.Waiting);
        room.ApplyUpdateV1(c.Waiting);
        room.ApplyUpdateV1(UpdateOperations.MergeUpdates([b.Missing, e.Waiting]));
        var other = new YDoc(new YDocOptions { ClientId = 21 });

        var accepted = room.TryApplyUpdate(HostileUpdates.Edit(other, d => d.GetText("other").Insert(0, "y")));

        Assert.IsTrue(accepted);
    }

    // Nested types deeper than MaxDepth, waiting for a struct of their client: when it arrives, the document takes it
    // and drops the nested types without an error (no PartlyApplied, nothing left waiting), while every client that
    // joined meanwhile got them as waiting structs and, with other limits (Yjs has none), integrates them.
    [TestMethod]
    [Ignore("ycs: nested types beyond MaxDepth that waited are dropped without an error when what they wait for arrives")]
    public void ApplyUpdate_WaitingTypesDeeperThanTheLimitResolved_AreRefusedOrKept()
    {
        var forger = new YDoc(new YDocOptions { ClientId = 7 });
        var missing = HostileUpdates.Edit(forger, d => d.GetArray("array").Insert(0, [1]));
        var deep = HostileUpdates.Edit(forger, d =>
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

        var accepted = room.TryApplyUpdate(missing);

        Assert.IsTrue(!accepted || (!room.HasPending && Convert.ToHexString(room.EncodeStateVectorV1()) == Convert.ToHexString(forger.EncodeStateVectorV1())));
    }
}
