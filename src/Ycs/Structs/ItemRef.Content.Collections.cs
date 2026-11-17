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
        SlotKind.String => Chars[index].ToString(),
        _ => StoredValueAt(Content, index),
    };

    internal static object StoredValueAt(IContent content, int index) => content switch
    {
        ContentJson json => json.ValueAt(index),
        null => null,
        _ => content.GetContent()[index],
    };

    /// <summary>
    /// The content as it is now, readable after the struct merged into another or left the store: a String's text and an
    /// Any's values are copies, not views of the arena, which a compaction moves.
    /// </summary>
    internal StoredContent Stored => Kind switch
    {
        SlotKind.Type => new(TypeOf),
        SlotKind.String => new(Chars.ToString()),
        SlotKind.Any => new(ValueSpan.ToArray()),
        _ => new(Content),
    };
}

/// <summary>The content object of an item at one moment; it stays readable when the item's slot is reused.</summary>
internal readonly struct StoredContent
{
    private readonly IContent _content;
    private readonly AbstractType _type;
    private readonly string _text;
    private readonly object[] _values;

    internal StoredContent(IContent content) => _content = content;

    internal StoredContent(AbstractType type) => _type = type;

    // The text of a String item, one string per UTF-16 code unit.
    internal StoredContent(string text) => _text = text;

    // The values of an Any item, as stored.
    internal StoredContent(object[] values) => _values = values;

    // A type has length 1: the index is ignored.
    internal object ValueAt(int index) => _type ?? (_text != null ? _text[index].ToString() : _values != null ? ValueFreezer.OnRead(_values[index]) : ItemRef.StoredValueAt(_content, index));
}
