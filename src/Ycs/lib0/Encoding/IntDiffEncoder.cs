namespace Ycs;

/// <summary>
/// Basic diff encoder using variable length encoding.
/// Encodes the values <c>[3, 1100, 1101, 1050, 0]</c> to <c>[3, 1097, 1, -51, -1050]</c>.
/// </summary>
/// <seealso cref="IntDiffDecoder"/>
internal class IntDiffEncoder : AbstractStreamEncoder<long>
{
    private long _state;

    private readonly long _start;

    public IntDiffEncoder(long start)
    {
        _start = start;
        _state = start;
    }

    /// <inheritdoc/>
    protected override void WriteValue(long value)
    {
        Writer.WriteVarInt(value - _state);
        _state = value;
    }

    public override void Reset()
    {
        base.Reset();
        _state = _start;
    }
}
