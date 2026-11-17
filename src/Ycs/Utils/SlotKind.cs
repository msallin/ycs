namespace Ycs;

/// <summary>
/// What a slot holds, as bits 8..15 of <see cref="Slot.Info"/> say. The values of the structs are the reference numbers
/// of the update format, which are those of <see cref="ItemKind"/>; <see cref="Free"/> marks a slot on a free list.
/// </summary>
internal enum SlotKind
{
    GC = 0,
    Deleted = 1,
    Json = 2,
    Binary = 3,
    String = 4,
    Embed = 5,
    Format = 6,
    Type = 7,
    Any = 8,
    Doc = 9,
    Skip = 10,
    Free = Slot.FreeKind,
}

internal static class SlotKinds
{
    /// <summary>Whether an item of <paramref name="kind"/> counts in its parent's length while it is not deleted (Yjs: the content's <c>isCountable</c>).</summary>
    internal static bool IsCountable(this SlotKind kind) =>
        kind is SlotKind.String or SlotKind.Any or SlotKind.Json or SlotKind.Binary or SlotKind.Embed or SlotKind.Type or SlotKind.Doc;

    /// <summary>Whether the content of <paramref name="kind"/> is a cold object (no inline storage ever).</summary>
    internal static bool IsCold(this SlotKind kind) =>
        kind is SlotKind.Format or SlotKind.Embed or SlotKind.Binary or SlotKind.Json or SlotKind.Doc;

    /// <summary>Whether two items of <paramref name="kind"/> merge when they are neighbours in clock and origin (Yjs: the content's <c>mergeWith</c>); a merge of this kind always succeeds.</summary>
    internal static bool IsMergeable(this SlotKind kind) =>
        kind is SlotKind.Deleted or SlotKind.String or SlotKind.Any or SlotKind.Json;

    /// <summary>Whether a slot of <paramref name="kind"/> holds a content object in the slab's reference pages (a String's text is in the character arena, an Any's values in the value arena).</summary>
    internal static bool HasObject(this SlotKind kind) =>
        kind is SlotKind.Json or SlotKind.Binary or SlotKind.Embed or SlotKind.Format or SlotKind.Doc;

    /// <summary>The public kind of a struct; a free slot is none.</summary>
    internal static ItemKind ToItemKind(this SlotKind kind) =>
        kind == SlotKind.Free ? throw new ArgumentOutOfRangeException(nameof(kind), "A free slot holds no struct.") : (ItemKind)(int)kind;
}
