using Ycs.Server.Rooms;
using Ycs.Server.Tests.Support;

namespace Ycs.Server.Tests;

/// <summary>Sync messages a hostile client sends, one case each.</summary>
[TestClass]
public class HostileSyncTests
{
    private const long HonestA = 1L << 40;
    private const long HonestB = (1L << 40) + 1;

    private static Session Join(Room room)
    {
        var session = new Session(room);
        session.Start();
        return session;
    }

    private static (string, string, string) Views(Room room, TestClient a, TestClient b) =>
        (Messages.TextOf(room.EncodeStateAsUpdate()), a.Text, b.Text);

    [TestMethod]
    public void Receive_DeleteOfARangeNotWrittenYet_ReachesTheClientThatWritesIt()
    {
        var room = new Room("r");
        var a = new TestClient(HonestA);
        var b = new TestClient(HonestB);
        a.Connect(room);
        b.Connect(room);
        TestClient.PumpAll(a, b);
        var hostile = Join(room);
        hostile.Receive(Messages.Update(HostileUpdates.Delete(HonestA, 0, 3)));

        a.Insert(0, "abcdef");
        TestClient.PumpAll(a, b);

        Assert.AreEqual(("def", "def", "def"), Views(room, a, b));
    }

    [TestMethod]
    public void Receive_StructWaitingForAnotherClients_ReachesThatClientOnceItArrives()
    {
        var room = new Room("r");
        var a = new TestClient(HonestA);
        var b = new TestClient(HonestB);
        a.Connect(room);
        b.Connect(room);
        TestClient.PumpAll(a, b);

        // The hostile client foresees A's next struct: the same insert into an empty document by A's client id.
        var foreseen = new YDoc(new YDocOptions { ClientId = HonestA });
        foreseen.GetText("text").Insert(0, "abc");
        var forger = new YDoc(new YDocOptions { ClientId = 7 });
        forger.ApplyUpdateV1(foreseen.EncodeStateAsUpdateV1());
        var hostile = Join(room);
        hostile.Receive(Messages.Update(HostileUpdates.Edit(forger, d => d.GetText("text").Insert(3, "X"))));

        a.Insert(0, "abc");
        TestClient.PumpAll(a, b);

        Assert.AreEqual(("abcX", "abcX", "abcX"), Views(room, a, b));
    }

    [TestMethod]
    public void Receive_UpdateBeyondTheIntegrationSteps_ClosesItsSessionAndEveryoneHasThePartApplied()
    {
        var room = new Room("r", null, new UpdateLimits { MaxIntegrationSteps = 10_000 });
        var a = new TestClient(HonestA);
        var b = new TestClient(HonestB);
        a.Connect(room);
        b.Connect(room);
        TestClient.PumpAll(a, b);
        // Typed backwards, every character is its own item; each of the second run passes all of the first: n * m steps.
        var first = new YDoc(new YDocOptions { ClientId = 100 });
        var second = new YDoc(new YDocOptions { ClientId = 200 });
        var hostile = Join(room);
        hostile.Receive(Messages.Update(HostileUpdates.Edit(first, d =>
        {
            for (var i = 0; i < 150; i++)
            {
                d.GetText("text").Insert(0, "f");
            }
        })));

        hostile.Receive(Messages.Update(HostileUpdates.Edit(second, d =>
        {
            for (var i = 0; i < 150; i++)
            {
                d.GetText("text").Insert(0, "b");
            }
        })));
        TestClient.PumpAll(a, b);

        var text = Messages.TextOf(room.EncodeStateAsUpdate());
        Assert.AreEqual(
            (SessionCloseReason.ProtocolError, true, 150 + 66, text, text, Convert.ToHexString(room.EncodeStateVector()), Convert.ToHexString(room.EncodeStateVector())),
            (hostile.CloseReason, text.Contains('b', StringComparison.Ordinal), text.Length, a.Text, b.Text, Convert.ToHexString(a.Doc.EncodeStateVectorV1()), Convert.ToHexString(b.Doc.EncodeStateVectorV1())));
    }

    // Yjs 13.6.32 gives the same two texts: the item is placed one way when it arrives after the run it points into,
    // another when it is read with that run from one update. Only a crafted update has such an item.
    [TestMethod]
    public void Receive_ItemWhoseRightOriginPrecedesItsOrigin_AJoinerSeesAnotherText()
    {
        var room = new Room("r");
        var a = new TestClient(HonestA);
        a.Connect(room);
        a.Insert(0, "abcdef");
        TestClient.PumpAll(a);
        var hostile = Join(room);

        // Client 2 inserts "z" with origin (A, 4) and right origin (A, 1).
        var crafted = new MemoryStream();
        foreach (var value in new ulong[] { 1, 1, 2, 0 })
        {
            HostileUpdates.WriteVarUint(crafted, value);
        }

        crafted.WriteByte(0xC4);
        foreach (var value in new ulong[] { (ulong)HonestA, 4, (ulong)HonestA, 1, 1 })
        {
            HostileUpdates.WriteVarUint(crafted, value);
        }

        crafted.Write("z\0"u8);
        hostile.Receive(Messages.Update(crafted.ToArray()));
        TestClient.PumpAll(a);
        var joiner = new TestClient(HonestB);
        joiner.Connect(room);
        TestClient.PumpAll(a, joiner);

        Assert.AreEqual(("azbcdef", "abcdezf"), (a.Text, joiner.Text));
    }

    // After a restart the room is empty, and a client's step 2 is its whole document.
    [TestMethod]
    public void Receive_DocumentOfMoreStructsThanYcsAllowsPerUpdate_IsAccepted()
    {
        var structs = (int)UpdateLimits.Default.MaxStructsPerUpdate + 1;
        var room = new Room("r");
        var client = new TestClient(HonestA);
        client.Doc.Transact(_ =>
        {
            var text = client.Doc.GetText("text");
            for (var i = 0; i < structs; i++)
            {
                text.Insert(0, "x");
            }
        });

        client.Connect(room);
        TestClient.PumpAll(client);

        var copy = new YDoc();
        copy.ApplyUpdateV1(room.EncodeStateAsUpdate(), null, UpdateLimits.Unlimited);
        Assert.AreEqual((SessionCloseReason.None, structs), (client.Session!.CloseReason, copy.GetText("text").Length));
    }
}
