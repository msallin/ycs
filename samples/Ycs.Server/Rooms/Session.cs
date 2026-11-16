using System.Threading.Channels;

namespace Ycs.Server.Rooms;

/// <summary>
/// One client connection, without its transport: the transport calls <see cref="Start"/> on connect,
/// <see cref="Receive"/> for each binary message, and <see cref="Close"/> on disconnect, and sends what
/// <see cref="Outgoing"/> yields, one binary message each, until it completes. Then <see cref="CloseReason"/> says
/// why. <see cref="Receive"/> of one session must not run concurrently with itself (a transport reads one message at
/// a time); different sessions may call in from any thread.
/// </summary>
internal sealed class Session : IDisposable
{
    public const int DefaultQueueCapacity = 1024;

    private readonly Room room;
    private readonly Channel<byte[]> outgoing;
    private int closeReason;
    private int started;

    public Session(Room room, int queueCapacity = DefaultQueueCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(queueCapacity, 1);
        this.room = room;
        outgoing = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>
    /// The messages to send, in order. It completes when the session closes; what was queued before stays readable,
    /// and the transport may send or drop it. A message array may be shared between sessions: never modify it.
    /// </summary>
    public ChannelReader<byte[]> Outgoing => outgoing.Reader;

    public SessionCloseReason CloseReason => (SessionCloseReason)Volatile.Read(ref closeReason);

    public bool IsClosed => CloseReason != SessionCloseReason.None;

    /// <summary>
    /// The awareness client ids this session's messages set a state for and did not remove: the room removes their states
    /// when the session leaves. Only the room touches it, under its lock.
    /// </summary>
    internal HashSet<ulong> ControlledIds { get; } = [];

    /// <summary>Joins the room and queues sync step 1 with the room's state vector, as y-websocket's server does.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref started, 1) == 1)
        {
            throw new InvalidOperationException("The session has already started.");
        }

        if (!IsClosed)
        {
            room.Join(this);
        }
    }

    /// <summary>
    /// Handles one message. A message the session cannot handle closes it with
    /// <see cref="SessionCloseReason.ProtocolError"/>; the document is then as it was. After the close, messages are ignored.
    /// </summary>
    public void Receive(byte[] message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (Volatile.Read(ref started) == 0)
        {
            throw new InvalidOperationException("Start the session before it receives messages.");
        }

        if (IsClosed)
        {
            return;
        }

        try
        {
            var reader = new MemoryStream(message, writable: false);
            var type = VarUint.Read(reader);
            switch (type)
            {
                case MessageType.Sync:
                    room.ReceiveSync(this, reader);
                    break;
                case MessageType.Awareness:
                    room.ReceiveAwareness(this, reader);
                    break;
                case MessageType.QueryAwareness:
                    // y-websocket's server ignores it (its messageListener has no case for it); clients never send it.
                    break;
                default:
                    Close(SessionCloseReason.ProtocolError);
                    break;
            }
        }
        catch (Exception)
        {
            // Whatever the read or the apply threw, from a malformed message to an exceeded limit: Ycs leaves the
            // document as it was, and the client resyncs if it reconnects.
            Close(SessionCloseReason.ProtocolError);
        }
    }

    public void Close() => Close(SessionCloseReason.Disconnected);

    public void Dispose() => Close();

    /// <summary>
    /// Queues a message without blocking; the room calls it under its lock. A full queue closes the session.
    /// </summary>
    internal void Enqueue(byte[] message)
    {
        if (!outgoing.Writer.TryWrite(message))
        {
            Close(SessionCloseReason.QueueFull);
        }
    }

    private void Close(SessionCloseReason reason)
    {
        if (Interlocked.CompareExchange(ref closeReason, (int)reason, (int)SessionCloseReason.None) != (int)SessionCloseReason.None)
        {
            return;
        }

        room.Leave(this);
        outgoing.Writer.TryComplete();
    }
}
