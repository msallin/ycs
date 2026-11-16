using Ycs.Server.Rooms;

namespace Ycs.Server.Tests.Support;

/// <summary>
/// A y-websocket client in memory: a <see cref="YDoc"/> that sends its local updates to its session and answers
/// what the session queues, when the test pumps it. A disconnected client keeps editing offline.
/// </summary>
internal sealed class TestClient
{
    public TestClient(long clientId)
    {
        Doc = new YDoc(new YDocOptions { ClientId = clientId });
        Doc.Updated += (_, e) =>
        {
            if (!ReferenceEquals(e.Origin, this) && Session is { IsClosed: false })
            {
                Session.Receive(Messages.Update(e.Update.ToArray()));
            }
        };
    }

    public YDoc Doc { get; }

    public Session? Session { get; private set; }

    public string Text => Doc.GetText("text").ToString();

    public void Insert(int index, string text) => Doc.GetText("text").Insert(index, text);

    /// <summary>Opens a session and sends step 1, as the y-websocket provider does on open.</summary>
    public Session Connect(Room room, int queueCapacity = Session.DefaultQueueCapacity)
    {
        Session = new Session(room, queueCapacity);
        Session.Start();
        Session.Receive(Messages.Step1(Doc));
        return Session;
    }

    public void Disconnect()
    {
        Session?.Close();
        Session = null;
    }

    /// <summary>Handles every message queued for this client; returns how many there were.</summary>
    public int Pump()
    {
        var count = 0;
        while (Session is { } session && session.Outgoing.TryRead(out var message))
        {
            count++;
            var reader = new MemoryStream(message);
            Assert.AreEqual(MessageType.Sync, VarUint.Read(reader));
            if (Messages.HandleSync(reader, Doc, this) is { } answer)
            {
                session.Receive(answer);
            }
        }

        return count;
    }

    /// <summary>Pumps the clients until none has a message left.</summary>
    public static void PumpAll(params TestClient[] clients)
    {
        while (clients.Sum(c => c.Pump()) > 0)
        {
        }
    }
}
