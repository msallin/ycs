namespace Ycs;

internal class ContentEmbed : IContentEx
{
    internal const int _ref = 5;

    private readonly object? _embed;

    internal ContentEmbed(object? embed)
    {
        _embed = embed;
    }

    /// <summary>The embedded value; a byte array as a copy.</summary>
    public object Embed => ValueFreezer.OnRead(_embed)!;

    int IContentEx.Ref => _ref;

    public bool Countable => true;
    public int Length => 1;

    public IReadOnlyList<object> GetContent() => new object[] { Embed };

    public IContent Copy() => new ContentEmbed(_embed);

    public IContent Splice(int offset) => throw TypeContent.Unsplittable();

    public bool MergeWith(IContent right)
    {
        return false;
    }

    void IContentEx.Integrate(Transaction transaction, ItemRef item)
    {
        // Do nothing.
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
        encoder.WriteJson(_embed);
    }

    internal static ContentEmbed Read(IUpdateDecoder decoder)
    {
        var content = decoder.ReadJson();
        return new ContentEmbed(content);
    }
}
