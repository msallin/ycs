using System.Text;

namespace Ycs;

// What the property tests (a project without InternalsVisibleTo, reading through UnsafeAccessor) and the internal
// readers of the tests ask of the store, in primitives and public types: no struct or content class leaves.
internal sealed partial class StructStore
{
    /// <summary>The name of a struct's kind in the tests' dumps; fixed here, not <c>GetType().Name</c>, so that a rename or a merged representation leaves the dumps as they are.</summary>
    internal static string KindNameForTests(ItemRef str) =>
        str.IsItem ? "Item" : str.IsGC ? "GC" : throw new InvalidOperationException("A null struct has no kind.");

    /// <summary>The name of a content's kind in the tests' dumps, by an explicit mapping (see <see cref="KindNameForTests"/>); a collected item is a deleted content.</summary>
    internal static string ContentKindNameForTests(SlotKind kind) => kind switch
    {
        SlotKind.Any => "ContentAny",
        SlotKind.Binary => "ContentBinary",
        SlotKind.Deleted => "ContentDeleted",
        SlotKind.Doc => "ContentDoc",
        SlotKind.Embed => "ContentEmbed",
        SlotKind.Format => "ContentFormat",
        SlotKind.Json => "ContentJson",
        SlotKind.String => "ContentString",
        SlotKind.Type => "ContentType",
        _ => throw new InvalidOperationException("A content kind of the store has no name for the tests' dumps."),
    };

    /// <summary>
    /// Writes every struct of the store, client by client (ascending) and in clock order: kind, id, length, deletion;
    /// for an item its content, links (left, right, origins, parent, key), keep, redone and, with
    /// <paramref name="markers"/>, the marker flag. <paramref name="parentText"/> names an item's parent (a type, an id
    /// or null); <paramref name="contentText"/> renders a content given its kind name and values (the string of a
    /// <c>ContentString</c> as the only value).
    /// </summary>
    internal void DumpForTests(StringBuilder sb, bool markers, Func<object, string> parentText, Func<string, IReadOnlyList<object>, string> contentText)
    {
        foreach (var (client, structs) in Clients.OrderBy(kv => kv.Key))
        {
            sb.Append("client ").Append(client).Append('\n');
            foreach (var s in structs)
            {
                sb.Append("  ").Append(KindNameForTests(s)).Append(' ').Append(IdText(s)).Append(" len ").Append(s.Length).Append(s.Deleted ? " deleted" : "");
                if (s.IsItem)
                {
                    var contentName = ContentKindNameForTests(s.Kind);
                    IReadOnlyList<object> values = s.Kind == SlotKind.String ? [s.Chars.ToString()] : s.Elements;
                    sb.Append(" content ").Append(contentName).Append(' ').Append(contentText(contentName, values));
                    sb.Append(" left ").Append(IdText(s.Left)).Append(" right ").Append(IdText(s.Right));
                    sb.Append(" origin ").Append(IdText(s.LeftOrigin)).Append(" rightOrigin ").Append(IdText(s.RightOrigin));
                    sb.Append(" parent ").Append(parentText(s.Parent));
                    sb.Append(" sub ").Append(s.ParentSub ?? "-").Append(" keep ").Append(s.Keep).Append(" redone ").Append(IdText(s.Redone));
                    if (markers)
                    {
                        sb.Append(" marker ").Append(s.Marker);
                    }
                }

                sb.Append('\n');
            }
        }
    }

    /// <summary>Every item whose content is a list type, with its id and the list (in the store's order of clients).</summary>
    internal IEnumerable<(ItemId Item, YArrayBase List)> NestedListsForTests()
    {
        foreach (var (_, structs) in Clients)
        {
            foreach (var s in structs)
            {
                if (s.Kind == SlotKind.Type && s.TypeOf is YArrayBase list)
                {
                    yield return (s.Id, list);
                }
            }
        }
    }

    private static string IdText(ItemRef s) => s.IsNull ? "-" : $"{s.Id.Client}:{s.Id.Clock}";

    private static string IdText(ItemId? id) => id is ItemId i ? $"{i.Client}:{i.Clock}" : "-";
}
