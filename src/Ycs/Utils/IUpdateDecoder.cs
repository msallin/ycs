namespace Ycs;

/// <summary>
/// Every read is handed the input the decoder was made over. A decoder that holds its own buffer ignores it.
/// </summary>
internal interface IDSDecoder
{
    Lib0Reader Reader { get; }

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
