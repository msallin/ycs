// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

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
    public long ReadDsClock()
    {
        _dsCurVal += Reader.ReadVarUint();
        return _dsCurVal;
    }

    public long ReadDsLength()
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
    private readonly StringDecoder _stringDecoder;
    private readonly RleDecoder _parentInfoDecoder;
    private readonly UintOptRleDecoder _typeRefDecoder;
    private readonly UintOptRleDecoder _lengthDecoder;

    /// <param name="input">The update.</param>
    /// <param name="trusted">Written by this library (pending structs): no limit on the number of structs.</param>
    public UpdateDecoderV2(Lib0Reader input, bool trusted = false)
        : base(input)
    {
        MaxStructs = trusted ? long.MaxValue : (long)input.Remaining * Limits.MaxStructsPerByteV2 + Limits.MinStructs;

        // Read feature flag - currently unused.
        Reader.ReadByte();

        // Each column is a reader over a slice of the same buffer; nothing is copied.
        _keyClockDecoder = new IntDiffOptRleDecoder(Reader.ReadVarUint8ArrayAsReader());
        _clientDecoder = new UintOptRleDecoder(Reader.ReadVarUint8ArrayAsReader());
        _leftClockDecoder = new IntDiffOptRleDecoder(Reader.ReadVarUint8ArrayAsReader());
        _rightClockDecoder = new IntDiffOptRleDecoder(Reader.ReadVarUint8ArrayAsReader());
        _infoDecoder = new RleDecoder(Reader.ReadVarUint8ArrayAsReader());
        _stringDecoder = new StringDecoder(Reader.ReadVarUint8ArrayAsReader());
        _parentInfoDecoder = new RleDecoder(Reader.ReadVarUint8ArrayAsReader());
        _typeRefDecoder = new UintOptRleDecoder(Reader.ReadVarUint8ArrayAsReader());
        _lengthDecoder = new UintOptRleDecoder(Reader.ReadVarUint8ArrayAsReader());
    }

    public long MaxStructs { get; }

    public ID ReadLeftId() => new ID(_clientDecoder.Read(), Limits.CheckClock(_leftClockDecoder.Read()));

    public ID ReadRightId() => new ID(_clientDecoder.Read(), Limits.CheckClock(_rightClockDecoder.Read()));

    /// <summary>
    /// Read the next client Id.
    /// </summary>
    public long ReadClient() => _clientDecoder.Read();

    public byte ReadInfo() => _infoDecoder.Read();

    public string ReadString() => _stringDecoder.Read();

    public bool ReadParentInfo() => _parentInfoDecoder.Read() == 1;

    public uint ReadTypeRef() => _typeRefDecoder.Read();

    public int ReadLength()
    {
        var value = _lengthDecoder.Read();
        if (value > Limits.MaxClock)
        {
            throw new YUpdateException("A length is out of range.");
        }

        return (int)value;
    }

    public object ReadAny() => Reader.ReadAny();

    public byte[] ReadBuffer() => Reader.ReadVarUint8Array();

    public string ReadKey()
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

    public object ReadJson() => Reader.ReadAny();
}
