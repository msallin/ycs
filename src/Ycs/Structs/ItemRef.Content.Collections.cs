#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

internal readonly partial struct ItemRef
{
    /// <summary>
    /// The element at <paramref name="index"/> as a list or map reads it: for <see cref="SlotKind.Any"/> and
    /// <see cref="SlotKind.Json"/> without the read-only wrapper, null for collected content (Yjs' <c>getContent()[index]</c>
    /// of <c>[]</c> is <c>undefined</c>), else the element of <c>GetContent()</c>. Allocates nothing for Any and Json.
    /// </summary>
    internal object StoredValueAt(int index) => StoredValueAt(Content, index);

    internal static object StoredValueAt(IContent content, int index) => content switch
    {
        ContentAny any => any.ValueAt(index),
        ContentJson json => json.ValueAt(index),
        ContentDeleted => null,
        _ => content.GetContent()[index],
    };

    /// <summary>The content as it is now, readable after the struct merged into another or left the store.</summary>
    internal StoredContent Stored => new(Content);

    /// <summary><see cref="CanExtendRun"/> for <see cref="SlotKind.Any"/> values.</summary>
    internal bool CanExtendAnyRun(Transaction transaction, AbstractType parent, ItemRef right, long client) =>
        CanExtendRun(transaction, parent, right, typeof(ContentAny), client);
}

/// <summary>The content object of an item at one moment; it stays readable when the item's slot is reused.</summary>
internal readonly struct StoredContent(IContent content)
{
    internal object ValueAt(int index) => ItemRef.StoredValueAt(content, index);
}
