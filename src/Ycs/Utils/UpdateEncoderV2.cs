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

    /// <param name="pooled">Rent the buffers from the pool: <see cref="Reset"/> must then be called on every path.</param>
    public DSEncoderV2(bool pooled = false)
    {
        RestWriter = new Lib0Writer(256, pooled);
    }

    public Lib0Writer RestWriter { get; }

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

    /// <inheritdoc/>
    public virtual void Reset()
    {
        RestWriter.Reset();
        _dsCurVal = 0;
    }
}

/// <summary>
/// The v2 update format: the struct fields go to nine run-length or diff encoded columns, and the
/// rest (clocks, contents, the delete set) to <see cref="DSEncoderV2.RestWriter"/>. The columns come
/// first in the output and their lengths are only known at the end, so the bytes are assembled from
/// the column buffers then: into one exact array (<see cref="ToArray"/>) or the caller's writer
/// (<see cref="WriteTo"/>), one copy either way.
/// </summary>
internal sealed class UpdateEncoderV2 : DSEncoderV2, IUpdateEncoder
{
    [ThreadStatic]
    private static UpdateEncoderV2? t_free;

    private bool _rented;

    // Refers to the next unique key-identifier to be used.
    private int _keyClock;

    private readonly IntDiffOptRleEncoder _keyClockEncoder;
    private readonly UintOptRleEncoder _clientEncoder;
    private readonly IntDiffOptRleEncoder _leftClockEncoder;
    private readonly IntDiffOptRleEncoder _rightClockEncoder;
    private readonly RleEncoder _infoEncoder;
    private readonly StringEncoder _stringEncoder;
    private readonly RleEncoder _parentInfoEncoder;
    private readonly UintOptRleEncoder _typeRefEncoder;
    private readonly UintOptRleEncoder _lengthEncoder;

    /// <inheritdoc/>
    public UpdateEncoderV2(bool pooled = false)
        : base(pooled)
    {
        _keyClockEncoder = new(pooled);
        _clientEncoder = new(pooled);
        _leftClockEncoder = new(pooled);
        _rightClockEncoder = new(pooled);
        _infoEncoder = new(pooled);
        _stringEncoder = new(pooled);
        _parentInfoEncoder = new(pooled);
        _typeRefEncoder = new(pooled);
        _lengthEncoder = new(pooled);
    }

    /// <summary>
    /// A pooled encoder from this thread's cache (allocation-free once warm). Hand it back with
    /// <see cref="Return"/> in a <c>finally</c>, and do not use it afterwards.
    /// </summary>
    public static UpdateEncoderV2 Rent()
    {
        var encoder = t_free ?? new UpdateEncoderV2(pooled: true);
        t_free = null;
        encoder._rented = true;
        return encoder;
    }

    /// <summary>Resets the encoder (returning its buffers to the pool) and puts it back into this thread's cache.</summary>
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

    /// <inheritdoc/>
    public override void Reset()
    {
        base.Reset();
        _keyClock = 0;
        _keyClockEncoder.Reset();
        _clientEncoder.Reset();
        _leftClockEncoder.Reset();
        _rightClockEncoder.Reset();
        _infoEncoder.Reset();
        _stringEncoder.Reset();
        _parentInfoEncoder.Reset();
        _typeRefEncoder.Reset();
        _lengthEncoder.Reset();
    }

    /// <summary>The update as one array of exactly its size. Finishes the columns: write nothing more before <see cref="Reset"/>.</summary>
    public override byte[] ToArray()
    {
        var result = new byte[GetEncodedLength()];
        var sink = new SpanSink(result);
        WriteAll(ref sink);
        Debug.Assert(sink.Position == result.Length);
        return result;
    }

    /// <summary>Writes the update into <paramref name="destination"/>. Finishes the columns: write nothing more before <see cref="Reset"/>.</summary>
    public override void WriteTo(IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var sink = new BufferWriterSink(destination);
        WriteAll(ref sink);
    }

    private int GetEncodedLength()
    {
        // The feature flag, the columns (each with its length), the rest.
        return checked(1
            + ColumnSize(_keyClockEncoder.GetWrittenSpan().Length)
            + ColumnSize(_clientEncoder.GetWrittenSpan().Length)
            + ColumnSize(_leftClockEncoder.GetWrittenSpan().Length)
            + ColumnSize(_rightClockEncoder.GetWrittenSpan().Length)
            + ColumnSize(_infoEncoder.GetWrittenSpan().Length)
            + ColumnSize(_stringEncoder.GetColumnLength())
            + ColumnSize(_parentInfoEncoder.GetWrittenSpan().Length)
            + ColumnSize(_typeRefEncoder.GetWrittenSpan().Length)
            + ColumnSize(_lengthEncoder.GetWrittenSpan().Length)
            + RestWriter.Length);

        static int ColumnSize(int length) => Lib0Writer.VarUintSize((uint)length) + length;
    }

    private void WriteAll<TSink>(ref TSink sink)
        where TSink : struct, IByteSink
    {
        // Feature flag that might be used in the future.
        sink.WriteVarUint(0);

        WriteColumn(ref sink, _keyClockEncoder.GetWrittenSpan());
        WriteColumn(ref sink, _clientEncoder.GetWrittenSpan());
        WriteColumn(ref sink, _leftClockEncoder.GetWrittenSpan());
        WriteColumn(ref sink, _rightClockEncoder.GetWrittenSpan());
        WriteColumn(ref sink, _infoEncoder.GetWrittenSpan());

        sink.WriteVarUint((uint)_stringEncoder.GetColumnLength());
        var utf8 = _stringEncoder.GetUtf8();
        sink.WriteVarUint((uint)utf8.Length);
        sink.Write(utf8);
        sink.Write(_stringEncoder.GetLengths());

        WriteColumn(ref sink, _parentInfoEncoder.GetWrittenSpan());
        WriteColumn(ref sink, _typeRefEncoder.GetWrittenSpan());
        WriteColumn(ref sink, _lengthEncoder.GetWrittenSpan());

        // Append the rest of the data from the RestWriter.
        // Note it's not the 'WriteVarUint8Array'.
        sink.Write(RestWriter.WrittenSpan);

        static void WriteColumn(ref TSink sink, ReadOnlySpan<byte> column)
        {
            sink.WriteVarUint((uint)column.Length);
            sink.Write(column);
        }
    }

    public void WriteLeftId(ID id)
    {
        _clientEncoder.Write((ulong)id.Client);
        _leftClockEncoder.Write(id.Clock);
    }

    public void WriteRightId(ID id)
    {
        _clientEncoder.Write((ulong)id.Client);
        _rightClockEncoder.Write(id.Clock);
    }

    public void WriteClient(long client) => _clientEncoder.Write((ulong)client);

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

    /// <summary>Where <see cref="WriteAll"/> puts the bytes; a struct, so the generic code is specialized per sink.</summary>
    private interface IByteSink
    {
        void Write(ReadOnlySpan<byte> bytes);

        void WriteVarUint(uint num);
    }

    private struct SpanSink : IByteSink
    {
        private readonly byte[] _destination;

        public SpanSink(byte[] destination) => _destination = destination;

        public int Position { get; private set; }

        public void Write(ReadOnlySpan<byte> bytes)
        {
            bytes.CopyTo(_destination.AsSpan(Position));
            Position += bytes.Length;
        }

        public void WriteVarUint(uint num) => Position += Lib0Writer.WriteVarUint(_destination.AsSpan(Position), num);
    }

    private readonly struct BufferWriterSink : IByteSink
    {
        private readonly IBufferWriter<byte> _destination;

        public BufferWriterSink(IBufferWriter<byte> destination) => _destination = destination;

        public void Write(ReadOnlySpan<byte> bytes) => _destination.Write(bytes);

        public void WriteVarUint(uint num) => Lib0Writer.WriteVarUint(_destination, num);
    }
}
