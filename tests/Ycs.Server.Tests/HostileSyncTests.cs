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
}
