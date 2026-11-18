namespace Ycs;

internal class ContentBinary : IContent
{
    internal const int _ref = 3;

    private readonly byte[] _content;

    internal ContentBinary(byte[] data)
    {
        _content = data;
    }

    int IContent.Ref => _ref;

    public bool Countable => true;
    public int Length => 1;

    public IReadOnlyList<object> GetContent() => new object[] { _content.Clone() };

    public IContent Copy() => new ContentBinary(_content);

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
        encoder.WriteBuffer(_content);
    }

    internal static ContentBinary Read<TDecoder>(ref TDecoder decoder, ReadOnlySpan<byte> input)
        where TDecoder : IUpdateDecoder
    {
        var content = decoder.ReadBuffer(input);
        return new ContentBinary(content);
    }
}
