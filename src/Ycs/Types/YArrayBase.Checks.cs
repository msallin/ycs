#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

public abstract partial class YArrayBase
{
    /// <summary>The push hint, for <see cref="StructStore.CheckForTests"/>.</summary>
    internal ItemRef PushHintForTests => _pushHint;

    /// <summary>The structs the search markers sit on, for <see cref="StructStore.CheckForTests"/>.</summary>
    internal IEnumerable<ItemRef> MarkerItemsForTests => _searchMarkers == null ? [] : _searchMarkers._searchMarkers.Select(m => m.P);
}
