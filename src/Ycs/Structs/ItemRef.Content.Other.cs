#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

internal readonly partial struct ItemRef
{
    /// <summary>
    /// The elements of the content (Yjs' <c>getContent</c>). A String's and an Any's are a copy, never a view of the arena:
    /// the reader (an <see cref="ItemView"/>) may run after a batch has moved the range. The readers that consume at
    /// once read <see cref="Chars"/> and <see cref="ValueSpan"/>.
    /// </summary>
    internal IReadOnlyList<object> Elements => Kind switch
    {
        SlotKind.Deleted => Array.Empty<object>(),
        SlotKind.Type => new object[] { TypeOf },
        SlotKind.String => CharElements(),
        SlotKind.Any => Values,
        _ => Content.GetContent(),
    };

    /// <summary>The document of a <see cref="SlotKind.Doc"/> item.</summary>
    internal YDoc Subdoc => Typed<ContentDoc>(SlotKind.Doc).Doc;

    /// <summary>Replaces the document of a <see cref="SlotKind.Doc"/> item by a new one with the same options, owned by this item.</summary>
    internal void RecreateSubdoc(string guid, UpdateLimits limits)
    {
        var content = Typed<ContentDoc>(SlotKind.Doc);
        content.Doc = ContentDoc.CreateDocFromOpts(guid, content.Opts, shouldLoad: false, limits);
        content.Doc._item = this;
    }
}
