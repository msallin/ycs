namespace Ycs;

// The text of a String item at one moment, for the readers that take a content object (ItemRef.Content); the store
// holds no such object, the arena holds the text.
internal sealed class ContentChars(string text) : IContentEx
{
    int IContentEx.Ref => ContentString._ref;

    public bool Countable => true;

    public int Length => text.Length;

    public IReadOnlyList<object> GetContent()
    {
        var result = new object[text.Length];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = text[i].ToString();
        }

        return result;
    }

    public IContent Copy() => throw Unsupported();

    public IContent Splice(int offset) => throw Unsupported();

    public bool MergeWith(IContent right) => throw Unsupported();

    void IContentEx.Integrate(Transaction transaction, ItemRef item) => throw Unsupported();

    void IContentEx.Delete(Transaction transaction) => throw Unsupported();

    void IContentEx.Gc(StructStore store) => throw Unsupported();

    void IContentEx.Write(IUpdateEncoder encoder, int offset) => throw Unsupported();

    private static NotSupportedException Unsupported() => new("A String's text is read, never changed, through its snapshot.");
}
