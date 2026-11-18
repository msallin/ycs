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
    /// First half of <see cref="ReadString"/> without the allocation: consumes the string, validates it (the same
    /// exceptions at the same position) and returns its length in UTF-16 units. <see cref="ReadStringChars"/> must
    /// follow before any other read on this decoder.
    /// </summary>
    int ReadStringLength();

    /// <summary>Writes the string <see cref="ReadStringLength"/> consumed into <paramref name="destination"/>, which holds exactly its length.</summary>
    void ReadStringChars(Span<char> destination);
    bool ReadParentInfo();
    uint ReadTypeRef();
    int ReadLength();
    object? ReadAny();
    byte[] ReadBuffer();
    string ReadKey();
    object? ReadJson();
}
