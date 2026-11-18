namespace Ycs;

/// <summary>
/// Reader for the original ('v1') Yjs update format. See <see cref="DSEncoderV1"/>.
/// </summary>
internal class DSDecoderV1 : IDSDecoder
{
    public DSDecoderV1(Lib0Reader input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Reader = input;
    }

    public Lib0Reader Reader { get; }

    public void ResetDsCurVal()
    {
        // Nothing to do.
    }

    public long ReadDsClock(ReadOnlySpan<byte> input) => Reader.ReadVarUint();

    public long ReadDsLength(ReadOnlySpan<byte> input) => Reader.ReadVarUint();
}

internal sealed class UpdateDecoderV1 : DSDecoderV1, IUpdateDecoder
{
    /// <param name="maxStructs">The most structs the update may hold (<see cref="UpdateLimits.MaxStructsPerUpdate"/>).</param>
    public UpdateDecoderV1(Lib0Reader input, long maxStructs)
        : base(input)
    {
        MaxStructs = maxStructs;
    }

    public long MaxStructs { get; set; }

    public ItemId ReadLeftId(ReadOnlySpan<byte> input) => new ItemId(Reader.ReadVarUint53(), Limits.CheckClock(Reader.ReadVarUint()));

    public ItemId ReadRightId(ReadOnlySpan<byte> input) => new ItemId(Reader.ReadVarUint53(), Limits.CheckClock(Reader.ReadVarUint()));

    public long ReadClient(ReadOnlySpan<byte> input) => Reader.ReadVarUint53();

    public byte ReadInfo(ReadOnlySpan<byte> input) => Reader.ReadByte();

    public string ReadString(ReadOnlySpan<byte> input) => Reader.ReadVarString();

    public int ReadStringBound(ReadOnlySpan<byte> input) => Reader.BeginVarString();

    public int ReadStringExact(ReadOnlySpan<byte> input) => Reader.PendingStringLength();

    public int ReadStringChars(ReadOnlySpan<byte> input, Span<char> destination) => Reader.EndVarString(destination);

    public bool ReadParentInfo(ReadOnlySpan<byte> input) => Reader.ReadVarUint() == 1;

    public uint ReadTypeRef(ReadOnlySpan<byte> input) => Reader.ReadVarUint();

    public int ReadLength(ReadOnlySpan<byte> input)
    {
        var value = Reader.ReadVarUint();
        if (value > Limits.MaxClock)
        {
            throw new YUpdateException("A length is out of range.");
        }

        return (int)value;
    }

    public object? ReadAny(ReadOnlySpan<byte> input) => Reader.ReadAny();

    public byte[] ReadBuffer(ReadOnlySpan<byte> input) => Reader.ReadVarUint8Array();

    public string ReadKey(ReadOnlySpan<byte> input) => Reader.ReadVarString();

    // Plain CLR values: the same content may be re-encoded with the v2 encoder,
    // which writes it through WriteAny.
    public object? ReadJson(ReadOnlySpan<byte> input) => YJson.Parse(Reader.ReadVarUint8ArraySpan(), Reader.MaxDepth);
}
