#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

internal class ContentType : IContentEx
{
    internal const int _ref = 7;

    private readonly AbstractType _type;

    internal ContentType(AbstractType type)
    {
        _type = type;
        (TypeRef, TypeName) = Describe(type);
    }

    // The decoded form: the type's reference and node name, no type. The store makes the type when the item enters it.
    private ContentType(int typeRef, string typeName)
    {
        TypeRef = typeRef;
        TypeName = typeName;
    }

    int IContentEx.Ref => _ref;

    public bool Countable => true;
    public int Length => 1;

    /// <summary>The type reference of the wire (<c>YArray.YArrayRefId</c> and the like).</summary>
    internal int TypeRef { get; }

    /// <summary>The node name of an XML element, the name of an XML hook, else null.</summary>
    internal string TypeName { get; }

    /// <summary>The type of a content made from one; a decoded content has none.</summary>
    public AbstractType Type => _type
        ?? throw new InvalidOperationException("A decoded type content holds the type's reference and name only; the type is made by Create when the item enters a store.");

    public IReadOnlyList<object> GetContent() => new object[] { Type };

    public IContent Copy() => new ContentType(Type.InternalCopy());

    public IContent Splice(int offset) => throw TypeContent.Unsplittable();

    public bool MergeWith(IContent right) => false;

    // A store slot of kind Type holds the type-table index of its type (TypeContent), never this object: this is the form
    // a new item arrives in, and what a redone item is made from.
    void IContentEx.Integrate(Transaction transaction, ItemRef item) => throw StoreSide();

    void IContentEx.Delete(Transaction transaction) => throw StoreSide();

    void IContentEx.Gc(StructStore store) => throw StoreSide();

    private static InvalidOperationException StoreSide() =>
        new("The content of a type is integrated, deleted and collected through its store slot (TypeContent), not as an object.");

    void IContentEx.Write(IUpdateEncoder encoder, int offset)
    {
        encoder.WriteTypeRef((uint)TypeRef);
        if (TypeName != null)
        {
            encoder.WriteKey(TypeName);
        }
    }

    /// <summary>Reads a type's reference and, for an XML element or hook, its node name; throws for a reference no type has.</summary>
    internal static int ReadHeader(IUpdateDecoder decoder, out string typeName)
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

    internal static ContentType Read(IUpdateDecoder decoder)
    {
        var typeRef = ReadHeader(decoder, out var typeName);
        return new ContentType(typeRef, typeName);
    }

    /// <summary>A new, empty type of the reference and name <see cref="ReadHeader"/> read: what an item of the update holds once it is adopted.</summary>
    internal static AbstractType Create(int typeRef, string typeName) => typeRef switch
    {
        YArray.YArrayRefId => new YArray(),
        YMap.YMapRefId => new YMap(),
        YText.YTextRefId => new YText(),
        YXmlElement.YXmlElementRefId => new YXmlElement(typeName),
        YXmlFragment.YXmlFragmentRefId => new YXmlFragment(),
        YXmlHook.YXmlHookRefId => new YXmlHook(typeName),
        YXmlText.YXmlTextRefId => new YXmlText(),
        _ => throw new YUpdateException($"Unknown shared type: {typeRef}."),
    };

    private static (int Ref, string Name) Describe(AbstractType type) => type switch
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
