#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

internal readonly partial struct ItemRef
{
    /// <summary>The <paramref name="index"/>th value of a <see cref="SlotKind.Any"/> item.</summary>
    internal object ValueAt(int index) => Typed<ContentAny>(SlotKind.Any).ValueAt(index);

    /// <summary>The values of a <see cref="SlotKind.Any"/> item.</summary>
    internal IReadOnlyList<object> Values => Typed<ContentAny>(SlotKind.Any).GetContent();

    /// <summary>Appends <paramref name="values"/> to a <see cref="SlotKind.Any"/> item; the caller adjusts <see cref="Length"/>.</summary>
    internal void AppendValues(List<object> values) => Typed<ContentAny>(SlotKind.Any).Append(values);

    /// <summary><see cref="CanExtendRun"/> for <see cref="SlotKind.Any"/> values.</summary>
    internal bool CanExtendAnyRun(Transaction transaction, AbstractType parent, ItemRef right, long client) =>
        CanExtendRun(transaction, parent, right, SlotKind.Any, client);
}
