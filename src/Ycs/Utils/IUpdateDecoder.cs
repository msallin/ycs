namespace Ycs;

internal interface IDSDecoder
{
    Lib0Reader Reader { get; }

    void ResetDsCurVal();
    long ReadDsClock();
    long ReadDsLength();
}

internal interface IUpdateDecoder : IDSDecoder
{
    /// <summary>How many structs this update may hold at most (<see cref="UpdateLimits.MaxStructsPerUpdate"/>).</summary>
    long MaxStructs { get; }

    ItemId ReadLeftId();
    ItemId ReadRightId();
    long ReadClient();
    byte ReadInfo();
    string ReadString();

    /// <summary>
    /// First half of <see cref="ReadString"/> without the allocation: consumes the string and returns the room its
    /// characters need, at least their number and at most the input it took. <see cref="ReadStringChars"/> must
    /// follow before any other read on this decoder.
    /// </summary>
    int ReadStringBound();

    /// <summary>The exact length of the string <see cref="ReadStringBound"/> consumed, for a caller that cannot afford the bound's room; rejects invalid UTF-8 as <see cref="ReadString"/> does.</summary>
    int ReadStringExact();

    /// <summary>
    /// Writes the string <see cref="ReadStringBound"/> consumed into <paramref name="destination"/>, which holds at
    /// least the bound, and returns its length in UTF-16 units. Rejects invalid UTF-8 as <see cref="ReadString"/>
    /// does, after the same input has been consumed.
    /// </summary>
    int ReadStringChars(Span<char> destination);
    bool ReadParentInfo();
    uint ReadTypeRef();
    int ReadLength();
    object? ReadAny();
    byte[] ReadBuffer();
    string ReadKey();
    object? ReadJson();
}
