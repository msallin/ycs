using System.Buffers;
using Ycs.Server.Rooms;

namespace Ycs.Server.Tests.Support;

/// <summary>y-websocket messages as a client puts them on the wire.</summary>
internal static class Messages
{
    public static byte[] Step1(YDoc doc) => Sync(s => SyncProtocol.WriteSyncStep1(s, doc));

    public static byte[] Update(byte[] update) => Sync(s => SyncProtocol.WriteUpdate(s, update));

    /// <summary>
    /// Handles the sync message that follows the outer type in <paramref name="reader"/>; returns what the peer
    /// answers (an outer type and a step 2) or null for a message that has no answer.
    /// </summary>
    public static byte[]? HandleSync(MemoryStream reader, YDoc doc, object origin)
    {
        var reply = new ArrayBufferWriter<byte>();
        SyncProtocol.WriteMessageType(reply, SyncProtocol.MessageSync);
        var result = SyncProtocol.ReadSyncMessage(reader.ToArray().AsSpan((int)reader.Position), reply, doc, origin);
        return result.Replied ? reply.WrittenSpan.ToArray() : null;
    }

    /// <summary>An awareness message of one client, as y-protocols encodes it; a null state removes it.</summary>
    public static byte[] Awareness(ulong clientId, ulong clock, string? json)
    {
        var update = new MemoryStream();
        update.WriteByte(1);
        WriteVarUint(update, clientId);
        WriteVarUint(update, clock);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json ?? "null");
        WriteVarUint(update, (ulong)bytes.Length);
        update.Write(bytes);

        var message = new MemoryStream();
        VarUint.Write(message, MessageType.Awareness);
        VarUint.Write(message, (uint)update.Length);
        update.WriteTo(message);
        return message.ToArray();
    }

    /// <summary>The outer type of a message and what follows it.</summary>
    public static (uint Type, MemoryStream Reader) Split(byte[] message)
    {
        var reader = new MemoryStream(message, writable: false);
        return (VarUint.Read(reader), reader);
    }

    /// <summary>The awareness update inside an awareness message (the length-prefixed payload after the type).</summary>
    public static byte[] AwarenessUpdate(byte[] message)
    {
        var (type, reader) = Split(message);
        Assert.AreEqual(MessageType.Awareness, type);
        var update = new byte[VarUint.Read(reader)];
        reader.ReadExactly(update);
        return update;
    }

    /// <summary>The client ids and states (null for a removal) of an awareness message, in its order.</summary>
    public static (ulong ClientId, string? State)[] AwarenessEntries(byte[] message)
    {
        var awareness = new Server.Awareness.Awareness(new Server.Awareness.AwarenessClock());
        var update = AwarenessUpdate(message);
        var reader = new MemoryStream(update);
        var count = ReadVarUint(reader);
        var ids = new List<ulong>();
        for (ulong i = 0; i < count; i++)
        {
            ids.Add(ReadVarUint(reader));
            ReadVarUint(reader);
            reader.Seek((long)ReadVarUint(reader), SeekOrigin.Current);
        }

        awareness.ApplyUpdate(update);
        return [.. ids.Select(id => (id, awareness.TryGetState(id, out var state) ? state : null))];
    }

    private static void WriteVarUint(Stream stream, ulong value)
    {
        while (value > 0x7F)
        {
            stream.WriteByte((byte)(0x80 | (value & 0x7F)));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }

    private static ulong ReadVarUint(Stream stream)
    {
        ulong value = 0;
        for (var shift = 0; ; shift += 7)
        {
            var b = stream.ReadByte();
            value |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80)
            {
                return value;
            }
        }
    }

    public static byte[] Sync(Action<IBufferWriter<byte>> write)
    {
        var message = new ArrayBufferWriter<byte>();
        SyncProtocol.WriteMessageType(message, SyncProtocol.MessageSync);
        write(message);
        return message.WrittenSpan.ToArray();
    }

    /// <summary>The sync sub-type of a queued message, which is always a sync message here.</summary>
    public static uint SyncType(byte[] message)
    {
        var reader = new MemoryStream(message);
        Assert.AreEqual(MessageType.Sync, VarUint.Read(reader));
        return VarUint.Read(reader);
    }

    public static List<byte[]> Drain(Session session)
    {
        var messages = new List<byte[]>();
        while (session.Outgoing.TryRead(out var message))
        {
            messages.Add(message);
        }

        return messages;
    }

    /// <summary>The text of a fresh document built from <paramref name="state"/>.</summary>
    public static string TextOf(byte[] state)
    {
        var doc = new YDoc();
        doc.ApplyUpdate(state);
        return doc.GetText("text").ToString();
    }
}
