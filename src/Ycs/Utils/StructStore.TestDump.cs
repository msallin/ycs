#nullable disable // Not annotated for nullable reference types yet.

using System.Text;

namespace Ycs;

// What the property tests (a project without InternalsVisibleTo, reading through UnsafeAccessor) and the internal
// readers of the tests ask of the store, in primitives and public types: no struct or content class leaves.
internal sealed partial class StructStore
{
    /// <summary>The name of a struct's kind in the tests' dumps; fixed here, not <c>GetType().Name</c>, so that a rename or a merged representation leaves the dumps as they are.</summary>
    internal static string KindNameForTests(ItemRef str) =>
        str.IsItem ? "Item" : str.IsGC ? "GC" : throw new InvalidOperationException("A null struct has no kind.");

    /// <summary>The name of a content's kind in the tests' dumps, by an explicit mapping (see <see cref="KindNameForTests"/>).</summary>
    internal static string ContentKindNameForTests(IContent content) => content switch
    {
        ContentAny => "ContentAny",
        ContentBinary => "ContentBinary",
        ContentDeleted => "ContentDeleted",
        ContentDoc => "ContentDoc",
        ContentEmbed => "ContentEmbed",
        ContentFormat => "ContentFormat",
        ContentJson => "ContentJson",
        ContentString => "ContentString",
        ContentType => "ContentType",
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
                    var content = s.Content;
                    IReadOnlyList<object> values = content is ContentString str ? [str.GetString()] : content.GetContent();
                    sb.Append(" content ").Append(ContentKindNameForTests(content)).Append(' ').Append(contentText(ContentKindNameForTests(content), values));
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
    internal IEnumerable<(ID Item, YArrayBase List)> NestedListsForTests()
    {
        foreach (var (_, structs) in Clients)
        {
            foreach (var s in structs)
            {
                if (s.IsItem && s.Content is ContentType { Type: YArrayBase list })
                {
                    yield return (s.Id, list);
                }
            }
        }
    }

    private static string IdText(ItemRef s) => s.IsNull ? "-" : $"{s.Id.Client}:{s.Id.Clock}";

    private static string IdText(ID? id) => id is ID i ? $"{i.Client}:{i.Clock}" : "-";
}
