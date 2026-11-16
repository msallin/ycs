namespace Ycs;

/// <summary>
/// Stages decoded structs into a <see cref="StagingTable"/> and reads them back as <see cref="DecodedStruct"/>. The
/// decoder is a stream, so the staged struct is taken from the object just built from it, which stays authoritative:
/// the table is its shadow and must read back equal.
/// </summary>
internal static class StructStaging
{
    /// <summary>
    /// Appends <paramref name="str"/> to the open client's range. A Type stages its type reference and node name, never
    /// the <see cref="AbstractType"/>. Cold content (Json, Binary, Embed, Format, Doc) is held by reference.
    /// </summary>
    public static void Stage(StagingTable table, AbstractStruct str)
    {
        if (str is not Item item)
        {
            table.Add(str is Skip ? StagedKind.Skip : StagedKind.GC, str.Length);
            return;
        }

        var content = item.Content;
        var kind = KindOf(content);
        var i = table.Add(kind, item.Length, item.LeftOrigin, item.RightOrigin);
        switch (item.Parent)
        {
            case string name:
                table.SetParentRoot(i, name);
                break;
            case ID parentId:
                table.SetParentId(i, parentId);
                break;
        }

        if (item.ParentSub != null)
        {
            table.SetParentSub(i, item.ParentSub);
        }

        switch (content)
        {
            case ContentString text:
                text.CopyTo(table.Chars(i));
                break;
            case ContentAny any:
                var values = table.Values(i);
                for (var v = 0; v < values.Length; v++)
                {
                    values[v] = any.ValueAt(v);
                }

                break;
            case ContentType type:
                var (typeRef, typeName) = TypeOf(type.Type);
                table.SetType(i, typeRef, typeName);
                break;
            case ContentJson or ContentBinary or ContentEmbed or ContentFormat or ContentDoc:
                table.SetCold(i, content);
                break;
        }
    }

    /// <summary>Struct <paramref name="i"/> of <paramref name="table"/> as the record the objects would give.</summary>
    public static DecodedStruct Decoded(StagingTable table, int i)
    {
        var s = table.At(i);
        var id = table.IdOf(i);
        var kind = (ItemKind)(int)s.Kind;
        if (kind is ItemKind.GC or ItemKind.Skip)
        {
            return new DecodedStruct(id, s.Length, kind, null, null, null, null, null, [], null, null, null, -1, null, null);
        }

        IReadOnlyList<object?> values = [];
        string? text = null, formatKey = null, typeName = null, docGuid = null;
        object? formatValue = null;
        var typeRef = -1;
        switch (kind)
        {
            case ItemKind.String:
                text = new string(table.Chars(i));
                break;
            case ItemKind.Any:
                values = table.Values(i).ToArray();
                break;
            case ItemKind.Json or ItemKind.Binary or ItemKind.Embed:
                values = ((IContent)table.Cold(i)).GetContent();
                break;
            case ItemKind.Format:
                var format = (ContentFormat)table.Cold(i);
                formatKey = format.Key;
                formatValue = format.Value;
                break;
            case ItemKind.Type:
                typeRef = s.TypeRef;
                typeName = table.TypeName(i);
                break;
            case ItemKind.Doc:
                docGuid = ((ContentDoc)table.Cold(i)).Doc.Guid;
                break;
        }

        return new DecodedStruct(
            id,
            s.Length,
            kind,
            table.LeftOrigin(i),
            table.RightOrigin(i),
            s.ParentKind == StagedParent.Root ? table.ParentName(i) : null,
            s.ParentKind == StagedParent.Id ? table.ParentId(i) : null,
            table.ParentSub(i),
            values,
            text,
            formatKey,
            formatValue,
            typeRef,
            typeName,
            docGuid);
    }

    private static StagedKind KindOf(IContent content) => content switch
    {
        ContentDeleted => StagedKind.Deleted,
        ContentJson => StagedKind.Json,
        ContentBinary => StagedKind.Binary,
        ContentString => StagedKind.String,
        ContentEmbed => StagedKind.Embed,
        ContentFormat => StagedKind.Format,
        ContentType => StagedKind.Type,
        ContentAny => StagedKind.Any,
        _ => StagedKind.Doc,
    };

    private static (int Ref, string? Name) TypeOf(AbstractType type) => type switch
    {
        YXmlElement element => (YXmlElement.YXmlElementRefId, element.NodeName),
        YXmlHook hook => (YXmlHook.YXmlHookRefId, hook.HookName),
        YXmlText => (YXmlText.YXmlTextRefId, null),
        YXmlFragment => (YXmlFragment.YXmlFragmentRefId, null),
        YText => (YText.YTextRefId, null),
        YMap => (YMap.YMapRefId, null),
        _ => (YArray.YArrayRefId, null),
    };
}
