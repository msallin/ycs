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
    /// <see cref="ReadString"/> without the allocation: the same characters, the same exceptions at the same
    /// position. The span is valid until the next call on this decoder (any read, <c>ReadString</c> included) and
    /// must be copied before; it is never kept past the decoder's lease.
    /// </summary>
    ReadOnlySpan<char> ReadStringSpan();
    bool ReadParentInfo();
    uint ReadTypeRef();
    int ReadLength();
    object ReadAny();
    byte[] ReadBuffer();
    string ReadKey();
    object ReadJson();
}
