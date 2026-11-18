namespace Ycs;

internal class DSDecoderV2 : IDSDecoder
{
    private long _dsCurVal;

    public DSDecoderV2(Lib0Reader input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Reader = input;
    }

    public Lib0Reader Reader { get; }

    public void ResetDsCurVal()
    {
        _dsCurVal = 0;
    }

    // At most 2^32 per step and checked by the caller after each range, so the sum cannot overflow.
    public long ReadDsClock(ReadOnlySpan<byte> input)
    {
        _dsCurVal += Reader.ReadVarUint();
        return _dsCurVal;
    }

    public long ReadDsLength(ReadOnlySpan<byte> input)
    {
        var diff = Reader.ReadVarUint() + 1L;
        _dsCurVal += diff;
        return diff;
    }
}

internal sealed class UpdateDecoderV2 : DSDecoderV2, IUpdateDecoder
{
    /// <summary>
    /// List of cached keys. If the keys[id] does not exist, we read a new key from
    /// the string encoder and push it to keys.
    /// </summary>
    private readonly List<string> _keys = new();
    private readonly IntDiffOptRleDecoder _keyClockDecoder;
    private readonly UintOptRleDecoder _clientDecoder;
    private readonly IntDiffOptRleDecoder _leftClockDecoder;
    private readonly IntDiffOptRleDecoder _rightClockDecoder;
    private readonly RleDecoder _infoDecoder;
    // Made by the constructor's ReadColumns; Reset only refills it.
    private StringDecoder _stringDecoder = null!;
    private readonly RleDecoder _parentInfoDecoder;
    private readonly UintOptRleDecoder _typeRefDecoder;
    private readonly UintOptRleDecoder _lengthDecoder;

    /// <param name="input">The update.</param>
    /// <param name="maxStructs">
    /// The most structs the update may hold (<see cref="UpdateLimits.MaxStructsPerUpdate"/>); for an
    /// update this library wrote (pending structs), what it counted when it wrote them.
    /// </param>
    public UpdateDecoderV2(Lib0Reader input, long maxStructs)
        : base(input)
    {
        _keyClockDecoder = new IntDiffOptRleDecoder(new Lib0Reader());
        _clientDecoder = new UintOptRleDecoder(new Lib0Reader(), Limits.MaxClient);
        _leftClockDecoder = new IntDiffOptRleDecoder(new Lib0Reader());
        _rightClockDecoder = new IntDiffOptRleDecoder(new Lib0Reader());
        _infoDecoder = new RleDecoder(new Lib0Reader());
        _parentInfoDecoder = new RleDecoder(new Lib0Reader());
        _typeRefDecoder = new UintOptRleDecoder(new Lib0Reader());
        _lengthDecoder = new UintOptRleDecoder(new Lib0Reader());
        ReadColumns(maxStructs, createStringDecoder: true);
    }

    public long MaxStructs { get; private set; }

    /// <summary>
    /// Starts over on a new update: <see cref="DSDecoderV2.Reader"/> must have been pointed at it
    /// (<see cref="Lib0Reader.Reset"/>). The column decoders and their readers are reused.
    /// </summary>
    public void Reset(long maxStructs)
    {
        ResetDsCurVal();
        _keys.Clear();
        ReadColumns(maxStructs, createStringDecoder: false);
    }

    /// <summary>Drops the references to the input and to the decoded strings.</summary>
    public void Clear()
    {
        Reader.Clear();
        _keys.Clear();
        _keyClockDecoder.Input.Clear();
        _clientDecoder.Input.Clear();
        _leftClockDecoder.Input.Clear();
        _rightClockDecoder.Input.Clear();
        _infoDecoder.Input.Clear();
        _stringDecoder.Clear();
        _parentInfoDecoder.Input.Clear();
        _typeRefDecoder.Input.Clear();
        _lengthDecoder.Input.Clear();
    }

    private void ReadColumns(long maxStructs, bool createStringDecoder)
    {
        MaxStructs = maxStructs;

        // Read feature flag - currently unused.
        Reader.ReadByte();

        // Each column is a reader over a slice of the same buffer; nothing is copied.
        ReadColumn(_keyClockDecoder);
        ReadColumn(_clientDecoder);
        ReadColumn(_leftClockDecoder);
        ReadColumn(_rightClockDecoder);
        ReadColumn(_infoDecoder);

        if (createStringDecoder)
        {
            _stringDecoder = new StringDecoder(Reader.ReadVarUint8ArrayAsReader());
        }
        else
        {
            Reader.ReadVarUint8ArrayInto(_stringDecoder.Input);
            _stringDecoder.Reset();
        }

        ReadColumn(_parentInfoDecoder);
        ReadColumn(_typeRefDecoder);
        ReadColumn(_lengthDecoder);
    }

    private void ReadColumn<T>(AbstractStreamDecoder<T> decoder)
    {
        Reader.ReadVarUint8ArrayInto(decoder.Input);
        decoder.Reset();
    }


    public ItemId ReadLeftId(ReadOnlySpan<byte> input) => new ItemId((long)_clientDecoder.Read(), Limits.CheckClock(_leftClockDecoder.Read()));

    public ItemId ReadRightId(ReadOnlySpan<byte> input) => new ItemId((long)_clientDecoder.Read(), Limits.CheckClock(_rightClockDecoder.Read()));

    /// <summary>
    /// Read the next client Id.
    /// </summary>
    public long ReadClient(ReadOnlySpan<byte> input) => (long)_clientDecoder.Read();

    public byte ReadInfo(ReadOnlySpan<byte> input) => _infoDecoder.Read();

    public string ReadString(ReadOnlySpan<byte> input) => _stringDecoder.Read();

    public int ReadStringBound(ReadOnlySpan<byte> input) => _stringDecoder.BeginRead();

    public int ReadStringExact(ReadOnlySpan<byte> input) => _stringDecoder.SegmentLength;

    public int ReadStringChars(ReadOnlySpan<byte> input, Span<char> destination) => _stringDecoder.EndRead(destination);

    public bool ReadParentInfo(ReadOnlySpan<byte> input) => _parentInfoDecoder.Read() == 1;

    public uint ReadTypeRef(ReadOnlySpan<byte> input) => (uint)_typeRefDecoder.Read();

    public int ReadLength(ReadOnlySpan<byte> input)
    {
        var value = _lengthDecoder.Read();
        if (value > Limits.MaxClock)
        {
            throw new YUpdateException("A length is out of range.");
        }

        return (int)value;
    }

    public object? ReadAny(ReadOnlySpan<byte> input) => Reader.ReadAny();

    public byte[] ReadBuffer(ReadOnlySpan<byte> input) => Reader.ReadVarUint8Array();

    public string ReadKey(ReadOnlySpan<byte> input)
    {
        // As in Yjs: a known index returns a cached key, any higher one reads the next new key.
        var keyClock = _keyClockDecoder.Read();
        if (keyClock < 0)
        {
            throw new YUpdateException("A key index is negative.");
        }

        if (keyClock < _keys.Count)
        {
            return _keys[(int)keyClock];
        }

        var key = _stringDecoder.Read();
        _keys.Add(key);
        return key;
    }

    public object? ReadJson(ReadOnlySpan<byte> input) => Reader.ReadAny();
}
