#nullable enable

namespace Ycs;

public enum YChangeKind
{
    Added,
    Removed,
}

/// <summary>
/// The <c>ychange</c> attribute of <see cref="YText.ToDelta(Snapshot, Snapshot?, Func{YChangeKind, ItemId, YChange}?)"/>: whether, and by
/// whom, the text was added or removed.
/// </summary>
/// <param name="Kind">Whether the text was added since the previous snapshot or removed since the snapshot.</param>
/// <param name="User">The client id of the change; null when <c>computeYChange</c> does not set it (as the default does not).</param>
public readonly record struct YChange(YChangeKind Kind, long? User);
