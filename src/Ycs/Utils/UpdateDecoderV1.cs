// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

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

    public long ReadDsClock() => Reader.ReadVarUint();

    public long ReadDsLength() => Reader.ReadVarUint();
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

    public ID ReadLeftId() => new ID(Reader.ReadVarUint53(), Limits.CheckClock(Reader.ReadVarUint()));

    public ID ReadRightId() => new ID(Reader.ReadVarUint53(), Limits.CheckClock(Reader.ReadVarUint()));

    public long ReadClient() => Reader.ReadVarUint53();

    public byte ReadInfo() => Reader.ReadByte();

    public string ReadString() => Reader.ReadVarString();

    public bool ReadParentInfo() => Reader.ReadVarUint() == 1;

    public uint ReadTypeRef() => Reader.ReadVarUint();

    public int ReadLength()
    {
        var value = Reader.ReadVarUint();
        if (value > Limits.MaxClock)
        {
            throw new YUpdateException("A length is out of range.");
        }

        return (int)value;
    }

    public object ReadAny() => Reader.ReadAny();

    public byte[] ReadBuffer() => Reader.ReadVarUint8Array();

    public string ReadKey() => Reader.ReadVarString();

    // Plain CLR values: the same content may be re-encoded with the v2 encoder,
    // which writes it through WriteAny.
    public object ReadJson() => YJson.Parse(Reader.ReadVarUint8ArraySpan());
}
