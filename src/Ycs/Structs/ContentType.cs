// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

public class ContentType : IContentEx
{
    internal const int _ref = 7;

    internal ContentType(AbstractType type)
    {
        Type = type;
    }

    int IContentEx.Ref => _ref;

    public bool Countable => true;
    public int Length => 1;

    public AbstractType Type { get; }

    public IReadOnlyList<object> GetContent() => new object[] { Type };

    public IContent Copy() => new ContentType(Type.InternalCopy());

    public IContent Splice(int offset) => throw new NotImplementedException();

    public bool MergeWith(IContent right) => false;

    void IContentEx.Integrate(Transaction transaction, Item item)
    {
        Type.Integrate(transaction.Doc, item);
    }

    void IContentEx.Delete(Transaction transaction)
    {
        var item = Type._start;

        while (item != null)
        {
            if (!item.Deleted)
            {
                item.Delete(transaction);
            }
            else if (IsOlderThan(item, transaction))
            {
                // Collected with the type later, but not in the transaction's delete set: merge it then.
                transaction._mergeStructs.Add(item);
            }

            item = item.Right as Item;
        }

        foreach (var valueItem in Type._map.Values)
        {
            if (!valueItem.Deleted)
            {
                valueItem.Delete(transaction);
            }
            else if (IsOlderThan(valueItem, transaction))
            {
                transaction._mergeStructs.Add(valueItem);
            }
        }

        transaction.Changed.Remove(Type);

        static bool IsOlderThan(Item item, Transaction transaction) =>
            item.Id.Clock < (transaction.BeforeState.TryGetValue(item.Id.Client, out var clock) ? clock : 0);
    }

    void IContentEx.Gc(StructStore store)
    {
        var item = Type._start;
        while (item != null)
        {
            item.Gc(store, parentGCd: true);
            item = item.Right as Item;
        }

        Type._start = null;

        foreach (var kvp in Type._map)
        {
            var valueItem = kvp.Value;
            while (valueItem != null)
            {
                valueItem.Gc(store, parentGCd: true);
                valueItem = valueItem.Left as Item;
            }
        }

        Type._map.Clear();
        Type._mapCount = 0;
    }

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
