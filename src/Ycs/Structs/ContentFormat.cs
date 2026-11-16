// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

namespace Ycs;

internal class ContentFormat : IContentEx
{
    internal const int _ref = 6;

    public readonly string Key;
    public readonly object Value;

    internal ContentFormat(string key, object value)
    {
        Key = key;
        Value = value;
    }

    int IContentEx.Ref => _ref;

    bool IContentEx.Mergeable => false;

    public bool Countable => false;
    public int Length => 1;

    public IContent Copy() => new ContentFormat(Key, Value);

    // Yjs: ContentFormat#getContent returns an empty array - formatting carries no user content.
    public IReadOnlyList<object> GetContent() => System.Array.Empty<object>();

    public IContent Splice(int offset) => throw new NotImplementedException();

    public bool MergeWith(IContent right)
    {
        return false;
    }

    void IContentEx.Integrate(Transaction transaction, ItemRef item)
    {
        // Search markers are currently unsupported for rich text documents.
        (item.Parent as YArrayBase)?.DisableSearchMarkers();
        if (item.Parent is YText text)
        {
            text.HasFormatting = true;
        }
    }

    void IContentEx.Delete(Transaction transaction)
    {
        // Do nothing.
    }

    void IContentEx.Gc(StructStore store)
    {
        // Do nothing.
    }

    void IContentEx.Write(IUpdateEncoder encoder, int offset)
    {
        encoder.WriteKey(Key);
        encoder.WriteJson(Value);
    }

    internal static ContentFormat Read(IUpdateDecoder decoder)
    {
        var key = decoder.ReadKey();
        var value = decoder.ReadJson();
        return new ContentFormat(key, value);
    }
}
