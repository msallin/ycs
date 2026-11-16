using Ycs.Server.Rooms;
using Ycs.Server.Tests.Support;

namespace Ycs.Server.Tests;

/// <summary>Awareness messages a hostile or careless client sends, one case each.</summary>
[TestClass]
public class HostileAwarenessTests
{
    private static Session Join(Room room)
    {
        var session = new Session(room);
        session.Start();
        return session;
    }

    [TestMethod]
    public void Close_StateSetAgainAfterItsOwnRemoval_RemovesIt()
    {
        var room = new Room("r");
        var session = Join(room);
        session.Receive(Messages.Awareness(5, 1, "{}"));
        session.Receive(Messages.Awareness(5, 2, null));
        session.Receive(Messages.Awareness(5, 3, "{\"back\":true}"));

        session.Close();

        Assert.AreEqual(0, room.AwarenessClientIds.Count);
    }

    [TestMethod]
    public void Close_StateSetAgainAfterTheTimeout_RemovesIt()
    {
        var clock = new Server.Awareness.AwarenessClock();
        var room = new Room("r", clock);
        var session = Join(room);
        session.Receive(Messages.Awareness(5, 1, "{}"));
        clock.Advance(TimeSpan.FromSeconds(31));
        room.RemoveOutdatedAwareness();
        session.Receive(Messages.Awareness(5, 2, "{}"));

        session.Close();

        Assert.AreEqual(0, room.AwarenessClientIds.Count);
    }

    [TestMethod]
    public void Close_StateOfAnotherSessionsClientSetAgain_RemovesIt()
    {
        var room = new Room("r");
        var honest = Join(room);
        var hostile = Join(room);
        honest.Receive(Messages.Awareness(5, 1, "{\"name\":\"honest\"}"));
        hostile.Receive(Messages.Awareness(5, 2, null));
        honest.Close();
        hostile.Receive(Messages.Awareness(5, 3, "{\"name\":\"impostor\"}"));

        hostile.Close();

        Assert.AreEqual(0, room.AwarenessClientIds.Count);
    }
}
