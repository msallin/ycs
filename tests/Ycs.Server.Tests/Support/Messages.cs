using Ycs.Server.Rooms;

namespace Ycs.Server.Tests.Support;

/// <summary>y-websocket messages as a client puts them on the wire.</summary>
internal static class Messages
{
    public static byte[] Step1(YDoc doc) => Sync(s => SyncProtocol.WriteSyncStep1(s, doc));

    public static byte[] Update(byte[] update) => Sync(s => SyncProtocol.WriteUpdate(s, update));

    public static byte[] Sync(Action<Stream> write)
    {
        var stream = new MemoryStream();
        VarUint.Write(stream, MessageType.Sync);
        write(stream);
        return stream.ToArray();
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
        doc.ApplyUpdateV1(state);
        return doc.GetText("text").ToString();
    }
}
