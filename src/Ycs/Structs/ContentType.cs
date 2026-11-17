namespace Ycs;

internal class ContentType : IContent
{
    internal const int _ref = 7;

    internal ContentType(YType type)
    {
        Type = type;
    }

    int IContent.Ref => _ref;

    public bool Countable => true;
    public int Length => 1;

    public YType Type { get; }

    public IReadOnlyList<object> GetContent() => new object[] { Type };

    public IContent Copy() => new ContentType(Type.InternalCopy());

    public IContent Splice(int offset) => throw TypeContent.Unsplittable();

    public bool MergeWith(IContent right) => false;

    // A store slot of kind Type holds the type-table index of its type (TypeContent), never this object: this is the form
    // a new item arrives in, and what a redone item is made from.
    void IContent.Integrate(Transaction transaction, ItemRef item) => throw StoreSide();

    void IContent.Delete(Transaction transaction) => throw StoreSide();

    void IContent.Gc(StructStore store) => throw StoreSide();

    private static InvalidOperationException StoreSide() =>
        new("The content of a type is integrated, deleted, collected and written through its store slot (TypeContent), not as an object.");

    void IContent.Write(IUpdateEncoder encoder, int offset) => throw StoreSide();

    /// <summary>Reads a type's reference and, for an XML element or hook, its node name; throws for a reference no type has.</summary>
    internal static int ReadHeader(IUpdateDecoder decoder, out string? typeName)
    {
        var typeRef = decoder.ReadTypeRef();
        typeName = null;
        switch (typeRef)
        {
            case YXmlElement.YXmlElementRefId or YXmlHook.YXmlHookRefId:
                typeName = decoder.ReadKey();
                break;
            case YArray.YArrayRefId or YMap.YMapRefId or YText.YTextRefId or YXmlFragment.YXmlFragmentRefId or YXmlText.YXmlTextRefId:
                break;
            default:
                throw new YUpdateException($"Unknown shared type: {typeRef}.");
        }

        return (int)typeRef;
    }

    /// <summary>A new, empty type of the reference and name <see cref="ReadHeader"/> read: what an item of the update holds once it is adopted.</summary>
    internal static YType Create(int typeRef, string? typeName) => typeRef switch
    {
        YArray.YArrayRefId => new YArray(),
        YMap.YMapRefId => new YMap(),
        YText.YTextRefId => new YText(),
        YXmlElement.YXmlElementRefId => new YXmlElement(typeName!),
        YXmlFragment.YXmlFragmentRefId => new YXmlFragment(),
        YXmlHook.YXmlHookRefId => new YXmlHook(typeName!),
        YXmlText.YXmlTextRefId => new YXmlText(),
        _ => throw new YUpdateException($"Unknown shared type: {typeRef}."),
    };
}
