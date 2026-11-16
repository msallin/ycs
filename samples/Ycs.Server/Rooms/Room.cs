using Ycs.Server.Awareness;

namespace Ycs.Server.Rooms;

/// <summary>
/// One document, its awareness states and the sessions editing it. <see cref="YDoc"/> and
/// <see cref="Awareness.Awareness"/> are not thread-safe: every read and write of <see cref="doc"/>,
/// <see cref="awareness"/>, <see cref="sessions"/> and of each session's controlled ids happens under
/// <see cref="gate"/>, and nothing under it blocks or awaits (a session's queue is only ever written with
/// <c>TryWrite</c>). A session whose queue is full is closed once the fan-out that found it is done, by the outermost
/// call under the lock (<see cref="CloseOverflowing"/>), so that closes do not nest; the lock is reentrant for them.
/// </summary>
internal sealed class Room
{
    private readonly Lock gate = new();
    private readonly YDoc doc;
    private readonly Awareness.Awareness awareness;

    // Under the lock: whether the update being applied may integrate what waited for it, which its origin has not seen.
    private bool updateReachesOrigin;

    // Copy-on-write under the lock, so that a fan-out iterates a snapshot while a full queue removes its session.
    private Session[] sessions = [];

    // Under the lock: sessions whose queue refused a message, to close after the fan-out; and whether they are being
    // closed, so that the leave of one adds the queues its removal fills instead of closing them nested.
    private readonly List<Session> overflowing = [];
    private bool closingOverflowing;

    public Room(string name, TimeProvider? time = null, UpdateLimits? limits = null)
    {
        Name = name;
        doc = new YDoc(new YDocOptions { Limits = limits ?? DocumentLimits });
        awareness = new Awareness.Awareness(time ?? TimeProvider.System);
        doc.Update += OnUpdate;
    }

    public string Name { get; }

    /// <summary>
    /// The document's limits: Ycs' defaults, but 2,097,152 structs per update instead of 131,072, because an update
    /// may be a whole document: a client's step 2 into a room that does not have its state (after a restart) is
    /// everything it has. A character typed at a time is a struct of about 7 bytes of v1, so a document that fits in
    /// one 16 MiB message (the transports' default) is about that many structs. One such update makes Ycs allocate
    /// about 560 MiB and keep about 480 MiB (16 times the measured cost of the default, docs/api.md), whether it is a
    /// document's state or a hostile client's.
    /// </summary>
    public static readonly UpdateLimits DocumentLimits = new() { MaxStructsPerUpdate = 1 << 21 };

    /// <summary>
    /// The most awareness clients one session may hold a state for, and name in one message. A y-websocket client
    /// sends one entry, its document's client id, and keeps it for the connection; Yjs gives a document a new id
    /// only when another client uses its id. y-protocols has no limit; without one a single message of a few bytes
    /// per id makes the room keep a clock per id and parse a JSON text per id under its lock.
    /// </summary>
    public const int MaxAwarenessClientsPerSession = 16;

    /// <summary>
    /// Adds the session and queues sync step 1 for it in one step, so that it misses no update in between.
    /// </summary>
    internal void Join(Session session)
    {
        lock (gate)
        {
            // A close that ran first has already left; Close sets the reason before it takes the lock in Leave.
            if (session.IsClosed)
            {
                return;
            }

            try
            {
                sessions = [.. sessions, session];
                var message = new MemoryStream();
                VarUint.Write(message, MessageType.Sync);
                SyncProtocol.WriteSyncStep1(message, doc);
                Send(session, message.ToArray());

                // As y-websocket's server: the states of everyone present, in one message, only if there are any.
                if (awareness.ClientIds.Count > 0)
                {
                    Send(session, AwarenessMessage([.. awareness.ClientIds]));
                }
            }
            finally
            {
                CloseOverflowing();
            }
        }
    }

    /// <summary>
    /// Removes the session and the awareness states it set, and tells the others that they are gone, as
    /// y-websocket's server does on close.
    /// </summary>
    internal void Leave(Session session)
    {
        lock (gate)
        {
            try
            {
                var i = Array.IndexOf(sessions, session);
                if (i < 0)
                {
                    return;
                }

                sessions = [.. sessions.AsSpan(0, i), .. sessions.AsSpan(i + 1)];
                var change = awareness.RemoveStates(session.ControlledIds);
                session.ControlledIds.Clear();
                Broadcast(change);
            }
            finally
            {
                CloseOverflowing();
            }
        }
    }

    /// <summary>
    /// Handles the awareness message after its outer type: a length-prefixed awareness update, applied with
    /// y-protocols' rules and broadcast to every session, <paramref name="session"/> included. The ids it sets a state
    /// for become the session's, those it removes stop being. Throws what reading or applying throws; nothing changes then.
    /// </summary>
    internal void ReceiveAwareness(Session session, Stream reader)
    {
        var length = VarUint.Read(reader);
        if (length > reader.Length - reader.Position)
        {
            throw new YUpdateException("The awareness message ends inside its update.");
        }

        var update = new byte[length];
        reader.ReadExactly(update);
        var entries = Awareness.Awareness.Decode(update, MaxAwarenessClientsPerSession);

        lock (gate)
        {
            try
            {
                ApplyAwareness(session, entries);
            }
            finally
            {
                CloseOverflowing();
            }
        }
    }

    // Under the lock.
    private void ApplyAwareness(Session session, List<Awareness.Awareness.Entry> entries)
    {
        // A session that left (closed from another thread) would set states nobody removes before they time out.
        if (Array.IndexOf(sessions, session) < 0)
        {
            return;
        }

        var added = entries.Where(e => e.State is not null && !session.ControlledIds.Contains(e.ClientId)).Select(e => e.ClientId).Distinct().Count();
        if (session.ControlledIds.Count + added > MaxAwarenessClientsPerSession)
        {
            throw new YUpdateException($"The session would set the awareness states of more than {MaxAwarenessClientsPerSession} clients.");
        }

        var change = awareness.Apply(entries);

        // By the state each id is left with, not by the change's lists: a message may set, remove and set one id
        // again, and a state set again after a removal or a timeout, or over another session's, is an update
        // (unlike y-websocket's server, which takes only the added ids).
        foreach (var id in change.Broadcast())
        {
            if (awareness.TryGetState(id, out _))
            {
                session.ControlledIds.Add(id);
            }
            else
            {
                session.ControlledIds.Remove(id);
            }
        }

        Broadcast(change);
    }

    /// <summary>
    /// Removes the awareness states not renewed for 30 s and tells every session. Unlike a close, the removed ids
    /// stay their sessions' controlled ids, as with y-protocols' timeout: a renewal brings the state back.
    /// </summary>
    internal void RemoveOutdatedAwareness()
    {
        lock (gate)
        {
            try
            {
                Broadcast(awareness.RemoveOutdated());
            }
            finally
            {
                CloseOverflowing();
            }
        }
    }

    /// <summary>
    /// Handles the sync message after its outer type: answers step 1 with step 2 (queued for
    /// <paramref name="session"/>), applies step 2 and updates with the session as the transaction origin. Throws
    /// what reading or applying throws; Ycs then leaves the document as it was. A step 1 while the answer to the
    /// session's last one is still queued throws too: each costs a diff of the whole document under the lock, so a
    /// session gets the next only once it has taken the last (a y-websocket client sends one per connection, and one
    /// per resync interval if it has one).
    /// </summary>
    internal void ReceiveSync(Session session, Stream reader)
    {
        lock (gate)
        {
            try
            {
                if (session.ReplyUnread && PeekVarUint(reader) == SyncProtocol.MessageYjsSyncStep1)
                {
                    throw new YUpdateException("A step 1 arrived before the answer to the last one was sent.");
                }

                var reply = new MemoryStream();
                VarUint.Write(reply, MessageType.Sync);

                // Structs and deletions waiting for this session's structs integrate in its transaction, so the update
                // then holds more than the session sent; without anything pending it holds only what the session sent.
                updateReachesOrigin = doc.HasPending;
                SyncProtocol.ReadSyncMessage(reader, reply, doc, session);

                // Only a step 1 writes a reply; the outer type alone is one byte.
                if (reply.Length > 1 && !session.TryEnqueueReply(reply.ToArray()))
                {
                    overflowing.Add(session);
                }
            }
            finally
            {
                CloseOverflowing();
            }
        }
    }

    private static uint PeekVarUint(Stream reader)
    {
        var position = reader.Position;
        var value = VarUint.Read(reader);
        reader.Position = position;
        return value;
    }

    /// <summary>A copy of the document's state, for tests and diagnostics.</summary>
    internal byte[] EncodeStateAsUpdate()
    {
        lock (gate)
        {
            return doc.EncodeStateAsUpdateV1();
        }
    }

    internal byte[] EncodeStateVector()
    {
        lock (gate)
        {
            return doc.EncodeStateVectorV1();
        }
    }

    internal int SessionCount
    {
        get
        {
            lock (gate)
            {
                return sessions.Length;
            }
        }
    }

    internal IReadOnlyCollection<ulong> AwarenessClientIds
    {
        get
        {
            lock (gate)
            {
                return [.. awareness.ClientIds];
            }
        }
    }

    // Under the lock.
    private void Broadcast(AwarenessChange change)
    {
        if (change.IsEmpty)
        {
            return;
        }

        // One array for every session: readers must not modify it.
        var bytes = AwarenessMessage(change.Broadcast());
        foreach (var session in sessions)
        {
            Send(session, bytes);
        }
    }

    // Under the lock.
    private void Send(Session session, byte[] message)
    {
        if (!session.TryEnqueue(message) && !session.IsClosed)
        {
            overflowing.Add(session);
        }
    }

    // Under the lock, at the end of every call that takes it: the leave of each session adds those its removal could
    // not reach, and this loop closes them too, one after the other.
    private void CloseOverflowing()
    {
        if (closingOverflowing)
        {
            return;
        }

        closingOverflowing = true;
        try
        {
            for (var i = 0; i < overflowing.Count; i++)
            {
                overflowing[i].Close(SessionCloseReason.QueueFull);
            }
        }
        finally
        {
            overflowing.Clear();
            closingOverflowing = false;
        }
    }

    // Under the lock.
    private byte[] AwarenessMessage(ulong[] clientIds)
    {
        var update = awareness.EncodeUpdate(clientIds);
        var message = new MemoryStream();
        VarUint.Write(message, MessageType.Awareness);
        VarUint.Write(message, (uint)update.Length);
        message.Write(update);
        return message.ToArray();
    }

    // Runs under the lock: the document changes only there.
    private void OnUpdate(object? sender, (byte[] Data, object Origin, Transaction Transaction) e)
    {
        var message = new MemoryStream();
        VarUint.Write(message, MessageType.Sync);
        SyncProtocol.WriteUpdate(message, e.Data);

        // One array for every session: readers must not modify it.
        var bytes = message.ToArray();
        foreach (var session in sessions)
        {
            if (updateReachesOrigin || !ReferenceEquals(session, e.Origin))
            {
                Send(session, bytes);
            }
        }
    }
}
