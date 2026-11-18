using System.Diagnostics;

namespace Ycs;

/// <summary>
/// Reads one struct (an item, a GC or a Skip) at a time from an update, across client boundaries, without
/// materializing the whole update or integrating anything into a document (Yjs' <c>LazyStructReader</c>,
/// src/utils/updates.js). The struct is staged into the reader's <see cref="StagingTable"/> straight from the decoder
/// (no struct object); an item's parent stays unresolved, a root type's name or the id of the parent's item. Used by
/// the update-manipulation utilities in <see cref="UpdateOperations"/>.
/// </summary>
internal sealed class LazyStructReader<TDecoder>
    where TDecoder : struct, IUpdateDecoder
{
    // Not readonly: Next() advances it in place.
    private EncodingUtils.StructCursor _cursor;
    private TDecoder _decoder;
    private readonly bool _filterSkips;

    /// <param name="table">An empty table, the reader's own until the update is read.</param>
    /// <remarks>
    /// The table holds the current struct only (it is <see cref="StagingTable.Truncate"/>d before each read), so what a
    /// caller needs of an earlier one it copies first.
    /// </remarks>
    /// <param name="input">The input the decoder was made over; every <see cref="Next"/> is handed the same.</param>
    public LazyStructReader(TDecoder decoder, ReadOnlySpan<byte> input, bool filterSkips, StagingTable table)
    {
        _decoder = decoder;
        _cursor = EncodingUtils.StructCursor.Begin(ref _decoder, input, table);
        _filterSkips = filterSkips;
        Table = table;
        Curr = -1;

        Next(input);
    }

    /// <summary>The decoder, positioned after the structs read so far: once <see cref="Curr"/> is -1, at the delete set.</summary>
    public ref TDecoder Decoder => ref _decoder;

    public StagingTable Table { get; private set; }

    /// <summary>Which input of its caller this reader reads (a merge's); the reader itself does not use it.</summary>
    public int Source { get; init; }

    /// <summary>The index in <see cref="Table"/> of the struct last read by <see cref="Next"/>, or -1 once the update is exhausted.</summary>
    public int Curr { get; private set; }

    /// <summary>The fields of the current struct, kept off the table's rows for the merge's comparisons.</summary>
    public long Client { get; private set; }

    public long Clock { get; private set; }

    public int Length { get; private set; }

    public StagedKind Kind { get; private set; }

    /// <summary>
    /// Gives away the table, which holds the current struct (at index 0) and nothing else, and reads on into
    /// <paramref name="spare"/>. The caller must call <see cref="Next"/> before it reads this reader again.
    /// </summary>
    public StagingTable SwapTable(StagingTable spare)
    {
        Debug.Assert(Curr == 0 && Table.Count == 1);
        var table = Table;
        spare.Truncate();
        spare.BeginClient(Client, Clock + Length);
        _cursor.UseStaging(spare);
        Table = spare;
        return table;
    }

    /// <summary>
    /// Advances to the next struct, skipping over 'Skip' structs when <c>filterSkips</c> was requested in the
    /// constructor. Returns whether there is one.
    /// </summary>
    public bool Next(ReadOnlySpan<byte> input)
    {
        do
        {
            if (Table.Count > 0)
            {
                Table.Truncate();
            }

            Curr = _cursor.NextStaged(ref _decoder, input);
            if (Curr >= 0)
            {
                ref readonly var s = ref Table.At(Curr);
                Client = Table.ClientAt(s.Client);
                Clock = s.Clock;
                Length = s.Length;
                Kind = s.Kind;
            }
        }
        while (_filterSkips && Curr >= 0 && Kind == StagedKind.Skip);

        return Curr >= 0;
    }
}
