// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System.Buffers;
using System.Diagnostics;

namespace Ycs;

internal class DSEncoderV2 : IDSEncoder
{
    private long _dsCurVal;

    public Lib0Writer RestWriter { get; } = new Lib0Writer(256);

    public void ResetDsCurVal()
    {
        _dsCurVal = 0;
    }

    public void WriteDsClock(long clock)
    {
        var diff = clock - _dsCurVal;
        Debug.Assert(diff >= 0);
        _dsCurVal = clock;
        RestWriter.WriteVarUint((ulong)diff);
    }

    public void WriteDsLength(long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        RestWriter.WriteVarUint((ulong)(length - 1));
        _dsCurVal += length;
    }

    public virtual byte[] ToArray() => RestWriter.ToArray();

    public virtual void WriteTo(IBufferWriter<byte> destination) => RestWriter.CopyTo(destination);
}

internal sealed class UpdateEncoderV2 : DSEncoderV2, IUpdateEncoder
{
    // Refers to the next unique key-identifier to be used.
    private int _keyClock;

    private readonly IntDiffOptRleEncoder _keyClockEncoder = new();
    private readonly UintOptRleEncoder _clientEncoder = new();
    private readonly IntDiffOptRleEncoder _leftClockEncoder = new();
    private readonly IntDiffOptRleEncoder _rightClockEncoder = new();
    private readonly RleEncoder _infoEncoder = new();
    private readonly StringEncoder _stringEncoder = new();
    private readonly RleEncoder _parentInfoEncoder = new();
    private readonly UintOptRleEncoder _typeRefEncoder = new();
    private readonly UintOptRleEncoder _lengthEncoder = new();

    public override byte[] ToArray()
    {
        var output = new ArrayBufferWriter<byte>(RestWriter.Length + 256);
        WriteTo(output);
        return output.WrittenSpan.ToArray();
    }

    public override void WriteTo(IBufferWriter<byte> destination)
    {
        // Feature flag that might be used in the future.
        destination.GetSpan(1)[0] = 0;
        destination.Advance(1);

        WriteColumn(destination, _keyClockEncoder.GetWrittenSpan());
        WriteColumn(destination, _clientEncoder.GetWrittenSpan());
        WriteColumn(destination, _leftClockEncoder.GetWrittenSpan());
        WriteColumn(destination, _rightClockEncoder.GetWrittenSpan());
        WriteColumn(destination, _infoEncoder.GetWrittenSpan());
        WriteColumn(destination, _stringEncoder.GetWrittenSpan());
        WriteColumn(destination, _parentInfoEncoder.GetWrittenSpan());
        WriteColumn(destination, _typeRefEncoder.GetWrittenSpan());
        WriteColumn(destination, _lengthEncoder.GetWrittenSpan());

        // Append the rest of the data from the RestWriter.
        // Note it's not the 'WriteVarUint8Array'.
        RestWriter.CopyTo(destination);

        static void WriteColumn(IBufferWriter<byte> destination, ReadOnlySpan<byte> column)
        {
            Lib0Writer.WriteVarUint(destination, (uint)column.Length);
            destination.Write(column);
        }
    }

    public void WriteLeftId(ID id)
    {
        _clientEncoder.Write((uint)id.Client);
        _leftClockEncoder.Write(id.Clock);
    }

    public void WriteRightId(ID id)
    {
        _clientEncoder.Write((uint)id.Client);
        _rightClockEncoder.Write(id.Clock);
    }

    public void WriteClient(long client) => _clientEncoder.Write((uint)client);

    public void WriteInfo(byte info) => _infoEncoder.Write(info);

    public void WriteString(string s) => _stringEncoder.Write(s);

    public void WriteString(ReadOnlySpan<char> s) => _stringEncoder.Write(s);

    public void WriteParentInfo(bool isYKey) => _parentInfoEncoder.Write((byte)(isYKey ? 1 : 0));

    public void WriteTypeRef(uint info) => _typeRefEncoder.Write(info);

    public void WriteLength(int len)
    {
        Debug.Assert(len >= 0);
        _lengthEncoder.Write((uint)len);
    }

    public void WriteAny(object any) => RestWriter.WriteAny(any);

    public void WriteBuffer(ReadOnlySpan<byte> data) => RestWriter.WriteVarUint8Array(data);

    /// <summary>
    /// Property keys are often reused. For example, in y-prosemirror the key 'bold'
    /// might occur very often. For a 3D application, the key 'position' might occur often.
    /// <br/>
    /// Yjs could cache these keys in a map and refer to them via a unique number, but it
    /// never enabled that (see <c>UpdateEncoderV2#writeKey</c>): every key gets a fresh
    /// clock and is written out. We do the same so the bytes match.
    /// </summary>
    public void WriteKey(string key)
    {
        _keyClockEncoder.Write(_keyClock++);
        _stringEncoder.Write(key);
    }

    /// <summary>
    /// Unlike v1, the v2 format writes embeds and formatting attributes with the
    /// generic 'any' encoding rather than as a JSON string (see Yjs'
    /// UpdateEncoderV2#writeJSON).
    /// </summary>
    public void WriteJson(object any) => RestWriter.WriteAny(any);
}
