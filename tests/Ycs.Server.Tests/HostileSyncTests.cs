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
        forger.ApplyUpdate(foreseen.EncodeStateAsUpdate());
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
            (hostile.CloseReason, text.Contains('b', StringComparison.Ordinal), text.Length, a.Text, b.Text, Convert.ToHexString(a.Doc.EncodeStateVector()), Convert.ToHexString(b.Doc.EncodeStateVector())));
    }

    // Yjs 13.6.32 applies it and places the item one way when it arrives after the run it points into, another when it
    // is read with that run from one update, so a joiner sees another text. Ycs shares this with Yjs (a check on one
    // struct is bypassed by the merge of two structs or by an offset, and leaves a room that no joiner can load: docs/findings-server.md).
    // This test pins the divergence. Only a crafted update has such an item.
    [TestMethod]
    public void Receive_ItemWhoseRightOriginPrecedesItsOrigin_IsAppliedAndAJoinerSeesAnotherText()
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

        Assert.AreEqual((SessionCloseReason.None, "azbcdef", "abcdezf"), (hostile.CloseReason, a.Text, joiner.Text));
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
        copy.ApplyUpdate(room.EncodeStateAsUpdate(), null, limits: UpdateLimits.Unlimited);
        Assert.AreEqual((SessionCloseReason.None, structs), (client.Session!.CloseReason, copy.GetText("text").Length));
    }

    // Two structs under one id, the first waiting for a client nobody sends: Yjs 13.6.32 keeps it waiting, and the
    // structs behind it, also once the second one is integrated, so a client that joins is ahead of the room for
    // good. Ycs applies the second and drops what waited (reported by PendingDropped, the session stays open); a joiner has what
    // the room has. Only a crafted update reuses an id.
    [TestMethod]
    public void Receive_TwoStructsUnderOneId_DropsWhatWaitedAndAJoinerHasWhatTheRoomHas()
    {
        var room = new Room("r");
        var a = new TestClient(HonestA);
        a.Connect(room);
        a.Insert(0, "ab");
        TestClient.PumpAll(a);
        var unknown = new YDoc(new YDocOptions { ClientId = 999 });
        unknown.ApplyUpdate(a.Doc.EncodeStateAsUpdate());
        unknown.GetText("text").Insert(1, "z");
        var waiting = new YDoc(new YDocOptions { ClientId = 7 });
        waiting.ApplyUpdate(unknown.EncodeStateAsUpdate());
        var first = HostileUpdates.Edit(waiting, d => d.GetText("text").Insert(1, "u"));
        var behind = HostileUpdates.Edit(waiting, d => d.GetMap("map")["k"] = 1);
        var integrating = new YDoc(new YDocOptions { ClientId = 7 });
        integrating.ApplyUpdate(a.Doc.EncodeStateAsUpdate());
        var second = HostileUpdates.Edit(integrating, d => d.GetText("text").Insert(1, "u"));
        var hostile = Join(room);
        hostile.Receive(Messages.Update(behind));
        hostile.Receive(Messages.Update(first));
        hostile.Receive(Messages.Update(second));

        var joiner = new TestClient(HonestB);
        joiner.Connect(room);
        TestClient.PumpAll(a, joiner);

        Assert.AreEqual(
            (SessionCloseReason.None, 1UL, 1UL),
            (hostile.CloseReason, Clock(room.EncodeStateVector(), 7), Clock(joiner.Doc.EncodeStateVector(), 7)));
    }

    private static ulong Clock(byte[] vector, ulong client)
    {
        var reader = new MemoryStream(vector);
        var count = VarUint.Read(reader);
        for (var i = 0; i < count; i++)
        {
            var (id, clock) = (ReadVarUint64(reader), ReadVarUint64(reader));
            if (id == client)
            {
                return clock;
            }
        }

        return 0;
    }

    private static ulong ReadVarUint64(Stream stream)
    {
        ulong value = 0;
        for (var shift = 0; ; shift += 7)
        {
            var b = stream.ReadByte();
            value |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80)
            {
                return value;
            }
        }
    }
}
