#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

internal readonly partial struct ItemRef
{
    /// <summary>The elements of the content (Yjs' <c>getContent</c>); a live view, valid until the item's content changes.</summary>
    internal IReadOnlyList<object> Elements => Kind switch
    {
        SlotKind.Deleted => Array.Empty<object>(),
        SlotKind.Type => new object[] { TypeOf },
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
