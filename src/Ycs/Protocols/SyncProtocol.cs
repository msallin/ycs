// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

namespace Ycs;

/// <summary>
/// The y-protocols 'sync' message format. Defaults to the original ('v1') update
/// encoding, which is what y-websocket and every other JS provider puts on the wire;
/// pass <c>v2: true</c> to use the denser v2 encoding on both ends instead.
/// </summary>
public static class SyncProtocol
{
    public const uint MessageYjsSyncStep1 = 0;
    public const uint MessageYjsSyncStep2 = 1;
    public const uint MessageYjsUpdate = 2;

    private static void WriteVarUint(Stream stream, uint num)
    {
        Span<byte> buffer = stackalloc byte[5];
        var i = 0;
        while (num > 0x7F)
        {
            buffer[i++] = (byte)(0x80 | (num & 0x7F));
            num >>= 7;
        }

        buffer[i++] = (byte)num;
        stream.Write(buffer[..i]);
    }

    private static void WriteVarUint8Array(Stream stream, ReadOnlySpan<byte> data)
    {
        WriteVarUint(stream, (uint)data.Length);
        stream.Write(data);
    }

    private static uint ReadVarUint(Stream stream)
    {
        ulong num = 0;
        for (var shift = 0; shift <= 35; shift += 7)
        {
            var r = stream.ReadByte();
            if (r < 0)
            {
                throw new YUpdateException("The message ended early.");
            }

            num |= (ulong)(r & 0x7F) << shift;
            if (r < 0x80)
            {
                return num <= uint.MaxValue ? (uint)num : throw new YUpdateException("A variable-length integer is out of range.");
            }
        }

        throw new YUpdateException("A variable-length integer is out of range.");
    }

    /// <summary>
    /// The length comes from the peer, so the buffer grows with the bytes that actually arrive instead
    /// of being allocated up front.
    /// </summary>
    private static byte[] ReadVarUint8Array(Stream stream)
    {
        var length = ReadVarUint(stream);
        const int Chunk = 64 * 1024;
        if (length <= Chunk)
        {
            var small = new byte[length];
            ReadExactly(stream, small);
            return small;
        }

        var buffer = new MemoryStream();
        var chunk = new byte[Chunk];
        for (long remaining = length; remaining > 0;)
        {
            var part = chunk.AsSpan(0, (int)Math.Min(Chunk, remaining));
            ReadExactly(stream, part);
            buffer.Write(part);
            remaining -= part.Length;
        }

        return buffer.ToArray();
    }

    private static void ReadExactly(Stream stream, Span<byte> destination)
    {
        try
        {
            stream.ReadExactly(destination);
        }
        catch (EndOfStreamException e)
        {
            throw new YUpdateException("The message ended early.", e);
        }
    }

    public static void WriteSyncStep1(Stream stream, YDoc doc, bool v2 = false)
    {
        WriteVarUint(stream, MessageYjsSyncStep1);
        var sv = v2 ? doc.EncodeStateVectorV2() : doc.EncodeStateVectorV1();
        WriteVarUint8Array(stream, sv);
    }

    public static void WriteSyncStep2(Stream stream, YDoc doc, byte[] encodedStateVector, bool v2 = false)
    {
        WriteVarUint(stream, MessageYjsSyncStep2);
        var update = v2 ? doc.EncodeStateAsUpdateV2(encodedStateVector) : doc.EncodeStateAsUpdateV1(encodedStateVector);
        WriteVarUint8Array(stream, update);
    }

    public static void ReadSyncStep1(Stream reader, Stream writer, YDoc doc, bool v2 = false)
    {
        var encodedStateVector = ReadVarUint8Array(reader);
        WriteSyncStep2(writer, doc, encodedStateVector, v2);
    }

    /// <summary>
    /// Reads an update and applies it. As in y-protocols, with an <paramref name="errorHandler"/> an
    /// exception of the read or the apply (a <see cref="YUpdateException"/> for a malformed update, or one an
    /// observer threw) goes to the handler and the call returns; without one it propagates (y-protocols logs it).
    /// </summary>
    public static void ReadSyncStep2(Stream stream, YDoc doc, object transactionOrigin, bool v2 = false, Action<Exception> errorHandler = null)
    {
        try
        {
            var update = ReadVarUint8Array(stream);
            if (v2)
            {
                doc.ApplyUpdateV2(update, transactionOrigin);
            }
            else
            {
                doc.ApplyUpdateV1(update, transactionOrigin);
            }
        }
        catch (Exception e) when (errorHandler != null)
        {
            errorHandler(e);
        }
    }

    public static void WriteUpdate(Stream stream, byte[] update)
    {
        WriteVarUint(stream, MessageYjsUpdate);
        WriteVarUint8Array(stream, update);
    }

    /// <inheritdoc cref="ReadSyncStep2"/>
    public static void ReadUpdate(Stream stream, YDoc doc, object transactionOrigin, bool v2 = false, Action<Exception> errorHandler = null)
    {
        ReadSyncStep2(stream, doc, transactionOrigin, v2, errorHandler);
    }

    /// <summary>
    /// Reads one sync message and handles it; returns its type. <paramref name="errorHandler"/> receives what
    /// reading and applying an update threw (see <see cref="ReadSyncStep2"/>); an unknown message type or a
    /// malformed step 1 throws <see cref="YUpdateException"/>, as in y-protocols.
    /// </summary>
    public static uint ReadSyncMessage(Stream reader, Stream writer, YDoc doc, object transactionOrigin, bool v2 = false, Action<Exception> errorHandler = null)
    {
        var messageType = ReadVarUint(reader);

        switch (messageType)
        {
            case MessageYjsSyncStep1:
                ReadSyncStep1(reader, writer, doc, v2);
                break;
            case MessageYjsSyncStep2:
                ReadSyncStep2(reader, doc, transactionOrigin, v2, errorHandler);
                break;
            case MessageYjsUpdate:
                ReadUpdate(reader, doc, transactionOrigin, v2, errorHandler);
                break;
            default:
                throw new YUpdateException($"Unknown sync message type: {messageType}.");
        }

        return messageType;
    }
}
