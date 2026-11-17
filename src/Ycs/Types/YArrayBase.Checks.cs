namespace Ycs;

public abstract partial class YArrayBase
{
    /// <summary>The push hint, for <see cref="StructStore.CheckForTests"/>.</summary>
    internal ItemHandle PushHintForTests => _pushHintHeld;

    /// <summary>The structs the search markers sit on, for <see cref="StructStore.CheckForTests"/>.</summary>
    internal IEnumerable<ItemHandle> MarkerItemsForTests => _searchMarkers == null ? [] : _searchMarkers._searchMarkers.Select(m => m.Held);
}
