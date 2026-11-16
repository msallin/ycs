namespace Ycs.Server.Rooms;

/// <summary>
/// One document and the sessions editing it. <see cref="YDoc"/> is not thread-safe: every read and write of
/// <see cref="doc"/> and of <see cref="sessions"/> happens under <see cref="gate"/>, and nothing under it blocks or
/// awaits (a session's queue is only ever written with <c>TryWrite</c>).
/// </summary>
internal sealed class Room
{
    private readonly Lock gate = new();
    private readonly YDoc doc = new();

    // Copy-on-write under the lock, so that a fan-out iterates a snapshot while a full queue removes its session.
    private Session[] sessions = [];

    public Room(string name)
    {
        Name = name;
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
            sessions = [.. sessions, session];
            var message = new MemoryStream();
            VarUint.Write(message, MessageType.Sync);
            SyncProtocol.WriteSyncStep1(message, doc);
            session.Enqueue(message.ToArray());
        }
    }

    internal void Leave(Session session)
    {
        lock (gate)
        {
            var i = Array.IndexOf(sessions, session);
            if (i >= 0)
            {
                sessions = [.. sessions.AsSpan(0, i), .. sessions.AsSpan(i + 1)];
            }
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
