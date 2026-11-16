using System.Buffers;
using Ycs.Server.Rooms;

namespace Ycs.Server.Tests.Support;

/// <summary>
/// An honest client of a hostile room: a <see cref="TestClient"/> that also keeps the awareness states it is told
/// about, as a y-websocket provider does, and announces its own. Or, with <see cref="Listen"/>, a client that never
/// sends: it holds exactly what the room broadcast, so the room's integrated state without anything pending.
/// </summary>
internal sealed class HostilePeer
{
    private readonly TimeProvider time;
    private ulong awarenessClock;

    public HostilePeer(long clientId, TimeProvider time)
    {
        this.time = time;
        Client = new TestClient(clientId);
        View = new Server.Awareness.Awareness(time);
    }

    public TestClient Client { get; }

    public YDoc Doc => Client.Doc;

    public ulong ClientId => (ulong)Client.Doc.ClientId;

    /// <summary>The awareness states as this client sees them; a fresh view on every connect, as the provider's.</summary>
    public Server.Awareness.Awareness View { get; private set; }

    public Session? Session => Client.Session ?? Listening;

    public bool Connected => Session is { IsClosed: false };

    private Session? Listening { get; set; }

    public void Connect(Room room)
    {
        View = new Server.Awareness.Awareness(time);
        Client.Connect(room);
        Announce();
    }

    /// <summary>Joins without a step 1: the room then never sends a step 2, only what it broadcasts.</summary>
    public void Listen(Room room)
    {
        Listening = new Session(room);
        Listening.Start();
    }

    public void Disconnect() => Client.Disconnect();

    /// <summary>Sends this client's awareness state with the next clock.</summary>
    public void Announce()
    {
        if (Client.Session is { IsClosed: false } session)
        {
            session.Receive(Messages.Awareness(ClientId, ++awarenessClock, $"{{\"user\":{ClientId}}}"));
        }
    }

    /// <summary>Handles what the room queued for this client; returns how many messages there were.</summary>
    public int Pump()
    {
        var count = 0;
        while (Session is { } session && session.Outgoing.TryRead(out var message))
        {
            count++;
            var (type, reader) = Messages.Split(message);
            switch (type)
            {
                case MessageType.Sync when Listening is null:
                    if (Messages.HandleSync(reader, Doc, Client) is { } answer)
                    {
                        session.Receive(answer);
                    }

                    break;
                case MessageType.Sync:
                    var sync = reader.ToArray().AsSpan((int)reader.Position);
                    if (VarUint.Peek(sync) == (uint)SyncMessageType.Update)
                    {
                        SyncProtocol.ReadSyncMessage(sync, new ArrayBufferWriter<byte>(), Doc, Client);
                    }

                    break;
                case MessageType.Awareness:
                    View.ApplyUpdate(Messages.AwarenessUpdate(message));
                    break;
                default:
                    throw new InvalidOperationException($"The room sent a message of type {type}.");
            }
        }

        return count;
    }

    /// <summary>The awareness states this client sees, as "id=json" in order of id.</summary>
    public string States() => string.Join(';', View.ClientIds.Order().Select(id => $"{id}={(View.TryGetState(id, out var s) ? s : "?")}"));
}
