namespace Ycs;

/// <summary>
/// A placeholder struct that represents a range of clock values for which we don't have any
/// data (yet). It never gets integrated into a document; it only exists transiently while
/// merging/diffing raw update byte streams (see <see cref="UpdateOperations"/>), where a
/// client's structs might not be contiguous because we only have part of its history.
/// </summary>
internal sealed class Skip : AbstractStruct
{
    internal const byte StructSkipRefNumber = 10;

    internal Skip(ID id, int length)
        : base(id, length)
    {
        // Do nothing.
    }

    public override bool Deleted => true;

    internal override bool CanMergeWith(AbstractStruct right) => right is Skip;

    internal override bool MergeWith(AbstractStruct right)
    {
        if (!CanMergeWith(right))
        {
            return false;
        }

        Length += right.Length;
        return true;
    }

    internal override void Write(IUpdateEncoder encoder, int offset)
    {
        encoder.WriteInfo(StructSkipRefNumber);

        // Write as VarUint because Skips can't make use of predictable length-encoding.
        encoder.RestWriter.WriteVarUint((uint)(Length - offset));
    }
}
