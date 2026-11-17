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

    public IContent Splice(int offset) => throw new NotImplementedException();

    public bool MergeWith(IContent right) => false;

    void IContentEx.Integrate(Transaction transaction, ItemRef item)
    {
        Type.Integrate(transaction.Doc, item);
    }

    // Deleting and collecting a type walk the types nested in it, as deep as an update may nest them
    // (UpdateLimits.MaxDepth), so they keep the open types in a list instead of recursing. Both visit the items
    // in the order of Yjs' recursion: depth first, a type's list from its start, then its keys' values; the order
    // decides the order of the changed types, and so of the events.
    private sealed class Open(AbstractType type, ItemRef owner)
    {
        public readonly AbstractType Type = type;
        public readonly ItemRef Owner = owner;
        public ItemRef Next = type._start;
        public bool InMap;
        public Dictionary<string, ItemRef>.Enumerator Keys;
        public ItemRef Value;

        /// <summary>Starts on the keys' values, once the list is done.</summary>
        public void EnterMap()
        {
            InMap = true;
            Keys = Type._map.GetEnumerator();
        }
    }

    void IContentEx.Delete(Transaction transaction)
    {
        var open = new List<Open> { new(Type, default) };
        while (open.Count > 0)
        {
            var frame = open[^1];
            ItemRef item;
            if (!frame.InMap && !frame.Next.IsNull)
            {
                item = frame.Next;
                frame.Next = item.Right.IsItem ? item.Right : default;
            }
            else
            {
                if (!frame.InMap)
                {
                    frame.EnterMap();
                }

                if (!frame.Keys.MoveNext())
                {
                    transaction.LiveChanged.Remove(frame.Type);
                    open.RemoveAt(open.Count - 1);
                    continue;
                }

                item = frame.Keys.Current.Value;
            }

            if (item.MarkDeleted(transaction))
            {
                if (item.Content is ContentType nested)
                {
                    open.Add(new Open(nested.Type, item));
                }
                else
                {
                    item.Content.Delete(transaction);
                }
            }
            else if (IsOlderThan(item, transaction))
            {
                // Collected with the type later, but not in the transaction's delete set: merge it then.
                transaction._mergeStructs.Add(item.Id);
            }
        }
    }

    private static bool IsOlderThan(ItemRef item, Transaction transaction) =>
        item.Id.Clock < (transaction.BeforeState.TryGetValue(item.Id.Client, out var clock) ? clock : 0);

    void IContentEx.Gc(StructStore store)
    {
        // An item is replaced by a GC struct after the types nested in it were collected, as in Yjs' Item.gc.
        var open = new List<Open> { new(Type, default) };
        while (open.Count > 0)
        {
            var frame = open[^1];
            ItemRef item;
            if (!frame.InMap && !frame.Next.IsNull)
            {
                item = frame.Next;
                frame.Next = item.Right.IsItem ? item.Right : default;
            }
            else if (!frame.Value.IsNull)
            {
                item = frame.Value;
                frame.Value = item.Left.IsItem ? item.Left : default;
            }
            else
            {
                if (!frame.InMap)
                {
                    frame.EnterMap();
                }

                if (frame.Keys.MoveNext())
                {
                    frame.Value = frame.Keys.Current.Value;
                    continue;
                }

                frame.Type._start = default;
                (frame.Type as YArrayBase)?.ForgetList();
                frame.Type._map.Clear();
                frame.Type._mapCount = 0;
                frame.Type.Collect();
                open.RemoveAt(open.Count - 1);
                if (!frame.Owner.IsNull)
                {
                    store.ReplaceWithGC(frame.Owner);
                }

                continue;
            }

            if (!item.Deleted)
            {
                throw new InvalidOperationException();
            }

            if (item.Content is ContentType nested)
            {
                open.Add(new Open(nested.Type, item));
            }
            else
            {
                item.Content.Gc(store);
                store.ReplaceWithGC(item);
            }
        }
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
