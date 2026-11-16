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

    [TestMethod]
    public void Close_StateRemovedAndSetAgainInOneMessage_RemovesIt()
    {
        var room = new Room("r");
        var session = Join(room);
        session.Receive(Messages.Awareness(5, 1, "{}"));
        session.Receive(HostileMessages.Awareness((5, 2, "{}"), (5, 3, null), (5, 4, "{\"back\":true}")));

        session.Close();

        Assert.AreEqual(0, room.AwarenessClientIds.Count);
    }

    [TestMethod]
    public void Receive_MessageOfMoreClientsThanAllowed_ClosesTheSessionAndSetsNothing()
    {
        var room = new Room("r");
        var session = Join(room);

        session.Receive(HostileMessages.Awareness([.. Enumerable.Range(1, Room.MaxAwarenessClientsPerSession + 1).Select(i => ((ulong)i, 1UL, (string?)"{}"))]));

        Assert.AreEqual((SessionCloseReason.ProtocolError, 0), (session.CloseReason, room.AwarenessClientIds.Count));
    }

    [TestMethod]
    public void Receive_MoreClientsThanAllowedOverSeveralMessages_ClosesTheSessionAndRemovesItsStates()
    {
        var room = new Room("r");
        var session = Join(room);
        for (var i = 1; i <= Room.MaxAwarenessClientsPerSession; i++)
        {
            session.Receive(Messages.Awareness((ulong)i, 1, "{}"));
        }

        session.Receive(Messages.Awareness(1_000, 1, "{}"));

        Assert.AreEqual((SessionCloseReason.ProtocolError, 0), (session.CloseReason, room.AwarenessClientIds.Count));
    }

    [TestMethod]
    public void Receive_ClientsSetAndRemovedInTurn_StaysOpen()
    {
        var room = new Room("r");
        var session = Join(room);

        for (var i = 1; i <= 4 * Room.MaxAwarenessClientsPerSession; i++)
        {
            session.Receive(Messages.Awareness((ulong)i, 1, "{}"));
            session.Receive(Messages.Awareness((ulong)i, 2, null));
        }

        Assert.AreEqual(SessionCloseReason.None, session.CloseReason);
    }

    [TestMethod]
    public void RemoveOutdated_ClientWithoutStateForLongerThanTheTimeout_IsForgotten()
    {
        var clock = new Server.Awareness.AwarenessClock();
        var awareness = new Server.Awareness.Awareness(clock);
        awareness.ApplyUpdate(Messages.AwarenessUpdate(Messages.Awareness(1, 5, "{}")));
        awareness.RemoveStates([1]);
        clock.Advance(TimeSpan.FromMilliseconds(Server.Awareness.Awareness.OutdatedTimeoutMs));

        awareness.RemoveOutdated();

        Assert.IsFalse(awareness.TryGetClock(1, out _));
    }

    [TestMethod]
    public void RemoveOutdated_ClientWithoutStateWithinTheTimeout_KeepsItsClock()
    {
        var clock = new Server.Awareness.AwarenessClock();
        var awareness = new Server.Awareness.Awareness(clock);
        awareness.ApplyUpdate(Messages.AwarenessUpdate(Messages.Awareness(1, 5, "{}")));
        awareness.RemoveStates([1]);
        clock.Advance(TimeSpan.FromMilliseconds(Server.Awareness.Awareness.OutdatedTimeoutMs - 1));

        awareness.RemoveOutdated();

        Assert.IsTrue(awareness.TryGetClock(1, out _));
    }

    [TestMethod]
    public void RemoveOutdated_StateTimedOutInThisSweep_KeepsItsClockForTheBroadcast()
    {
        var clock = new Server.Awareness.AwarenessClock();
        var awareness = new Server.Awareness.Awareness(clock);
        awareness.ApplyUpdate(Messages.AwarenessUpdate(Messages.Awareness(1, 5, "{}")));
        clock.Advance(TimeSpan.FromMilliseconds(Server.Awareness.Awareness.OutdatedTimeoutMs));

        var change = awareness.RemoveOutdated();

        Assert.AreEqual((1, true), (change.Removed.Count, awareness.TryGetClock(1, out _)));
    }
}
