using Ycs.Server.Awareness;
using Ycs.Server.Rooms;
using Ycs.Server.Tests.Support;

namespace Ycs.Server.Tests;

[TestClass]
public class RoomAwarenessTests
{
    private static Session Join(Room room)
    {
        var session = new Session(room);
        session.Start();
        return session;
    }

    private static List<byte[]> AwarenessMessages(Session session) =>
        [.. Messages.Drain(session).Where(m => Messages.Split(m).Type == MessageType.Awareness)];

    private static string Format((ulong Id, string? State)[] entries) =>
        string.Join(";", entries.Select(e => $"{e.Id}={e.State ?? "null"}"));

    [TestMethod]
    public void Join_NoStates_GetsOnlySyncStep1()
    {
        var room = new Room("r");

        var session = Join(room);

        Assert.AreEqual(1, Messages.Drain(session).Count);
    }

    [TestMethod]
    public void Join_StatesPresent_GetsThemAllInOneMessageAfterStep1()
    {
        var room = new Room("r");
        var a = Join(room);
        var b = Join(room);
        a.Receive(Messages.Awareness(10, 1, "{\"name\":\"a\"}"));
        b.Receive(Messages.Awareness(20, 1, "{\"name\":\"b\"}"));

        var joined = Join(room);

        var messages = Messages.Drain(joined);
        Assert.AreEqual(
            (2, MessageType.Sync, "10={\"name\":\"a\"};20={\"name\":\"b\"}"),
            (messages.Count, Messages.Split(messages[0]).Type, Format(Messages.AwarenessEntries(messages[1]))));
    }

    [TestMethod]
    public void ReceiveAwareness_Update_ReachesEverySessionIncludingTheSender()
    {
        var room = new Room("r");
        var sessions = new[] { Join(room), Join(room), Join(room) };
        foreach (var session in sessions)
        {
            Messages.Drain(session);
        }

        sessions[1].Receive(Messages.Awareness(7, 1, "{\"x\":1}"));

        var received = string.Join(" ", sessions.Select(s => string.Join("|", AwarenessMessages(s).Select(m => Format(Messages.AwarenessEntries(m))))));
        Assert.AreEqual("7={\"x\":1} 7={\"x\":1} 7={\"x\":1}", received);
    }

    [TestMethod]
    public void ReceiveAwareness_StaleClock_BroadcastsNothing()
    {
        var room = new Room("r");
        var session = Join(room);
        session.Receive(Messages.Awareness(7, 2, "{}"));
        Messages.Drain(session);

        session.Receive(Messages.Awareness(7, 1, "{\"old\":true}"));

        Assert.AreEqual(0, Messages.Drain(session).Count);
    }

    [TestMethod]
    public void ReceiveAwareness_LengthBeyondTheMessage_ClosesTheSessionWithProtocolError()
    {
        var room = new Room("r");
        var session = Join(room);
        var message = Messages.Awareness(7, 1, "{}");

        session.Receive(message[..^1]);

        Assert.AreEqual(SessionCloseReason.ProtocolError, session.CloseReason);
    }

    [TestMethod]
    public void Close_RemovesExactlyThatSessionsIdsAndTellsTheOthers()
    {
        var room = new Room("r");
        var leaving = Join(room);
        var staying = Join(room);
        leaving.Receive(Messages.Awareness(1, 1, "{\"a\":1}"));
        leaving.Receive(Messages.Awareness(2, 1, "{\"a\":2}"));
        staying.Receive(Messages.Awareness(3, 1, "{\"b\":3}"));
        Messages.Drain(staying);

        leaving.Close();

        var removal = AwarenessMessages(staying).Single();
        Assert.AreEqual(
            ("1=null;2=null", "3"),
            (Format(Messages.AwarenessEntries(removal)), string.Join(",", room.AwarenessClientIds)));
    }

    [TestMethod]
    public void Close_IdTheSessionRemovedItself_IsNotRemovedAgain()
    {
        var room = new Room("r");
        var leaving = Join(room);
        var staying = Join(room);
        leaving.Receive(Messages.Awareness(1, 1, "{}"));
        leaving.Receive(Messages.Awareness(2, 1, "{}"));
        leaving.Receive(Messages.Awareness(1, 2, null));
        Messages.Drain(staying);

        leaving.Close();

        Assert.AreEqual("2=null", Format(Messages.AwarenessEntries(AwarenessMessages(staying).Single())));
    }

    [TestMethod]
    public void Close_SessionWithoutStates_BroadcastsNothing()
    {
        var room = new Room("r");
        var leaving = Join(room);
        var staying = Join(room);
        staying.Receive(Messages.Awareness(3, 1, "{}"));
        Messages.Drain(staying);

        leaving.Close();

        Assert.AreEqual(0, Messages.Drain(staying).Count);
    }

    [TestMethod]
    public void RemoveOutdatedAwareness_After30s_RemovesTheStaleStateAndTellsEverySession()
    {
        var clock = new AwarenessClock();
        var room = new Room("r", clock);
        var a = Join(room);
        var b = Join(room);
        a.Receive(Messages.Awareness(1, 1, "{}"));
        clock.Advance(TimeSpan.FromSeconds(20));
        b.Receive(Messages.Awareness(2, 1, "{}"));
        Messages.Drain(a);
        Messages.Drain(b);
        clock.Advance(TimeSpan.FromSeconds(10));

        room.RemoveOutdatedAwareness();

        var received = (Format(Messages.AwarenessEntries(AwarenessMessages(a).Single())), Format(Messages.AwarenessEntries(AwarenessMessages(b).Single())));
        Assert.AreEqual(("1=null", "1=null"), received);
    }

    [TestMethod]
    public void SweepOnce_RemovesOutdatedStatesInEveryRoom()
    {
        var clock = new AwarenessClock();
        var registry = new RoomRegistry(clock);
        var sweep = new AwarenessSweep(registry, clock);
        foreach (var name in new[] { "a", "b" })
        {
            Join(registry.GetOrCreate(name)).Receive(Messages.Awareness(1, 1, "{}"));
        }

        clock.Advance(TimeSpan.FromMilliseconds(Awareness.Awareness.OutdatedTimeoutMs));

        sweep.SweepOnce();

        Assert.AreEqual(0, registry.Rooms.Sum(r => r.AwarenessClientIds.Count));
    }
}
