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
/// </summary>
internal class DSEncoderV1 : IDSEncoder
{
    public Lib0Writer RestWriter { get; } = new Lib0Writer(256);

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
}

internal sealed class UpdateEncoderV1 : DSEncoderV1, IUpdateEncoder
{
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
