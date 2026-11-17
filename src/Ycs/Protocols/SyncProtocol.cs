#nullable enable

using System.Buffers;

namespace Ycs;

/// <summary>The type of a y-protocols 'sync' message, the first variable-length integer of one.</summary>
public enum SyncMessageType : uint
{
    /// <summary>A state vector; the answer is a <see cref="SyncStep2"/>.</summary>
    SyncStep1 = 0,

    /// <summary>The updates the sender of a <see cref="SyncStep1"/> lacks.</summary>
    SyncStep2 = 1,

    /// <summary>One update of the document.</summary>
    Update = 2,
}

/// <summary>What <see cref="SyncProtocol.ReadSyncMessage(ReadOnlySpan{byte}, IBufferWriter{byte}, YDoc, object)"/> did with a message.</summary>
/// <param name="Type">The type of the message.</param>
/// <param name="Replied">Whether an answer was appended to the reply buffer: true for a step 1, false for the others.</param>
public readonly record struct SyncReadResult(SyncMessageType Type, bool Replied);

/// <summary>
/// The y-protocols 'sync' message format, which y-websocket and every other Yjs provider puts on the wire: a
/// <see cref="SyncMessageType"/> as a variable-length integer, then the payload as a length-prefixed byte array.
/// The messages carry updates in the original ('v1') encoding; <see cref="UpdateFormat.V2"/> uses the denser v2 encoding, which
/// both ends must then agree on. The writers append to a buffer writer and keep nothing; a reader takes one whole
/// message, and bytes after it are ignored. A message that is malformed throws <see cref="YUpdateException"/>.
/// <br/>
/// y-websocket puts one more variable-length integer in front of every message, which says what the message is
/// (<see cref="MessageSync"/>, <see cref="MessageAwareness"/>; y-websocket also has 2, auth, and 3, query awareness):
/// <see cref="WriteMessageType"/> and <see cref="ReadMessageType"/> write and strip it. What follows the type of an
/// awareness message is not part of this class.
/// </summary>
public static class SyncProtocol
{
    /// <summary>The y-websocket message type of a sync message.</summary>
    public const uint MessageSync = 0;

    /// <summary>The y-websocket message type of an awareness message.</summary>
    public const uint MessageAwareness = 1;

    private const int MaxVarUintBytes = 5;

    /// <summary>Appends the y-websocket message type that starts a message.</summary>
    public static void WriteMessageType(IBufferWriter<byte> destination, uint messageType)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var header = destination.GetSpan(MaxVarUintBytes);
        destination.Advance(WriteVarUint(header, messageType));
    }

    /// <summary>
    /// Reads the y-websocket message type a message starts with; <paramref name="body"/> is what follows it, such as
    /// the sync message <see cref="ReadSyncMessage(ReadOnlySpan{byte}, IBufferWriter{byte}, YDoc, object)"/> takes.
    /// </summary>
    /// <exception cref="YUpdateException">The message is empty or its type is truncated.</exception>
    public static uint ReadMessageType(ReadOnlySpan<byte> message, out ReadOnlySpan<byte> body)
    {
        var position = 0;
        var type = ReadVarUint(message, ref position);
        body = message[position..];
        return type;
    }

    /// <summary>Appends sync step 1: the document's state vector, which asks the peer for what the document lacks.</summary>
    public static void WriteSyncStep1(IBufferWriter<byte> destination, YDoc doc)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(doc);
        using var payload = new Payload();
        doc.EncodeStateVector(payload);
        WriteMessage(destination, SyncMessageType.SyncStep1, payload.Written);
    }

    /// <summary>Appends sync step 2: what a peer with <paramref name="encodedStateVector"/> lacks of the document.</summary>
    public static void WriteSyncStep2(IBufferWriter<byte> destination, YDoc doc, ReadOnlySpan<byte> encodedStateVector, UpdateFormat format = UpdateFormat.V1)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(doc);
        using var payload = new Payload();
        doc.EncodeStateAsUpdate(payload, encodedStateVector, format);

        WriteMessage(destination, SyncMessageType.SyncStep2, payload.Written);
    }

    /// <summary>Appends an update message; <paramref name="update"/> is in the encoding the peer reads.</summary>
    public static void WriteUpdate(IBufferWriter<byte> destination, ReadOnlySpan<byte> update)
    {
        ArgumentNullException.ThrowIfNull(destination);
        WriteMessage(destination, SyncMessageType.Update, update);
    }

    /// <summary>
    /// Returns a whole y-websocket sync update message, the message type, the sync type, the length and
    /// <paramref name="update"/>, in one array: what <see cref="WriteMessageType"/> followed by <see cref="WriteUpdate"/> write.
    /// </summary>
    public static byte[] CreateUpdateMessage(ReadOnlySpan<byte> update)
    {
        Span<byte> header = stackalloc byte[3 * MaxVarUintBytes];
        var count = WriteVarUint(header, MessageSync);
        count += WriteVarUint(header[count..], (uint)SyncMessageType.Update);
        count += WriteVarUint(header[count..], (uint)update.Length);
        var message = new byte[count + update.Length];
        header[..count].CopyTo(message);
        update.CopyTo(message.AsSpan(count));
        return message;
    }

    /// <summary>
    /// Handles one sync message, as the y-websocket message type has been stripped from it: a step 1 appends the answer (a step 2) to
    /// <paramref name="reply"/>; a step 2 or an update is applied to <paramref name="doc"/> with
    /// <paramref name="origin"/> as the transaction origin, and writes nothing. The result tells the type and whether
    /// <paramref name="reply"/> got an answer.
    /// </summary>
    /// <exception cref="YUpdateException">
    /// The message is truncated or of an unknown type, or its update is malformed or beyond a limit of the
    /// document (<see cref="YDoc.ApplyUpdate(ReadOnlySpan{byte}, UpdateFormat, object, UpdateLimits)"/>; the document is then as
    /// it was). An exception of an observer propagates as it is.
    /// </exception>
    public static SyncReadResult ReadSyncMessage(ReadOnlySpan<byte> message, IBufferWriter<byte> reply, YDoc doc, object? origin = null) =>
        ReadSyncMessage(message, reply, doc, UpdateFormat.V1, origin);

    /// <inheritdoc cref="ReadSyncMessage(ReadOnlySpan{byte}, IBufferWriter{byte}, YDoc, object)"/>
    /// <param name="format">The encoding of the updates the message carries and of the answer to a step 1.</param>
    public static SyncReadResult ReadSyncMessage(ReadOnlySpan<byte> message, IBufferWriter<byte> reply, YDoc doc, UpdateFormat format, object? origin = null)
    {
        ArgumentNullException.ThrowIfNull(reply);
        ArgumentNullException.ThrowIfNull(doc);
        var position = 0;
        var type = ReadVarUint(message, ref position);
        var length = ReadVarUint(message, ref position);
        if (length > (uint)(message.Length - position))
        {
            throw new YUpdateException("The message ends inside a byte array.");
        }

        var payload = message.Slice(position, (int)length);
        switch (type)
        {
            case (uint)SyncMessageType.SyncStep1:
                WriteSyncStep2(reply, doc, payload, format);
                break;
            case (uint)SyncMessageType.SyncStep2:
            case (uint)SyncMessageType.Update:
                doc.ApplyUpdate(payload, format, origin);
                break;
            default:
                throw new YUpdateException($"Unknown sync message type: {type}.");
        }

        return new SyncReadResult((SyncMessageType)type, type == (uint)SyncMessageType.SyncStep1);
    }

    /// <inheritdoc cref="ReadSyncMessage(ReadOnlySpan{byte}, IBufferWriter{byte}, YDoc, object)"/>
    public static SyncReadResult ReadSyncMessage(ReadOnlySequence<byte> message, IBufferWriter<byte> reply, YDoc doc, object? origin = null) =>
        ReadSyncMessage(message, reply, doc, UpdateFormat.V1, origin);

    /// <inheritdoc cref="ReadSyncMessage(ReadOnlySpan{byte}, IBufferWriter{byte}, YDoc, UpdateFormat, object)"/>
    public static SyncReadResult ReadSyncMessage(ReadOnlySequence<byte> message, IBufferWriter<byte> reply, YDoc doc, UpdateFormat format, object? origin = null)
    {
        if (message.IsSingleSegment)
        {
            return ReadSyncMessage(message.FirstSpan, reply, doc, format, origin);
        }

        // A message of several segments is copied once, to one array, as the length comes from the peer: the
        // copy is bounded by the message the caller already holds.
        var length = checked((int)message.Length);
        var rented = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            message.CopyTo(rented);
            return ReadSyncMessage(rented.AsSpan(0, length), reply, doc, format, origin);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void WriteMessage(IBufferWriter<byte> destination, SyncMessageType type, ReadOnlySpan<byte> payload)
    {
        var header = destination.GetSpan(2 * MaxVarUintBytes);
        var count = WriteVarUint(header, (uint)type);
        count += WriteVarUint(header[count..], (uint)payload.Length);
        destination.Advance(count);
        destination.Write(payload);
    }

    private static int WriteVarUint(Span<byte> destination, uint value)
    {
        var i = 0;
        while (value > 0x7F)
        {
            destination[i++] = (byte)(0x80 | (value & 0x7F));
            value >>= 7;
        }

        destination[i++] = (byte)value;
        return i;
    }

    private static uint ReadVarUint(ReadOnlySpan<byte> source, ref int position)
    {
        ulong value = 0;
        for (var shift = 0; shift <= 35; shift += 7)
        {
            if (position >= source.Length)
            {
                throw new YUpdateException("The message ended early.");
            }

            var b = source[position++];
            value |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80)
            {
                return value <= uint.MaxValue ? (uint)value : throw new YUpdateException("A variable-length integer is out of range.");
            }
        }

        throw new YUpdateException("A variable-length integer is out of range.");
    }

    // The length of a payload precedes it and is known only once it is encoded, so it is encoded here first.
    private sealed class Payload : IBufferWriter<byte>, IDisposable
    {
        private byte[] _buffer = ArrayPool<byte>.Shared.Rent(256);
        private int _count;

        public ReadOnlySpan<byte> Written => _buffer.AsSpan(0, _count);

        public void Advance(int count) => _count += count;

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Grow(sizeHint);
            return _buffer.AsMemory(_count);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Grow(sizeHint);
            return _buffer.AsSpan(_count);
        }

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = [];
        }

        private void Grow(int sizeHint)
        {
            var needed = _count + Math.Max(sizeHint, 1);
            if (needed <= _buffer.Length)
            {
                return;
            }

            var larger = ArrayPool<byte>.Shared.Rent(Math.Max(needed, _buffer.Length * 2));
            _buffer.AsSpan(0, _count).CopyTo(larger);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = larger;
        }
    }
}
