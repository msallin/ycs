using System.Diagnostics.CodeAnalysis;

namespace Ycs;

internal struct DSDecoderV2 : IDSDecoder
{
    private Lib0Reader _reader;
    private long _dsCurVal;

    /// <summary>A decoder of the bytes from <paramref name="start"/> up to <paramref name="end"/> of the input.</summary>
    public DSDecoderV2(int start, int end)
    {
        _reader = new Lib0Reader(start, end);
        _dsCurVal = 0;
    }

    [UnscopedRef]
    public ref Lib0Reader Reader => ref _reader;

    public void ResetDsCurVal()
    {
        _dsCurVal = 0;
    }

    // At most 2^32 per step and checked by the caller after each range, so the sum cannot overflow.
    public long ReadDsClock(ReadOnlySpan<byte> input)
    {
        _dsCurVal += _reader.ReadVarUint(input);
        return _dsCurVal;
    }

    public long ReadDsLength(ReadOnlySpan<byte> input)
    {
        var diff = _reader.ReadVarUint(input) + 1L;
        _dsCurVal += diff;
        return diff;
    }
}

internal struct UpdateDecoderV2 : IUpdateDecoder<UpdateDecoderV2>
{
    private Lib0Reader _reader;
    private long _dsCurVal;
    private IntDiffOptRleDecoder _keyClockDecoder;
    private UintOptRleDecoder _clientDecoder;
    private IntDiffOptRleDecoder _leftClockDecoder;
    private IntDiffOptRleDecoder _rightClockDecoder;
    private RleDecoder _infoDecoder;
    private StringDecoder _stringDecoder;
    private RleDecoder _parentInfoDecoder;
    private UintOptRleDecoder _typeRefDecoder;
    private UintOptRleDecoder _lengthDecoder;

    /// <summary>
    /// List of cached keys. If the keys[id] does not exist, we read a new key from
    /// the string encoder and push it to keys.
    /// </summary>
    private readonly List<string> _keys;

    /// <param name="update">The update this decoder reads, which every read is handed again.</param>
    /// <param name="maxStructs">
    /// The most structs the update may hold (<see cref="UpdateLimits.MaxStructsPerUpdate"/>); for an
    /// update this library wrote (pending structs), what it counted when it wrote them.
    /// </param>
    /// <param name="maxDepth">How deeply the values may be nested (<see cref="UpdateLimits.MaxDepth"/>).</param>
    /// <param name="keys">The empty list the keys read are cached in; the caller owns it, so a reused one allocates nothing.</param>
    public UpdateDecoderV2(ReadOnlySpan<byte> update, long maxStructs, int maxDepth, List<string> keys)
    {
        _reader = new Lib0Reader(0, update.Length, maxDepth);
        _dsCurVal = 0;
        MaxStructs = maxStructs;
        _keys = keys;

        // Read feature flag - currently unused.
        _reader.ReadByte(update);

        // Each column is a reader over a slice of the same input; nothing is copied.
        _keyClockDecoder = new IntDiffOptRleDecoder(_reader.ReadVarUint8ArrayAsReader(update));
        _clientDecoder = new UintOptRleDecoder(_reader.ReadVarUint8ArrayAsReader(update), Limits.MaxClient);
        _leftClockDecoder = new IntDiffOptRleDecoder(_reader.ReadVarUint8ArrayAsReader(update));
        _rightClockDecoder = new IntDiffOptRleDecoder(_reader.ReadVarUint8ArrayAsReader(update));
        _infoDecoder = new RleDecoder(_reader.ReadVarUint8ArrayAsReader(update));
        _stringDecoder = new StringDecoder(update, _reader.ReadVarUint8ArrayAsReader(update));
        _parentInfoDecoder = new RleDecoder(_reader.ReadVarUint8ArrayAsReader(update));
        _typeRefDecoder = new UintOptRleDecoder(_reader.ReadVarUint8ArrayAsReader(update));
        _lengthDecoder = new UintOptRleDecoder(_reader.ReadVarUint8ArrayAsReader(update));
    }

    public static UpdateDecoderV2 Create(ReadOnlySpan<byte> update, long maxStructs, int maxDepth) => new(update, maxStructs, maxDepth, []);

    [UnscopedRef]
    public ref Lib0Reader Reader => ref _reader;

    public long MaxStructs { get; }

    public void ResetDsCurVal()
    {
        _dsCurVal = 0;
    }

    // At most 2^32 per step and checked by the caller after each range, so the sum cannot overflow.
    public long ReadDsClock(ReadOnlySpan<byte> input)
    {
        _dsCurVal += _reader.ReadVarUint(input);
        return _dsCurVal;
    }

    public long ReadDsLength(ReadOnlySpan<byte> input)
    {
        var diff = _reader.ReadVarUint(input) + 1L;
        _dsCurVal += diff;
        return diff;
    }

    public ItemId ReadLeftId(ReadOnlySpan<byte> input) => new ItemId((long)_clientDecoder.Read(input), Limits.CheckClock(_leftClockDecoder.Read(input)));

    public ItemId ReadRightId(ReadOnlySpan<byte> input) => new ItemId((long)_clientDecoder.Read(input), Limits.CheckClock(_rightClockDecoder.Read(input)));

    /// <summary>
    /// Read the next client Id.
    /// </summary>
    public long ReadClient(ReadOnlySpan<byte> input) => (long)_clientDecoder.Read(input);

    public byte ReadInfo(ReadOnlySpan<byte> input) => _infoDecoder.Read(input);

    public string ReadString(ReadOnlySpan<byte> input) => _stringDecoder.Read(input);

    public int ReadStringBound(ReadOnlySpan<byte> input) => _stringDecoder.BeginRead(input);

    public readonly int ReadStringExact(ReadOnlySpan<byte> input) => _stringDecoder.SegmentLength;

    public readonly int ReadStringChars(ReadOnlySpan<byte> input, Span<char> destination) => _stringDecoder.EndRead(input, destination);

    public bool ReadParentInfo(ReadOnlySpan<byte> input) => _parentInfoDecoder.Read(input) == 1;

    public uint ReadTypeRef(ReadOnlySpan<byte> input) => (uint)_typeRefDecoder.Read(input);

    public int ReadLength(ReadOnlySpan<byte> input)
    {
        var value = _lengthDecoder.Read(input);
        if (value > Limits.MaxClock)
        {
            throw new YUpdateException("A length is out of range.");
        }

        return (int)value;
    }

    public object? ReadAny(ReadOnlySpan<byte> input) => _reader.ReadAny(input);

    public byte[] ReadBuffer(ReadOnlySpan<byte> input) => _reader.ReadVarUint8Array(input);

    public string ReadKey(ReadOnlySpan<byte> input)
    {
        // As in Yjs: a known index returns a cached key, any higher one reads the next new key.
        var keyClock = _keyClockDecoder.Read(input);
        if (keyClock < 0)
        {
            throw new YUpdateException("A key index is negative.");
        }

        if (keyClock < _keys.Count)
        {
            return _keys[(int)keyClock];
        }

        var key = _stringDecoder.Read(input);
        _keys.Add(key);
        return key;
    }

    public object? ReadJson(ReadOnlySpan<byte> input) => _reader.ReadAny(input);
}
