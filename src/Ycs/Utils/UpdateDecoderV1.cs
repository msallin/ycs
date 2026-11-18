using System.Diagnostics.CodeAnalysis;

namespace Ycs;

/// <summary>
/// Reader for the original ('v1') Yjs update format. See <see cref="DSEncoderV1"/>.
/// </summary>
internal struct DSDecoderV1 : IDSDecoder
{
    private Lib0Reader _reader;

    /// <summary>A decoder of the bytes from <paramref name="start"/> up to <paramref name="end"/> of the input.</summary>
    public DSDecoderV1(int start, int end)
    {
        _reader = new Lib0Reader(start, end);
    }

    [UnscopedRef]
    public ref Lib0Reader Reader => ref _reader;

    public readonly void ResetDsCurVal()
    {
        // Nothing to do.
    }

    public long ReadDsClock(ReadOnlySpan<byte> input) => _reader.ReadVarUint(input);

    public long ReadDsLength(ReadOnlySpan<byte> input) => _reader.ReadVarUint(input);
}

internal struct UpdateDecoderV1 : IUpdateDecoder<UpdateDecoderV1>
{
    private Lib0Reader _reader;

    /// <param name="update">The update this decoder reads, which every read is handed again.</param>
    /// <param name="maxStructs">The most structs the update may hold (<see cref="UpdateLimits.MaxStructsPerUpdate"/>).</param>
    /// <param name="maxDepth">How deeply the values may be nested (<see cref="UpdateLimits.MaxDepth"/>).</param>
    public UpdateDecoderV1(ReadOnlySpan<byte> update, long maxStructs, int maxDepth)
    {
        _reader = new Lib0Reader(0, update.Length, maxDepth);
        MaxStructs = maxStructs;
    }

    public static UpdateDecoderV1 Create(ReadOnlySpan<byte> update, long maxStructs, int maxDepth) => new(update, maxStructs, maxDepth);

    [UnscopedRef]
    public ref Lib0Reader Reader => ref _reader;

    public long MaxStructs { get; }

    public readonly void ResetDsCurVal()
    {
        // Nothing to do.
    }

    public long ReadDsClock(ReadOnlySpan<byte> input) => _reader.ReadVarUint(input);

    public long ReadDsLength(ReadOnlySpan<byte> input) => _reader.ReadVarUint(input);

    public ItemId ReadLeftId(ReadOnlySpan<byte> input) => new ItemId(_reader.ReadVarUint53(input), Limits.CheckClock(_reader.ReadVarUint(input)));

    public ItemId ReadRightId(ReadOnlySpan<byte> input) => new ItemId(_reader.ReadVarUint53(input), Limits.CheckClock(_reader.ReadVarUint(input)));

    public long ReadClient(ReadOnlySpan<byte> input) => _reader.ReadVarUint53(input);

    public byte ReadInfo(ReadOnlySpan<byte> input) => _reader.ReadByte(input);

    public string ReadString(ReadOnlySpan<byte> input) => _reader.ReadVarString(input);

    public int ReadStringBound(ReadOnlySpan<byte> input) => _reader.BeginVarString(input);

    public int ReadStringExact(ReadOnlySpan<byte> input) => _reader.PendingStringLength(input);

    public int ReadStringChars(ReadOnlySpan<byte> input, Span<char> destination) => _reader.EndVarString(input, destination);

    public bool ReadParentInfo(ReadOnlySpan<byte> input) => _reader.ReadVarUint(input) == 1;

    public uint ReadTypeRef(ReadOnlySpan<byte> input) => _reader.ReadVarUint(input);

    public int ReadLength(ReadOnlySpan<byte> input)
    {
        var value = _reader.ReadVarUint(input);
        if (value > Limits.MaxClock)
        {
            throw new YUpdateException("A length is out of range.");
        }

        return (int)value;
    }

    public object? ReadAny(ReadOnlySpan<byte> input) => _reader.ReadAny(input);

    public byte[] ReadBuffer(ReadOnlySpan<byte> input) => _reader.ReadVarUint8Array(input);

    public string ReadKey(ReadOnlySpan<byte> input) => _reader.ReadVarString(input);

    // Plain CLR values: the same content may be re-encoded with the v2 encoder,
    // which writes it through WriteAny.
    public object? ReadJson(ReadOnlySpan<byte> input) => YJson.Parse(_reader.ReadVarUint8ArraySpan(input), _reader.MaxDepth);
}
