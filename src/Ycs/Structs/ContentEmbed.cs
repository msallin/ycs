namespace Ycs;

internal class ContentEmbed : IContent
{
    internal const int _ref = 5;

    private readonly object? _embed;

    internal ContentEmbed(object? embed)
    {
        _embed = embed;
    }

    /// <summary>The embedded value, null for an embed of JSON null (Yjs' <c>insertEmbed(null)</c>); a byte array as a copy.</summary>
    public object? Embed => ValueFreezer.OnRead(_embed);

    int IContent.Ref => _ref;

    public bool Countable => true;
    public int Length => 1;

    public IReadOnlyList<object?> GetContent() => new object?[] { Embed };

    public IContent Copy() => new ContentEmbed(_embed);

    public IContent Splice(int offset) => throw TypeContent.Unsplittable();

    public bool MergeWith(IContent right)
    {
        return false;
    }

    void IContent.Integrate(Transaction transaction, ItemRef item)
    {
        // Do nothing.
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
        encoder.WriteJson(_embed);
    }

    internal static ContentEmbed Read<TDecoder>(ref TDecoder decoder, ReadOnlySpan<byte> input)
        where TDecoder : struct, IUpdateDecoder
    {
        var content = decoder.ReadJson(input);
        return new ContentEmbed(content);
    }
}
