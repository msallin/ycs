#nullable disable // Not annotated for nullable reference types yet.

using System.Buffers;

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
    /// Any's values are copies, not views of the arena, which a compaction moves. The copy of an Any's values is a rented
    /// array: <see cref="StoredContent.Release"/> returns it.
    /// </summary>
    internal StoredContent Stored
    {
        get
        {
            switch (Kind)
            {
                case SlotKind.Type:
                    return new(TypeOf);
                case SlotKind.String:
                    return new(Chars.ToString());
                case SlotKind.Any:
                    var span = ValueSpan;
                    var values = ArrayPool<object>.Shared.Rent(span.Length);
                    span.CopyTo(values);
                    return new(values);
                default:
                    return new(Content);
            }
        }
    }
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

    // The values of an Any item, as stored, in an array rented from the shared pool (longer than the item).
    internal StoredContent(object[] values) => _values = values;

    /// <summary>Returns the rented array of an Any's values; the content is not readable afterwards.</summary>
    internal void Release()
    {
        if (_values != null)
        {
            ArrayPool<object>.Shared.Return(_values, clearArray: true);
        }
    }

    // A type has length 1: the index is ignored.
    internal object ValueAt(int index) => _type ?? (_text != null ? _text[index].ToString() : _values != null ? ValueFreezer.OnRead(_values[index]) : ItemRef.StoredValueAt(_content, index));
}
