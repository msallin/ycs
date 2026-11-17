using System.Buffers;
using System.Text;

namespace Ycs.Client;

/// <summary>
/// The y-websocket messages a client writes and reads: lib0 encoding, the first variable-length unsigned integer the
/// message type.
/// </summary>
internal static class Wire
{
    public const uint Sync = SyncProtocol.MessageSync;
    public const uint Awareness = SyncProtocol.MessageAwareness;

    public static byte[] SyncStep1(YDoc doc)
    {
        var message = new ArrayBufferWriter<byte>();
        SyncProtocol.WriteMessageType(message, Sync);
        SyncProtocol.WriteSyncStep1(message, doc);
        return message.WrittenSpan.ToArray();
    }

    public static byte[] Update(byte[] update)
    {
        var message = new ArrayBufferWriter<byte>(update.Length + 16);
        SyncProtocol.WriteMessageType(message, Sync);
        SyncProtocol.WriteUpdate(message, update);
        return message.WrittenSpan.ToArray();
    }

    /// <summary>
    /// An awareness message of one client, as y-protocols' <c>encodeAwarenessUpdate</c> writes it inside the
    /// length-prefixed payload: the count, then client id, clock and the state's JSON text ("null" for none).
    /// </summary>
    public static byte[] AwarenessMessage(ulong clientId, ulong clock, string? json)
    {
        using var update = new MemoryStream();
        WriteVarUint(update, 1);
        WriteVarUint(update, clientId);
        WriteVarUint(update, clock);
        var bytes = Encoding.UTF8.GetBytes(json ?? "null");
        WriteVarUint(update, (ulong)bytes.Length);
        update.Write(bytes);

        return Message(Awareness, s =>
        {
            WriteVarUint(s, (ulong)update.Length);
            update.WriteTo(s);
        });
    }

    public static void WriteVarUint(Stream stream, ulong value)
    {
        while (value > 0x7F)
        {
            stream.WriteByte((byte)(0x80 | (value & 0x7F)));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }

    /// <summary>A variable-length unsigned integer of at most 32 bits, as lib0 frames messages and lengths.</summary>
    public static uint ReadVarUint(Stream stream)
    {
        ulong value = 0;
        for (var shift = 0; shift <= 35; shift += 7)
        {
            var b = stream.ReadByte();
            if (b < 0)
            {
                throw new YUpdateException("The message ended early.");
            }

            value |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80)
            {
                return value <= uint.MaxValue ? (uint)value : throw new YUpdateException("A variable-length integer is out of range.");
            }
        }

        throw new YUpdateException("A variable-length integer is out of range.");
    }

    /// <summary>A length-prefixed byte array (lib0's <c>readVarUint8Array</c>).</summary>
    public static byte[] ReadVarUint8Array(Stream stream)
    {
        var length = ReadVarUint(stream);
        if (length > stream.Length - stream.Position)
        {
            throw new YUpdateException("The message ends inside a byte array.");
        }

        var bytes = new byte[length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static byte[] Message(uint type, Action<Stream> write)
    {
        using var stream = new MemoryStream();
        WriteVarUint(stream, type);
        write(stream);
        return stream.ToArray();
    }
}
