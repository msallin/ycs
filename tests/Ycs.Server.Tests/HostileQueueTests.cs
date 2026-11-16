using Ycs.Server.Rooms;
using Ycs.Server.Tests.Support;

namespace Ycs.Server.Tests;

/// <summary>Clients that do not read, or ask for the whole document again and again.</summary>
[TestClass]
public class HostileQueueTests
{
    private static Session Join(Room room, int queueCapacity = Session.DefaultQueueCapacity, long queueBytes = Session.DefaultQueueBytes)
    {
        var session = new Session(room, queueCapacity, queueBytes);
        session.Start();
        return session;
    }

    [TestMethod]
    public void Enqueue_QueuedBytesBeyondTheBudget_ClosesTheSessionQueueFull()
    {
        var room = new Room("r");
        var writer = new TestClient(1);
        writer.Connect(room);
        var stalled = Join(room, queueBytes: 1_000);

        for (var i = 0; i < 3; i++)
        {
            writer.Insert(0, new string('x', 600));
        }

        Assert.AreEqual((SessionCloseReason.QueueFull, 1), (stalled.CloseReason, room.SessionCount));
    }

    [TestMethod]
    public void Enqueue_OneMessageLargerThanTheBudget_IsQueued()
    {
        var room = new Room("r");
        var writer = new TestClient(1);
        writer.Connect(room);
        var reader = Join(room, queueBytes: 100);
        Messages.Drain(reader);

        writer.Insert(0, new string('x', 600));

        Assert.AreEqual((SessionCloseReason.None, 1), (reader.CloseReason, Messages.Drain(reader).Count));
    }

    [TestMethod]
    public void Outgoing_ReadMessages_FreeTheBudget()
    {
        var room = new Room("r");
        var writer = new TestClient(1);
        writer.Connect(room);
        var reader = Join(room, queueBytes: 1_000);

        for (var i = 0; i < 10; i++)
        {
            writer.Insert(0, new string('x', 600));
            Messages.Drain(reader);
        }

        Assert.AreEqual(SessionCloseReason.None, reader.CloseReason);
    }

    [TestMethod]
    public void Close_AnotherSessionsQueueFull_TheOthersStillGetTheMessage()
    {
        var room = new Room("r");
        var writer = new TestClient(1);
        writer.Connect(room);
        var stalled = Join(room, queueCapacity: 1);
        var reader = Join(room);
        Messages.Drain(reader);

        writer.Insert(0, "x");

        Assert.AreEqual((SessionCloseReason.QueueFull, 1), (stalled.CloseReason, Messages.Drain(reader).Count));
    }

    [TestMethod]
    public void Receive_Step1WhileTheLastStep2IsStillQueued_ClosesTheSession()
    {
        var room = new Room("r");
        var session = Join(room);
        var step1 = Messages.Step1(new YDoc());
        session.Receive(step1);

        session.Receive(step1);

        Assert.AreEqual(SessionCloseReason.ProtocolError, session.CloseReason);
    }

    [TestMethod]
    public void Receive_Step1AfterTheStep2WasRead_IsAnswered()
    {
        var room = new Room("r");
        var session = Join(room);
        var step1 = Messages.Step1(new YDoc());
        session.Receive(step1);
        Messages.Drain(session);

        session.Receive(step1);

        Assert.AreEqual((SessionCloseReason.None, SyncProtocol.MessageYjsSyncStep2), (session.CloseReason, Messages.SyncType(Messages.Drain(session).Single())));
    }

    [TestMethod]
    public void Receive_ProtocolError_KeepsTheCauseWithoutTheMessage()
    {
        var room = new Room("r");
        var session = Join(room);

        session.Receive(Messages.Sync(s => s.WriteByte(9)));

        Assert.AreEqual((SessionCloseReason.ProtocolError, nameof(YUpdateException)), (session.CloseReason, session.CloseCause?.GetType().Name));
    }

    // Each full queue closes its session after the fan-out, not inside it, where every close nested one level deeper
    // (each leave broadcasts a removal to the other full queues): on a thread with a small stack, many sessions whose
    // queues are full and who all have awareness states leave in one fan-out.
    [TestMethod]
    public void Receive_ManyFullQueuesWithAwareness_CloseWithoutNesting()
    {
        const int Stalled = 1_000;
        var room = new Room("r");
        var writer = Join(room, queueCapacity: 4 * Stalled);
        var stalled = new List<Session>();
        for (var i = 0; i < Stalled; i++)
        {
            var session = Join(room, queueCapacity: 2);
            Messages.Drain(session);
            stalled.Add(session);
            session.Receive(Messages.Awareness((ulong)(i + 1), 1, "{}"));
            foreach (var other in stalled.Append(writer))
            {
                Messages.Drain(other);
            }
        }

        foreach (var session in stalled)
        {
            session.TryEnqueue([0]);
            session.TryEnqueue([0]);
        }

        var thread = new Thread(() => writer.Receive(Messages.Awareness(100_000, 1, "{}")), 256 * 1024);
        thread.Start();
        thread.Join();

        Assert.AreEqual((0, 1), (stalled.Count(s => !s.IsClosed), room.AwarenessClientIds.Count));
    }
}
