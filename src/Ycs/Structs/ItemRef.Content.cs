#nullable disable // Not annotated for nullable reference types yet.

using System.Runtime.CompilerServices;

namespace Ycs;

// The content of an item, read and written by its kind. The kind in Info equals the kind of the object in Refs at every
// write (New, Adopt, a split, gc, BecomeGC); the accessors are what the code outside this file reads, so that the storage
// under them can move.
internal readonly partial struct ItemRef
{
    /// <summary>What the slot holds: <see cref="SlotKind.GC"/> for a GC struct, else the kind of the item's content.</summary>
    internal SlotKind Kind
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (SlotKind)Read().Kind;
    }

    /// <summary>The text of a <see cref="SlotKind.String"/> item; valid until the item's content changes.</summary>
    internal ReadOnlySpan<char> Chars => Typed<ContentString>(SlotKind.String).GetString().AsSpan();

    /// <summary>The <paramref name="index"/>th value of a <see cref="SlotKind.Any"/> item.</summary>
    internal object ValueAt(int index) => Typed<ContentAny>(SlotKind.Any).ValueAt(index);

    /// <summary>The values of a <see cref="SlotKind.Any"/> item.</summary>
    internal IReadOnlyList<object> Values => Typed<ContentAny>(SlotKind.Any).GetContent();

    /// <summary>The type of a <see cref="SlotKind.Type"/> item.</summary>
    internal AbstractType TypeOf => Typed<ContentType>(SlotKind.Type).Type;

    /// <summary>The content object of a format, embed, binary, JSON or document item; null for any other kind.</summary>
    internal IContentEx Cold => Kind.IsCold() ? (IContentEx)_slab.ReadRef(HandleChecked()) : null;

    /// <summary>Appends the elements of the content (Yjs' <c>getContent</c>) to <paramref name="destination"/>.</summary>
    internal void AppendContentTo(List<object> destination) => destination.AddRange(Content.GetContent());

    /// <summary>A copy of the content that belongs to no item (what a redone item holds).</summary>
    internal IContentEx CopyContent() => (IContentEx)Content.Copy();

    /// <summary>Appends <paramref name="text"/> to a <see cref="SlotKind.String"/> item; the caller adjusts <see cref="Length"/>.</summary>
    internal void AppendChars(string text) => Typed<ContentString>(SlotKind.String).Append(text);

    /// <summary>Appends <paramref name="values"/> to a <see cref="SlotKind.Any"/> item; the caller adjusts <see cref="Length"/>.</summary>
    internal void AppendValues(List<object> values) => Typed<ContentAny>(SlotKind.Any).Append(values);

    private int HandleChecked()
    {
        Check();
        return _handle;
    }

    private T Typed<T>(SlotKind kind)
        where T : class, IContentEx
    {
        var actual = Kind;
        return actual == kind
            ? (T)Content
            : throw new InvalidOperationException($"The struct holds {actual}, not {kind} content.");
    }
}
