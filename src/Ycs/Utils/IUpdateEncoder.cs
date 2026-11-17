using System.Buffers;

namespace Ycs;

internal interface IDSEncoder
{
    Lib0Writer RestWriter { get; }

    byte[] ToArray();

    /// <summary>
    /// Writes the encoded bytes to <paramref name="destination"/>.
    /// </summary>
    void WriteTo(IBufferWriter<byte> destination);

    /// <summary>
    /// Makes the encoder as new, so one encoder can write any number of updates: forgets what was
    /// written and all running state. A pooled encoder returns its buffers to the pool (it must be
    /// reset on every path, exceptions included); an unpooled one keeps a buffer up to 64 KiB for the
    /// next update. Resetting twice is harmless.
    /// </summary>
    void Reset();

    /// <summary>
    /// Resets the ds value to 0.
    /// The v2 encoder uses this information to reset the initial diff value.
    /// </summary>
    void ResetDsCurVal();

    void WriteDsClock(long clock);
    void WriteDsLength(long length);
}

internal interface IUpdateEncoder : IDSEncoder
{
    void WriteLeftId(ItemId id);
    void WriteRightId(ItemId id);

    /// <summary>
    /// NOTE: Use 'writeClient' and 'writeClock' instead of writeID if possible.
    /// </summary>
    void WriteClient(long client);

    void WriteInfo(byte info);
    void WriteString(string s);
    void WriteString(ReadOnlySpan<char> s);
    void WriteParentInfo(bool isYKey);
    void WriteTypeRef(uint info);

    /// <summary>
    /// Write len of a struct - well suited for Opt RLE encoder.
    /// </summary>
    void WriteLength(int len);

    void WriteAny(object? any);
    void WriteBuffer(ReadOnlySpan<byte> buf);
    void WriteKey(string key);
    void WriteJson(object? any);
}
