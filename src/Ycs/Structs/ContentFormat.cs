namespace Ycs;

internal class ContentFormat : IContent
{
    internal const int _ref = 6;

    public readonly string Key;
    private readonly object? _value;

    internal ContentFormat(string key, object? value)
    {
        Key = key;
        _value = value;
    }

    /// <summary>The attribute's value; a byte array as a copy.</summary>
    public object? Value => ValueFreezer.OnRead(_value);

    int IContent.Ref => _ref;

    public bool Countable => false;
    public int Length => 1;

    public IContent Copy() => new ContentFormat(Key, _value);

    // Yjs: ContentFormat#getContent returns an empty array - formatting carries no user content.
    public IReadOnlyList<object> GetContent() => System.Array.Empty<object>();

    public IContent Splice(int offset) => throw TypeContent.Unsplittable();

    public bool MergeWith(IContent right)
    {
        return false;
    }

    void IContent.Integrate(Transaction transaction, ItemRef item)
    {
        // Search markers are currently unsupported for rich text documents.
        (item.Parent as YArrayBase)?.DisableSearchMarkers();
        if (item.Parent is YText text)
        {
            text.HasFormatting = true;
        }
    }

    void IContent.Delete(Transaction transaction)
    {
        // Do nothing.
    }

    void IContent.Gc(StructStore store)
    {
        // Do nothing.
    }

    void IContent.Write(IUpdateEncoder encoder, int offset)
    {
        encoder.WriteKey(Key);
        encoder.WriteJson(_value);
    }

    internal static ContentFormat Read(IUpdateDecoder decoder)
    {
        var key = decoder.ReadKey();
        var value = decoder.ReadJson();
        return new ContentFormat(key, value);
    }
}
