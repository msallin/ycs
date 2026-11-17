#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

internal readonly partial struct ItemRef
{
    /// <summary>
    /// The element at <paramref name="index"/> as a list or map reads it: for <see cref="SlotKind.Any"/> and
    /// <see cref="SlotKind.Json"/> without the read-only wrapper, null for collected content (Yjs' <c>getContent()[index]</c>
    /// of <c>[]</c> is <c>undefined</c>), else the element of <c>GetContent()</c>. Allocates nothing for Any and Json.
    /// </summary>
    internal object StoredValueAt(int index) => Kind switch
    {
        SlotKind.Type => TypeOf,
        SlotKind.Any => ValueAt(index),
        _ => StoredValueAt(Content, index),
    };

    internal static object StoredValueAt(IContent content, int index) => content switch
    {
        ContentAny any => any.ValueAt(index),
        ContentJson json => json.ValueAt(index),
        null => null,
        _ => content.GetContent()[index],
    };

    /// <summary>The content as it is now, readable after the struct merged into another or left the store.</summary>
    internal StoredContent Stored => Kind == SlotKind.Type ? new(TypeOf) : new(Content);
}

/// <summary>The content object of an item at one moment; it stays readable when the item's slot is reused.</summary>
internal readonly struct StoredContent
{
    private readonly IContent _content;
    private readonly AbstractType _type;

    internal StoredContent(IContent content) => _content = content;

    internal StoredContent(AbstractType type) => _type = type;

    // A type has length 1: the index is ignored.
    internal object ValueAt(int index) => _type ?? ItemRef.StoredValueAt(_content, index);
}
