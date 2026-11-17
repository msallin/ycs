using System.Buffers;
using Ycs.Server.Rooms;
using Ycs.Server.Tests.Support;

namespace Ycs.Server.Tests;

[TestClass]
public class SessionTests
{
    [TestMethod]
    public void Start_QueuesSyncStep1WithTheRoomsStateVector()
    {
        var room = new Room("r");
        var session = new Session(room);

        session.Start();

        var message = Messages.Drain(session).Single();
        CollectionAssert.AreEqual(Messages.Sync(s => SyncProtocol.WriteSyncStep1(s, new YDoc())), message);
    }

    [TestMethod]
    public void Connect_ServerHasContent_ClientReceivesIt()
    {
        var room = new Room("r");
        var writer = new TestClient(1);
        writer.Connect(room);
        writer.Insert(0, "hello");
        var reader = new TestClient(2);

        reader.Connect(room);
        TestClient.PumpAll(writer, reader);

        Assert.AreEqual("hello", reader.Text);
    }

    [TestMethod]
    public void Connect_ClientHasContent_RoomReceivesIt()
    {
        var room = new Room("r");
        var client = new TestClient(1);
        client.Insert(0, "offline");

        client.Connect(room);
        TestClient.PumpAll(client);

        Assert.AreEqual("offline", Messages.TextOf(room.EncodeStateAsUpdate()));
    }

    [TestMethod]
    public void Receive_Update_GoesToEveryOtherSessionAndNotToItsOrigin()
    {
        var room = new Room("r");
        var clients = Enumerable.Range(1, 4).Select(i => new TestClient(i)).ToArray();
        foreach (var client in clients)
        {
            client.Connect(room);
        }

        TestClient.PumpAll(clients);

        clients[0].Insert(0, "x");

        var received = clients.Select(c => Messages.Drain(c.Session!).Select(Messages.SyncType).ToArray()).ToArray();
        var expected = new[] { Array.Empty<uint>(), [(uint)SyncMessageType.Update], [(uint)SyncMessageType.Update], [(uint)SyncMessageType.Update] };
        Assert.AreEqual(Format(expected), Format(received));
    }

    [TestMethod]
    public void Receive_LiveEdits_EveryClientConverges()
    {
        var room = new Room("r");
        var clients = Enumerable.Range(1, 3).Select(i => new TestClient(i)).ToArray();
        foreach (var client in clients)
        {
            client.Connect(room);
        }

        clients[0].Insert(0, "a");
        clients[1].Insert(0, "b");
        TestClient.PumpAll(clients);
        clients[2].Insert(1, "c");
        TestClient.PumpAll(clients);

        var roomText = Messages.TextOf(room.EncodeStateAsUpdate());
        Assert.AreEqual((3, roomText, roomText, roomText), (roomText.Length, clients[0].Text, clients[1].Text, clients[2].Text));
    }

    [TestMethod]
    public void Reconnect_AfterOfflineEdits_ClientsAndRoomConverge()
    {
        var room = new Room("r");
        var a = new TestClient(1);
        var b = new TestClient(2);
        a.Connect(room);
        b.Connect(room);
        a.Insert(0, "shared ");
        TestClient.PumpAll(a, b);
        a.Disconnect();
        a.Insert(7, "offline");
        b.Insert(0, "online ");
        TestClient.PumpAll(b);

        a.Connect(room);
        TestClient.PumpAll(a, b);

        var roomText = Messages.TextOf(room.EncodeStateAsUpdate());
        Assert.AreEqual(("online shared offline", roomText, roomText), (a.Text, b.Text, roomText));
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public void Receive_ConcurrentFromManyThreads_EveryClientConvergesWithTheRoom()
    {
        const int Clients = 8;
        const int EditsEach = 200;
        var room = new Room("r");
        var clients = Enumerable.Range(1, Clients).Select(i => new TestClient(i)).ToArray();
        var updates = clients.Select(c => RecordEdits(c.Doc, EditsEach, seed: (int)c.Doc.ClientId)).ToArray();
        var sessions = clients.Select(_ => new Session(room, queueCapacity: Clients * EditsEach * 2)).ToArray();
        foreach (var session in sessions)
        {
            session.Start();
        }

        using var go = new ManualResetEventSlim();
        var threads = Enumerable.Range(0, Clients).Select(i => new Thread(() =>
        {
            go.Wait();
            foreach (var update in updates[i])
            {
                sessions[i].Receive(Messages.Update(update));
            }
        })).ToArray();
        foreach (var thread in threads)
        {
            thread.Start();
        }

        go.Set();
        foreach (var thread in threads)
        {
            thread.Join();
        }

        for (var i = 0; i < Clients; i++)
        {
            foreach (var message in Messages.Drain(sessions[i]))
            {
                var reader = new MemoryStream(message);
                VarUint.Read(reader);
                Messages.HandleSync(reader, clients[i].Doc, "server");
            }
        }

        var state = room.EncodeStateAsUpdate();
        var expected = (Text: Messages.TextOf(state), Length: Clients * EditsEach, Open: Clients);
        var actual = (
            Text: clients.Select(c => c.Text).Distinct().Single(),
            Length: Messages.TextOf(state).Length,
            Open: sessions.Count(s => !s.IsClosed));
        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    [DataRow("empty")]
    [DataRow("unknown message type")]
    [DataRow("unknown sync type")]
    [DataRow("truncated frame")]
    [DataRow("truncated update")]
    [DataRow("garbage update")]
    [DataRow("truncated step 1")]
    [DataRow("garbage state vector")]
    public void Receive_MalformedMessage_ClosesOnlyThatSessionAndLeavesTheDocumentAsItWas(string kind)
    {
        var room = new Room("r");
        var good = new TestClient(1);
        good.Connect(room);
        good.Insert(0, "kept");
        var bad = new Session(room);
        bad.Start();
        TestClient.PumpAll(good);
        var stateBefore = room.EncodeStateAsUpdate();
        var vectorBefore = room.EncodeStateVector();

        bad.Receive(Malformed(kind));

        var after = (
            bad.CloseReason,
            Good: good.Session!.CloseReason,
            room.SessionCount,
            Vector: Convert.ToHexString(room.EncodeStateVector()),
            State: Convert.ToHexString(room.EncodeStateAsUpdate()));
        Assert.AreEqual(
            (SessionCloseReason.ProtocolError, SessionCloseReason.None, 1, Convert.ToHexString(vectorBefore), Convert.ToHexString(stateBefore)),
            after);
    }

    [TestMethod]
    public void Receive_AfterAMalformedMessage_TheRoomKeepsWorking()
    {
        var room = new Room("r");
        var a = new TestClient(1);
        var b = new TestClient(2);
        a.Connect(room);
        b.Connect(room);
        var bad = new Session(room);
        bad.Start();
        bad.Receive(Malformed("truncated update"));

        a.Insert(0, "still");
        TestClient.PumpAll(a, b);

        Assert.AreEqual("still", b.Text);
    }

    [TestMethod]
    [DataRow(MessageType.Awareness)]
    [DataRow(MessageType.QueryAwareness)]
    public void Receive_AwarenessWithoutEntries_IsAcceptedAndChangesNothing(uint type)
    {
        var room = new Room("r");
        var session = new Session(room);
        session.Start();
        Messages.Drain(session);
        var stream = new MemoryStream();
        VarUint.Write(stream, type);

        // An awareness update of one byte: zero entries.
        stream.WriteByte(1);
        stream.WriteByte(0);

        session.Receive(stream.ToArray());

        Assert.AreEqual((SessionCloseReason.None, 0), (session.CloseReason, Messages.Drain(session).Count));
    }

    [TestMethod]
    public void Enqueue_QueueFull_ClosesThatSessionAndTheRoomKeepsWorking()
    {
        var room = new Room("r");
        var writer = new TestClient(1);
        var reader = new TestClient(2);
        writer.Connect(room);
        reader.Connect(room);
        TestClient.PumpAll(writer, reader);
        var stalled = new Session(room, queueCapacity: 2);
        stalled.Start();

        writer.Insert(0, "a");
        writer.Insert(1, "b");
        writer.Insert(2, "c");
        TestClient.PumpAll(writer, reader);

        Assert.AreEqual(
            (SessionCloseReason.QueueFull, 2, "abc"),
            (stalled.CloseReason, room.SessionCount, reader.Text));
    }

    [TestMethod]
    public void Close_LeavesTheRoomAndCompletesOutgoing()
    {
        var room = new Room("r");
        var session = new Session(room);
        session.Start();
        Messages.Drain(session);

        session.Close();

        Assert.AreEqual(
            (SessionCloseReason.Disconnected, 0, true),
            (session.CloseReason, room.SessionCount, session.Outgoing.Completion.IsCompleted));
    }

    [TestMethod]
    public void Receive_AfterClose_IsIgnored()
    {
        var room = new Room("r");
        var source = new YDoc();
        source.GetText("text").Insert(0, "late");
        var session = new Session(room);
        session.Start();
        session.Close();

        session.Receive(Messages.Update(source.EncodeStateAsUpdate()));

        Assert.AreEqual(string.Empty, Messages.TextOf(room.EncodeStateAsUpdate()));
    }

    [TestMethod]
    public void Receive_BeforeStart_Throws()
    {
        var session = new Session(new Room("r"));

        Assert.ThrowsExactly<InvalidOperationException>(() => session.Receive([0, 0, 0]));
    }

    [TestMethod]
    public void RoomRegistry_SameName_ReturnsTheSameRoom()
    {
        var registry = new RoomRegistry(TimeProvider.System);

        var rooms = (registry.GetOrCreate("a"), registry.GetOrCreate("a"), registry.GetOrCreate("b"));

        Assert.AreEqual((true, false), (ReferenceEquals(rooms.Item1, rooms.Item2), ReferenceEquals(rooms.Item1, rooms.Item3)));
    }

    private static byte[] Malformed(string kind)
    {
        var source = new YDoc(new YDocOptions { ClientId = 9 });
        source.GetText("text").Insert(0, "lost");
        source.GetText("text").Remove(1, 1);
        var update = source.EncodeStateAsUpdate();
        return kind switch
        {
            "empty" => [],
            "unknown message type" => [2, 0],
            "unknown sync type" => [(byte)MessageType.Sync, 3, 0],
            "truncated frame" => Messages.Update(update)[..^2],
            "truncated update" => Messages.Update(update[..^2]),
            "garbage update" => Messages.Update([0x05, 0xFF, 0xFF, 0xFF, 0x01, 0x80]),
            "truncated step 1" => [(byte)MessageType.Sync, (byte)SyncMessageType.SyncStep1, 5, 1],
            "garbage state vector" => Messages.Sync(s => s.Write(new byte[] { (byte)SyncMessageType.SyncStep1, 3, 0xFF, 0xFF, 0xFF }.AsSpan())),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static List<byte[]> RecordEdits(YDoc doc, int count, int seed)
    {
        var updates = new List<byte[]>();
        doc.Updated += (_, e) => updates.Add(e.Update.ToArray());
        var random = new Random(seed);
        var text = doc.GetText("text");
        for (var i = 0; i < count; i++)
        {
            text.Insert(random.Next(text.Length + 1), ((char)('a' + random.Next(26))).ToString());
        }

        return updates;
    }

    private static string Format(uint[][] messages) => string.Join(" | ", messages.Select(m => string.Join(",", m)));

    [TestMethod]
    public void Receive_MoreWaitingStructsThanTheRoomKeeps_ClosesTheSessionAndAReconnectedOneSyncs()
    {
        var room = new Room("r");
        var forger = new YDoc(new YDocOptions { ClientId = 7 });
        HostileUpdates.Edit(forger, d => d.GetText("text").Insert(0, "m"));
        var waiting = HostileUpdates.Edit(forger, d =>
        {
            for (var i = 0; i < 5_000; i++)
            {
                d.GetText("text").Insert(1, "w");
            }
        });
        var hostile = new Session(room);
        hostile.Start();
        hostile.Receive(Messages.Update(waiting));
        var reconnected = new TestClient(2);
        reconnected.Connect(room);
        reconnected.Insert(0, "ok");
        TestClient.PumpAll(reconnected);

        Assert.AreEqual(
            (SessionCloseReason.ProtocolError, true, 1, "ok"),
            (hostile.CloseReason, hostile.CloseCause is YUpdateException e && e.Message.Contains("MaxPendingStructs", StringComparison.Ordinal), room.SessionCount, reconnected.Text));
    }
}
