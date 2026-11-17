namespace Ycs;

// The text of a decoded String item; a store item keeps its text in the slab's character arena (ItemRef.Chars.cs).
internal sealed class ContentString : IContentEx
{
    internal const int _ref = 4;

    private readonly string _string;

    internal ContentString(string value) => _string = value;

    int IContentEx.Ref => _ref;

    public bool Countable => true;

    /// <summary>Length in UTF-16 code units, as in JavaScript.</summary>
    public int Length => _string.Length;

    public string GetString() => _string;

    internal ReadOnlySpan<char> AsSpan() => _string;

    /// <summary>Copies the text into <paramref name="destination"/>, which is at least <see cref="Length"/> long.</summary>
    internal void CopyTo(Span<char> destination) => _string.CopyTo(destination);

    /// <summary>
    /// One string per UTF-16 code unit, as Yjs' <c>str.split('')</c> (a surrogate pair gives two). Prefer
    /// <see cref="GetString"/>.
    /// </summary>
    public IReadOnlyList<object> GetContent()
    {
        var result = new object[_string.Length];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = _string[i].ToString();
        }

        return result;
    }

    public IContent Copy() => new ContentString(_string);

    // A decoded string is read, never changed: a store item splits and merges its range in the arena.
    public IContent Splice(int offset) => throw new NotSupportedException("A decoded String is not split; the store item's range is.");

    public bool MergeWith(IContent right) => throw new NotSupportedException("A decoded String is not merged; the store item's range is.");

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

    void IContentEx.Write(IUpdateEncoder encoder, int offset) => encoder.WriteString(_string.AsSpan(offset));

    internal static ContentString Read(IUpdateDecoder decoder) => new(decoder.ReadString());
}
