// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

internal class ContentType : IContentEx
{
    internal const int _ref = 7;

    internal ContentType(AbstractType type)
    {
        Type = type;
    }

    int IContentEx.Ref => _ref;

    bool IContentEx.Mergeable => false;

    public bool Countable => true;
    public int Length => 1;

    public AbstractType Type { get; }

    public IReadOnlyList<object> GetContent() => new object[] { Type };

    public IContent Copy() => new ContentType(Type.InternalCopy());

    public IContent Splice(int offset) => throw TypeContent.Unsplittable();

    public bool MergeWith(IContent right) => false;

    // A store slot of kind Type holds the type-table index of its type (TypeContent), never this object: this is the form
    // a decoded or a new item arrives in, and what a redone item is made from.
    void IContentEx.Integrate(Transaction transaction, ItemRef item) => throw StoreSide();

    void IContentEx.Delete(Transaction transaction) => throw StoreSide();

    void IContentEx.Gc(StructStore store) => throw StoreSide();

    private static InvalidOperationException StoreSide() =>
        new("The content of a type is integrated, deleted and collected through its store slot (TypeContent), not as an object.");

    void IContentEx.Write(IUpdateEncoder encoder, int offset)
    {
        Type.Write(encoder);
    }

    internal static ContentType Read(IUpdateDecoder decoder)
    {
        var typeRef = decoder.ReadTypeRef();
        switch (typeRef)
        {
            case YArray.YArrayRefId:
                var arr = YArray.Read(decoder);
                return new ContentType(arr);
            case YMap.YMapRefId:
                var map = YMap.Read(decoder);
                return new ContentType(map);
            case YText.YTextRefId:
                var text = YText.Read(decoder);
                return new ContentType(text);
            case YXmlElement.YXmlElementRefId:
                var xmlElement = YXmlElement.Read(decoder);
                return new ContentType(xmlElement);
            case YXmlFragment.YXmlFragmentRefId:
                var xmlFragment = YXmlFragment.Read(decoder);
                return new ContentType(xmlFragment);
            case YXmlHook.YXmlHookRefId:
                var xmlHook = YXmlHook.Read(decoder);
                return new ContentType(xmlHook);
            case YXmlText.YXmlTextRefId:
                var xmlText = YXmlText.Read(decoder);
                return new ContentType(xmlText);
            default:
                throw new YUpdateException($"Unknown shared type: {typeRef}.");
        }
    }
}
