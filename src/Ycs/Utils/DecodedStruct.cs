namespace Ycs;

/// <summary>
/// One struct of a decoded update, detached from any store and from the struct classes; one per struct, in the order
/// of the update, <see cref="ItemKind.Skip"/> and <see cref="ItemKind.GC"/> included. What the kind carries is in
/// exactly one place: <see cref="Content"/> (the values of Any, Json, Binary and Embed), <see cref="Text"/> (String),
/// <see cref="FormatKey"/> and <see cref="FormatValue"/> (Format), <see cref="TypeRef"/> and <see cref="TypeName"/>
/// (Type), <see cref="DocGuid"/> (Doc). The others are empty, null or -1.
/// </summary>
/// <param name="Id">The id of the first clock the struct covers.</param>
/// <param name="Length">The number of clocks it covers.</param>
/// <param name="Kind">What the struct is.</param>
/// <param name="LeftOrigin">The id the item was inserted after; null for none, and for a GC or a Skip.</param>
/// <param name="RightOrigin">The id the item was inserted before; null for none, and for a GC or a Skip.</param>
/// <param name="ParentName">The name of the root type when the update names it (an item without origins).</param>
/// <param name="ParentId">The id of the item whose type is the parent, when the update gives it (an item without origins).</param>
/// <param name="ParentSub">The map key of the item, null for a list entry.</param>
/// <param name="Content">The values of an Any, Json (undefined as null), Binary (its bytes) or Embed struct.</param>
/// <param name="Text">The characters of a String struct.</param>
/// <param name="FormatKey">The key of a Format struct.</param>
/// <param name="FormatValue">The value of a Format struct.</param>
/// <param name="TypeRef">The type reference number of a Type struct, -1 for another kind.</param>
/// <param name="TypeName">The node name of an XML element or the name of a hook, null for another type.</param>
/// <param name="DocGuid">The guid of the subdocument of a Doc struct.</param>
public sealed record DecodedStruct(
    ID Id,
    int Length,
    ItemKind Kind,
    ID? LeftOrigin,
    ID? RightOrigin,
    string? ParentName,
    ID? ParentId,
    string? ParentSub,
    IReadOnlyList<object?> Content,
    string? Text,
    string? FormatKey,
    object? FormatValue,
    int TypeRef,
    string? TypeName,
    string? DocGuid);
