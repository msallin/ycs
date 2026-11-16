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

    /// <summary>
    /// Appends struct <paramref name="i"/> of <paramref name="from"/>, <paramref name="cut"/> clocks in, to the empty
    /// <paramref name="to"/>, as the right part of Yjs' <c>splice</c> would be: an item starts after its own clock
    /// before the cut (its origin) and no longer names a parent, and a String whose cut falls inside a surrogate pair
    /// starts with U+FFFD. Slicing a Binary, Embed, Format, Type or Doc is as unsupported as it is in the content classes.
    /// </summary>
    public static void CopyFrom(StagingTable to, StagingTable from, int i, int cut)
    {
        var id = from.IdOf(i);
        var kind = from.At(i).Kind;
        var length = from.At(i).Length - cut;
        to.BeginClient(id.Client, id.Clock + cut);
        if (kind is StagedKind.GC or StagedKind.Skip)
        {
            to.Add(kind, length);
            return;
        }

        ID? leftOrigin = cut > 0 ? new ID(id.Client, id.Clock + cut - 1) : from.LeftOrigin(i);
        var j = to.Add(kind, length, leftOrigin, from.RightOrigin(i));
        if (cut == 0)
        {
            switch (from.At(i).ParentKind)
            {
                case StagedParent.Root:
                    to.SetParentRoot(j, from.ParentName(i));
                    break;
                case StagedParent.Id:
                    to.SetParentId(j, from.ParentId(i));
                    break;
            }
        }

        if (from.ParentSub(i) is { } parentSub)
        {
            to.SetParentSub(j, parentSub);
        }

        switch (kind)
        {
            case StagedKind.String:
                var chars = from.Chars(i);
                chars[cut..].CopyTo(to.Chars(j));
                if (cut > 0 && char.IsHighSurrogate(chars[cut - 1]))
                {
                    to.Chars(j)[0] = '\uFFFD';
                }

                break;
            case StagedKind.Any:
                from.Values(i)[cut..].CopyTo(to.Values(j));
                break;
            case StagedKind.Type:
                if (cut > 0)
                {
                    throw new NotImplementedException();
                }

                to.SetType(j, from.At(i).TypeRef, from.TypeName(i));
                break;
            case StagedKind.Json or StagedKind.Binary or StagedKind.Embed or StagedKind.Format or StagedKind.Doc:
                to.SetCold(j, cut == 0 ? from.Cold(i) : ((IContent)from.Cold(i)).Splice(cut));
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
