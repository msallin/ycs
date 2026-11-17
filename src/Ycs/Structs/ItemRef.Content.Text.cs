namespace Ycs;

// The content reads of the text types: a format's attribute and what an embed or an embedded type stands for.
internal readonly partial struct ItemRef
{

    /// <summary>The attribute name of a <see cref="SlotKind.Format"/> item.</summary>
    internal string FormatKey => Typed<ContentFormat>(SlotKind.Format).Key;

    /// <summary>The attribute value of a <see cref="SlotKind.Format"/> item (null removes the attribute); a byte array as a copy.</summary>
    internal object? FormatValue => Typed<ContentFormat>(SlotKind.Format).Value;

    /// <summary>What a <see cref="SlotKind.Embed"/> or <see cref="SlotKind.Type"/> item stands for in a delta.</summary>
    internal YValue Embedded => Kind == SlotKind.Embed
        ? YValue.FromStored(Typed<ContentEmbed>(SlotKind.Embed).Embed)
        : YValue.FromStored(TypeOf);
}
