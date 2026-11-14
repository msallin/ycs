// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System.Buffers;
using System.Diagnostics;

namespace Ycs;

/// <summary>
/// The original ('v1') Yjs update format: everything is written to a single stream
/// as plain var-ints. It is the format used by <c>Y.encodeStateAsUpdate</c>,
/// <c>y-protocols/sync</c> and every provider built on them, so it is the one
/// on the wire in practice; <see cref="DSEncoderV2"/> is the newer, denser variant.
/// <br/>
/// Because it is a single stream, it can be written straight into the caller's buffer
/// (<see cref="Reset(IBufferWriter{byte})"/>, then <see cref="Flush"/>).
/// </summary>
internal class DSEncoderV1 : IDSEncoder
{
    /// <param name="pooled">Rent the buffer from the pool: <see cref="Reset()"/> must then be called on every path.</param>
    public DSEncoderV1(bool pooled = false)
    {
        RestWriter = new Lib0Writer(256, pooled);
    }

    public Lib0Writer RestWriter { get; }

    // v1 writes absolute clocks, so there is no running diff to reset.
    public void ResetDsCurVal()
    {
        // Nothing to do.
    }

    public void WriteDsClock(long clock)
    {
        Debug.Assert(clock >= 0);
        RestWriter.WriteVarUint((ulong)clock);
    }

    public void WriteDsLength(long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        RestWriter.WriteVarUint((ulong)length);
    }

    public byte[] ToArray() => RestWriter.ToArray();

    public void WriteTo(IBufferWriter<byte> destination) => RestWriter.CopyTo(destination);

    /// <inheritdoc/>
    public void Reset() => RestWriter.Reset();

    /// <summary>
    /// Resets the encoder and writes from now on straight into <paramref name="destination"/>;
    /// <see cref="Flush"/> commits the bytes, <see cref="Reset()"/> ends it. <see cref="ToArray"/>
    /// and <see cref="WriteTo"/> are not available meanwhile.
    /// </summary>
    public void Reset(IBufferWriter<byte> destination)
    {
        Reset();
        RestWriter.StartWritingTo(destination);
    }

    /// <summary>Commits what was written into the destination given to <see cref="Reset(IBufferWriter{byte})"/>.</summary>
    public void Flush() => RestWriter.Flush();
}

internal sealed class UpdateEncoderV1 : DSEncoderV1, IUpdateEncoder
{
    [ThreadStatic]
    private static UpdateEncoderV1? t_free;

    private bool _rented;

    /// <inheritdoc/>
    public UpdateEncoderV1(bool pooled = false)
        : base(pooled)
    {
    }

    /// <summary>
    /// A pooled encoder from this thread's cache (allocation-free once warm). Hand it back with
    /// <see cref="Return"/> in a <c>finally</c>, and do not use it afterwards.
    /// </summary>
    public static UpdateEncoderV1 Rent()
    {
        var encoder = t_free ?? new UpdateEncoderV1(pooled: true);
        t_free = null;
        encoder._rented = true;
        return encoder;
    }

    /// <summary>Resets the encoder (returning its buffer to the pool) and puts it back into this thread's cache.</summary>
    public void Return()
    {
        if (!_rented)
        {
            throw new InvalidOperationException("The encoder was not rented, or was returned already.");
        }

        _rented = false;
        Reset();
        t_free = this;
    }

    public void WriteLeftId(ID id)
    {
        RestWriter.WriteVarUint((ulong)id.Client);
        RestWriter.WriteVarUint((ulong)id.Clock);
    }

    public void WriteRightId(ID id)
    {
        RestWriter.WriteVarUint((ulong)id.Client);
        RestWriter.WriteVarUint((ulong)id.Clock);
    }

    public void WriteClient(long client)
    {
        Debug.Assert(client >= 0);
        RestWriter.WriteVarUint((ulong)client);
    }

    public void WriteInfo(byte info) => RestWriter.WriteByte(info);

    public void WriteString(string s) => RestWriter.WriteVarString(s);

    public void WriteString(ReadOnlySpan<char> s) => RestWriter.WriteVarString(s);

    public void WriteParentInfo(bool isYKey) => RestWriter.WriteVarUint(isYKey ? 1u : 0u);

    public void WriteTypeRef(uint info) => RestWriter.WriteVarUint(info);

    public void WriteLength(int len)
    {
        Debug.Assert(len >= 0);
        RestWriter.WriteVarUint((uint)len);
    }

    public void WriteAny(object any) => RestWriter.WriteAny(any);

    public void WriteBuffer(ReadOnlySpan<byte> data) => RestWriter.WriteVarUint8Array(data);

    public void WriteKey(string key) => RestWriter.WriteVarString(key);

    public void WriteJson(object any) => RestWriter.WriteVarString(YJson.Stringify(any));
}
