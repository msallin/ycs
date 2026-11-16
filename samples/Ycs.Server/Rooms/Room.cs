using Ycs.Server.Awareness;

namespace Ycs.Server.Rooms;

/// <summary>
/// One document, its awareness states and the sessions editing it. <see cref="YDoc"/> and
/// <see cref="Awareness.Awareness"/> are not thread-safe: every read and write of <see cref="doc"/>,
/// <see cref="awareness"/>, <see cref="sessions"/> and of each session's controlled ids happens under
/// <see cref="gate"/>, and nothing under it blocks or awaits (a session's queue is only ever written with
/// <c>TryWrite</c>). The lock is reentrant: a full queue closes its session, and that session leaves, from inside a
/// fan-out.
/// </summary>
internal sealed class Room
{
    private readonly Lock gate = new();
    private readonly YDoc doc = new();
    private readonly Awareness.Awareness awareness;

    // Copy-on-write under the lock, so that a fan-out iterates a snapshot while a full queue removes its session.
    private Session[] sessions = [];

    public Room(string name, TimeProvider? time = null)
    {
        Name = name;
        awareness = new Awareness.Awareness(time ?? TimeProvider.System);
        doc.Update += OnUpdate;
    }

    public string Name { get; }

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

            sessions = [.. sessions, session];
            var message = new MemoryStream();
            VarUint.Write(message, MessageType.Sync);
            SyncProtocol.WriteSyncStep1(message, doc);
            session.Enqueue(message.ToArray());

            // As y-websocket's server: the states of everyone present, in one message, only if there are any.
            if (awareness.ClientIds.Count > 0)
            {
                session.Enqueue(AwarenessMessage([.. awareness.ClientIds]));
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

        lock (gate)
        {
            // A session that left (closed from another thread) would set states nobody removes before they time out.
            if (Array.IndexOf(sessions, session) < 0)
            {
                return;
            }

            var change = awareness.ApplyUpdate(update);

            // Updated too, unlike y-websocket's server: a state set again after a removal or a timeout, or over
            // another session's, is an update, and would outlive this session.
            session.ControlledIds.UnionWith(change.Added);
            session.ControlledIds.UnionWith(change.Updated);
            session.ControlledIds.ExceptWith(change.Removed);
            Broadcast(change);
        }
    }

    /// <summary>
    /// Removes the awareness states not renewed for 30 s and tells every session. Unlike a close, the removed ids
    /// stay their sessions' controlled ids, as with y-protocols' timeout: a renewal brings the state back.
    /// </summary>
    internal void RemoveOutdatedAwareness()
    {
        lock (gate)
        {
            Broadcast(awareness.RemoveOutdated());
        }
    }

    /// <summary>
    /// Handles the sync message after its outer type: answers step 1 with step 2 (queued for
    /// <paramref name="session"/>), applies step 2 and updates with the session as the transaction origin. Throws
    /// what reading or applying throws; Ycs then leaves the document as it was.
    /// </summary>
    internal void ReceiveSync(Session session, Stream reader)
    {
        lock (gate)
        {
            var reply = new MemoryStream();
            VarUint.Write(reply, MessageType.Sync);
            SyncProtocol.ReadSyncMessage(reader, reply, doc, session);

            // Only a step 1 writes a reply; the outer type alone is one byte.
            if (reply.Length > 1)
            {
                session.Enqueue(reply.ToArray());
            }
        }
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
            session.Enqueue(bytes);
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
            if (!ReferenceEquals(session, e.Origin))
            {
                session.Enqueue(bytes);
            }
        }
    }
}
