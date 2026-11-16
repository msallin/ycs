namespace Ycs;

/// <summary>
/// One struct of a decoded update, detached from any store and from the struct classes; one per struct, in the order
/// of the update, <see cref="ItemKind.Skip"/> and <see cref="ItemKind.GC"/> included. What the kind carries is in
/// exactly one place: <see cref="Content"/> (the values of Any, Json, Binary and Embed), <see cref="Text"/> (String),
/// <see cref="FormatKey"/> and <see cref="FormatValue"/> (Format), <see cref="TypeRef"/> and <see cref="TypeName"/>
/// (Type), <see cref="DocGuid"/> (Doc). The others are empty, null or -1.
/// </summary>
public sealed record DecodedStruct
{
    /// <summary>The id of the first clock the struct covers.</summary>
    public required ID Id { get; init; }

    /// <summary>The number of clocks it covers.</summary>
    public required long Length { get; init; }

    /// <summary>What the struct is.</summary>
    public required ItemKind Kind { get; init; }

    /// <summary>The id the item was inserted after; null for none, and for a GC or a Skip.</summary>
    public ID? LeftOrigin { get; init; }

    /// <summary>The id the item was inserted before; null for none, and for a GC or a Skip.</summary>
    public ID? RightOrigin { get; init; }

    /// <summary>The name of the root type when the update names it (an item without origins).</summary>
    public string? ParentName { get; init; }

    /// <summary>The id of the item whose type is the parent, when the update gives it (an item without origins).</summary>
    public ID? ParentId { get; init; }

    /// <summary>The map key of the item, null for a list entry.</summary>
    public string? ParentSub { get; init; }

    /// <summary>The values of an Any, Json (undefined as null), Binary (its bytes) or Embed struct.</summary>
    public IReadOnlyList<object?> Content { get; init; } = [];

    /// <summary>The characters of a String struct.</summary>
    public string? Text { get; init; }

    /// <summary>The key of a Format struct.</summary>
    public string? FormatKey { get; init; }

    /// <summary>The value of a Format struct.</summary>
    public object? FormatValue { get; init; }

    /// <summary>The type reference number of a Type struct, -1 for another kind.</summary>
    public int TypeRef { get; init; } = -1;

    /// <summary>The node name of an XML element or the name of a hook, null for another type.</summary>
    public string? TypeName { get; init; }

    /// <summary>The guid of the subdocument of a Doc struct.</summary>
    public string? DocGuid { get; init; }
}
