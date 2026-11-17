#nullable disable // Not annotated for nullable reference types yet.

using System.Buffers;

namespace Ycs;

// The content reads of the text types: a format's attribute and what an embed or an embedded type stands for.
internal readonly partial struct ItemRef
{
    /// <summary>Copies the text of a <see cref="SlotKind.String"/> item to <paramref name="destination"/>, which holds at least <see cref="Length"/> characters, without materializing a run kept in a builder.</summary>
    internal void CopyCharsTo(Span<char> destination) => Typed<ContentString>(SlotKind.String).CopyTo(destination);

    /// <summary>
    /// Writes the text of a <see cref="SlotKind.String"/> item to <paramref name="writer"/>. The content is read before the writer
    /// is asked for its span: a writer that runs user code there may change the item.
    /// </summary>
    internal void WriteCharsTo(IBufferWriter<char> writer)
    {
        var content = Typed<ContentString>(SlotKind.String);
        var length = content.Length;
        content.CopyTo(writer.GetSpan(length));
        writer.Advance(length);
    }

    /// <summary>The attribute name of a <see cref="SlotKind.Format"/> item.</summary>
    internal string FormatKey => Typed<ContentFormat>(SlotKind.Format).Key;

    /// <summary>The attribute value of a <see cref="SlotKind.Format"/> item (null removes the attribute); a byte array as a copy.</summary>
    internal object FormatValue => Typed<ContentFormat>(SlotKind.Format).Value;

    /// <summary>What a <see cref="SlotKind.Embed"/> or <see cref="SlotKind.Type"/> item stands for in a delta.</summary>
    internal YValue Embedded => Kind == SlotKind.Embed
        ? YValue.FromStored(Typed<ContentEmbed>(SlotKind.Embed).Embed)
        : YValue.FromStored(TypeOf);
}
