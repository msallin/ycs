// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

namespace Ycs;

/// <summary>
/// Reads one struct (an item, a GC or a Skip) at a time from an update, across client boundaries, without
/// materializing the whole update or integrating anything into a document (Yjs' <c>LazyStructReader</c>,
/// src/utils/updates.js). The struct is staged into the reader's <see cref="StagingTable"/> straight from the decoder
/// (no struct object); an item's parent stays unresolved, a root type's name or the id of the parent's item. Used by
/// the update-manipulation utilities in <see cref="UpdateOperations"/>.
/// </summary>
internal sealed class LazyStructReader
{
    // Not readonly: Next() advances it in place.
    private EncodingUtils.StructCursor _cursor;
    private readonly bool _filterSkips;
    private readonly bool _keep;

    /// <param name="table">An empty table, the reader's own until the update is read.</param>
    /// <param name="keep">
    /// Whether the table keeps every struct read. By default it holds the current struct only (the table is
    /// <see cref="StagingTable.Truncate"/>d before each read), so what a caller needs of an earlier one it copies first.
    /// </param>
    public LazyStructReader(IUpdateDecoder decoder, bool filterSkips, StagingTable table, bool keep = false)
    {
        _cursor = new EncodingUtils.StructCursor(decoder, staging: table);
        _filterSkips = filterSkips;
        _keep = keep;
        Table = table;
        Curr = -1;

        Next();
    }

    public StagingTable Table { get; }

    /// <summary>The index in <see cref="Table"/> of the struct last read by <see cref="Next"/>, or -1 once the update is exhausted.</summary>
    public int Curr { get; private set; }

    /// <summary>The fields of the current struct, kept off the table's rows for the merge's comparisons.</summary>
    public long Client { get; private set; }

    public long Clock { get; private set; }

    public int Length { get; private set; }

    public StagedKind Kind { get; private set; }

    /// <summary>
    /// Advances to the next struct, skipping over 'Skip' structs when <c>filterSkips</c> was requested in the
    /// constructor. Returns whether there is one.
    /// </summary>
    public bool Next()
    {
        do
        {
            if (!_keep && Table.Count > 0)
            {
                Table.Truncate();
            }

            Curr = _cursor.NextStaged();
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
