using System.Diagnostics.CodeAnalysis;

namespace Ycs;

/// <summary>
/// A decoder holds offsets into the input and nothing else; every read is handed the input it was made over. The
/// implementations are structs, passed by <c>ref</c>.
/// </summary>
internal interface IDSDecoder
{
    /// <summary>The rest stream, by reference: reads through it advance the decoder.</summary>
    [UnscopedRef]
    ref Lib0Reader Reader { get; }

    void ResetDsCurVal();
    long ReadDsClock(ReadOnlySpan<byte> input);
    long ReadDsLength(ReadOnlySpan<byte> input);
}

internal interface IUpdateDecoder : IDSDecoder
{

    /// <summary>How many structs this update may hold at most (<see cref="UpdateLimits.MaxStructsPerUpdate"/>).</summary>
    long MaxStructs { get; }

    ItemId ReadLeftId(ReadOnlySpan<byte> input);
    ItemId ReadRightId(ReadOnlySpan<byte> input);
    long ReadClient(ReadOnlySpan<byte> input);
    byte ReadInfo(ReadOnlySpan<byte> input);
    string ReadString(ReadOnlySpan<byte> input);

    /// <summary>
    /// First half of <see cref="ReadString"/> without the allocation: consumes the string and returns the room its
    /// characters need, at least their number and at most the input it took. <see cref="ReadStringChars"/> must
    /// follow before any other read on this decoder.
    /// </summary>
    int ReadStringBound(ReadOnlySpan<byte> input);

    /// <summary>The exact length of the string <see cref="ReadStringBound"/> consumed, for a caller that cannot afford the bound's room; rejects invalid UTF-8 as <see cref="ReadString"/> does.</summary>
    int ReadStringExact(ReadOnlySpan<byte> input);

    /// <summary>
    /// Writes the string <see cref="ReadStringBound"/> consumed into <paramref name="destination"/>, which holds at
    /// least the bound, and returns its length in UTF-16 units. Rejects invalid UTF-8 as <see cref="ReadString"/>
    /// does, after the same input has been consumed.
    /// </summary>
    int ReadStringChars(ReadOnlySpan<byte> input, Span<char> destination);
    bool ReadParentInfo(ReadOnlySpan<byte> input);
    uint ReadTypeRef(ReadOnlySpan<byte> input);
    int ReadLength(ReadOnlySpan<byte> input);
    object? ReadAny(ReadOnlySpan<byte> input);
    byte[] ReadBuffer(ReadOnlySpan<byte> input);
    string ReadKey(ReadOnlySpan<byte> input);
    object? ReadJson(ReadOnlySpan<byte> input);
}

/// <summary>A decoder a generic driver can make, as the merge makes one per input.</summary>
internal interface IUpdateDecoder<TSelf> : IUpdateDecoder
    where TSelf : IUpdateDecoder<TSelf>
{
    /// <summary>A decoder of <paramref name="update"/>, which every read is handed again.</summary>
    static abstract TSelf Create(ReadOnlySpan<byte> update, long maxStructs, int maxDepth);
}
