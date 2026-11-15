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

    // Deleting and collecting a type walk the types nested in it, as deep as an update may nest them
    // (UpdateLimits.MaxDepth), so they keep the open types in a list instead of recursing. Both visit the items
    // in the order of Yjs' recursion: depth first, a type's list from its start, then its keys' values; the order
    // decides the order of the changed types, and so of the events.
    private sealed class Open(AbstractType type, Item owner)
    {
        public readonly AbstractType Type = type;
        public readonly Item Owner = owner;
        public Item Next = type._start;
        public IEnumerator<Item> Values;
        public IEnumerator<KeyValuePair<string, Item>> Keys;
        public Item Value;
    }

    void IContentEx.Delete(Transaction transaction)
    {
        var open = new List<Open> { new(Type, null) };
        while (open.Count > 0)
        {
            var frame = open[^1];
            Item item;
            if (frame.Values == null && frame.Next != null)
            {
                item = frame.Next;
                frame.Next = item.Right as Item;
            }
            else
            {
                frame.Values ??= frame.Type._map.Values.GetEnumerator();
                if (!frame.Values.MoveNext())
                {
                    transaction.Changed.Remove(frame.Type);
                    open.RemoveAt(open.Count - 1);
                    continue;
                }

                item = frame.Values.Current;
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
                transaction._mergeStructs.Add(item);
            }
        }
    }

    private static bool IsOlderThan(Item item, Transaction transaction) =>
        item.Id.Clock < (transaction.BeforeState.TryGetValue(item.Id.Client, out var clock) ? clock : 0);

    void IContentEx.Gc(StructStore store)
    {
        // An item is replaced by a GC struct after the types nested in it were collected, as in Yjs' Item.gc.
        var open = new List<Open> { new(Type, null) };
        while (open.Count > 0)
        {
            var frame = open[^1];
            Item item;
            if (frame.Keys == null && frame.Next != null)
            {
                item = frame.Next;
                frame.Next = item.Right as Item;
            }
            else if (frame.Value != null)
            {
                item = frame.Value;
                frame.Value = item.Left as Item;
            }
            else
            {
                frame.Keys ??= frame.Type._map.GetEnumerator();
                if (frame.Keys.MoveNext())
                {
                    frame.Value = frame.Keys.Current.Value;
                    continue;
                }

                frame.Type._start = null;
                frame.Type._map.Clear();
                frame.Type._mapCount = 0;
                open.RemoveAt(open.Count - 1);
                if (frame.Owner != null)
                {
                    store.ReplaceStruct(frame.Owner, new GC(frame.Owner.Id, frame.Owner.Length));
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
                store.ReplaceStruct(item, new GC(item.Id, item.Length));
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
