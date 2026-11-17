using System.Diagnostics;

namespace Ycs;

/// <summary>
/// Copies a struct from one <see cref="StagingTable"/> to another and reads staged structs as <see cref="DecodedStruct"/>.
/// </summary>
internal static class StructStaging
{
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

        ItemId? leftOrigin = cut > 0 ? new ItemId(id.Client, id.Clock + cut - 1) : from.LeftOrigin(i);
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
                // A cut needs a struct of two clocks at least; a Type has one.
                Debug.Assert(cut == 0);
                if (cut > 0)
                {
                    throw new UnreachableException();
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
            return new DecodedStruct { Id = id, Length = s.Length, Kind = kind };
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
                values = new ContentView([.. table.Values(i)]);
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
                docGuid = ((ContentDoc)table.Cold(i)).Guid;
                break;
        }

        return new DecodedStruct
        {
            Id = id,
            Length = s.Length,
            Kind = kind,
            LeftOrigin = table.LeftOrigin(i),
            RightOrigin = table.RightOrigin(i),
            ParentName = s.ParentKind == StagedParent.Root ? table.ParentName(i) : null,
            ParentId = s.ParentKind == StagedParent.Id ? table.ParentId(i) : null,
            ParentSub = table.ParentSub(i),
            Content = values,
            Text = text,
            FormatKey = formatKey,
            FormatValue = formatValue,
            TypeRef = typeRef,
            TypeName = typeName,
            DocGuid = docGuid,
        };
    }
}
