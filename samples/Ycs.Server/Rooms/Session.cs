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

    /// <summary>
    /// The bytes the queue may hold before a message is refused: 8 MiB, eight times what a y-websocket client typing
    /// sends in a minute, and more than a step 2 of most documents. A message is queued while the queue holds less,
    /// so a step 2 larger than the budget still reaches a client that reads.
    /// </summary>
    public const long DefaultQueueBytes = 8L << 20;

    private readonly Room room;
    private readonly Channel<byte[]> outgoing;
    private readonly long queueBytes;
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long queuedBytes;
    private byte[]? unreadReply;
    private int closeReason;
    private int started;

    public Session(Room room, int queueCapacity = DefaultQueueCapacity, long queueBytes = DefaultQueueBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(queueCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(queueBytes, 1);
        this.room = room;
        this.queueBytes = queueBytes;
        outgoing = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        Outgoing = new OutgoingReader(this);
    }

    /// <summary>
    /// The messages to send, in order. It completes when the session closes; what was queued before stays readable,
    /// and the transport may send or drop it. A message array may be shared between sessions: never modify it.
    /// </summary>
    public ChannelReader<byte[]> Outgoing { get; }

    /// <summary>Completes when the session closes, before <see cref="Outgoing"/> is drained.</summary>
    public Task Closed => closed.Task;

    /// <summary>
    /// What closed the session with <see cref="SessionCloseReason.ProtocolError"/>. Ycs' and the awareness decoder's
    /// messages name the limit or the part of the message that failed, never the content.
    /// </summary>
    public Exception? CloseCause { get; private set; }

    /// <summary>Whether the answer to the last step 1 is still queued; the room sets it under its lock.</summary>
    internal bool ReplyUnread => Volatile.Read(ref unreadReply) is not null;

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
            var type = SyncProtocol.ReadMessageType(message, out var body);
            switch (type)
            {
                case MessageType.Sync:
                    room.ReceiveSync(this, body);
                    break;
                case MessageType.Awareness:
                    room.ReceiveAwareness(this, new MemoryStream(message, message.Length - body.Length, body.Length, writable: false));
                    break;
                case MessageType.QueryAwareness:
                    // y-websocket's server ignores it (its messageListener has no case for it); clients never send it.
                    break;
                default:
                    Close(SessionCloseReason.ProtocolError, new YUpdateException($"Unknown message type {type}."));
                    break;
            }
        }
        catch (Exception e)
        {
            // Whatever the read or the apply threw, from a malformed message to an exceeded limit: Ycs leaves the
            // document as it was (or applied in part, see YUpdateException.PartlyApplied, and broadcast that part).
            Close(SessionCloseReason.ProtocolError, e);
        }
    }

    public void Close() => Close(SessionCloseReason.Disconnected);

    public void Dispose() => Close();

    /// <summary>
    /// Queues a message without blocking; the room calls it under its lock and closes the session with
    /// <see cref="SessionCloseReason.QueueFull"/> when it returns false: the queue holds its capacity of messages, or
    /// its budget of bytes, or the session closed.
    /// </summary>
    internal bool TryEnqueue(byte[] message)
    {
        if (Volatile.Read(ref queuedBytes) >= queueBytes)
        {
            return false;
        }

        // Counted before the write, so that a reader that takes it at once never counts below zero.
        Interlocked.Add(ref queuedBytes, message.Length);
        if (outgoing.Writer.TryWrite(message))
        {
            return true;
        }

        Interlocked.Add(ref queuedBytes, -message.Length);
        return false;
    }

    /// <summary>Queues the answer to a step 1; <see cref="ReplyUnread"/> until the transport has taken it.</summary>
    internal bool TryEnqueueReply(byte[] message)
    {
        Volatile.Write(ref unreadReply, message);
        return TryEnqueue(message);
    }

    internal void Close(SessionCloseReason reason, Exception? cause = null)
    {
        if (Interlocked.CompareExchange(ref closeReason, (int)reason, (int)SessionCloseReason.None) != (int)SessionCloseReason.None)
        {
            return;
        }

        CloseCause = cause;
        room.Leave(this);
        outgoing.Writer.TryComplete();
        closed.TrySetResult();
    }

    private void Dequeued(byte[] message)
    {
        Interlocked.Add(ref queuedBytes, -message.Length);
        Interlocked.CompareExchange(ref unreadReply, null, message);
    }

    // The channel's reader, counting what leaves the queue.
    private sealed class OutgoingReader(Session session) : ChannelReader<byte[]>
    {
        private readonly ChannelReader<byte[]> inner = session.outgoing.Reader;

        public override Task Completion => inner.Completion;

        public override bool CanCount => true;

        public override int Count => inner.Count;

        public override bool CanPeek => true;

        public override bool TryRead(out byte[] item)
        {
            if (!inner.TryRead(out item!))
            {
                return false;
            }

            session.Dequeued(item);
            return true;
        }

        public override bool TryPeek(out byte[] item) => inner.TryPeek(out item!);

        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) => inner.WaitToReadAsync(cancellationToken);
    }
}
