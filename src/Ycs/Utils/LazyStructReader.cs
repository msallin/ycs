// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

/// <summary>
/// Reads one struct (<see cref="Item"/>, <see cref="GC"/>, or <see cref="Skip"/>) at a time
/// from an update, across client boundaries, without materializing the whole update or
/// integrating anything into a document (Yjs' <c>LazyStructReader</c>, src/utils/updates.js).
/// An <see cref="Item"/>'s parent stays unresolved: a root type's name or the id of the
/// parent's item. Used by the update-manipulation utilities in <see cref="UpdateOperations"/>.
/// </summary>
internal sealed class LazyStructReader
{
    private readonly IEnumerator<AbstractStruct> _generator;
    private readonly bool _filterSkips;

    public LazyStructReader(IUpdateDecoder decoder, bool filterSkips)
    {
        _generator = EncodingUtils.ReadStructSection(decoder).GetEnumerator();
        _filterSkips = filterSkips;
        Curr = null;

        Next();
    }

    /// <summary>
    /// The struct last read by <see cref="Next"/>, or <c>null</c> once the update is exhausted.
    /// </summary>
    public AbstractStruct Curr { get; private set; }

    /// <summary>
    /// Advances to (and returns) the next struct, skipping over 'Skip' structs when
    /// <c>filterSkips</c> was requested in the constructor.
    /// </summary>
    public AbstractStruct Next()
    {
        do
        {
            Curr = _generator.MoveNext() ? _generator.Current : null;
        }
        while (_filterSkips && Curr != null && Curr is Skip);

        return Curr;
    }
}
