// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace Ycs
{
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
                else
                {
                    // This will be gc'd later and we want to merge it if possible.
                    // We try to merge all deleted items each transaction,
                    // but we have no knowledge about that this needs to merged
                    // since it is not in transaction. Hence we add it to transaction._mergeStructs.
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
                else
                {
                    // Same as above.
                    transaction._mergeStructs.Add(valueItem);
                }
            }

            transaction.Changed.Remove(Type);
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
        }

        void IContentEx.Write(IUpdateEncoder encoder, int offset)
        {
            Type.Write(encoder);
        }

        // Yjs type refs Ycs does not implement; recognised only to fail with a clear message.
        internal const uint YXmlElementRefId = 3;
        internal const uint YXmlFragmentRefId = 4;
        internal const uint YXmlHookRefId = 5;
        internal const uint YXmlTextRefId = 6;

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
                case YXmlElementRefId:
                case YXmlFragmentRefId:
                case YXmlHookRefId:
                case YXmlTextRefId:
                    throw new NotSupportedException(
                        $"Ycs does not implement the Yjs XML types yet (type ref {typeRef}). " +
                        "Documents produced by y-prosemirror / y-quill and friends cannot be decoded.");
                default:
                    throw new NotSupportedException($"Unknown Yjs type ref: {typeRef}");
            }
        }
    }
}
