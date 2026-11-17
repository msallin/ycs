#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

// The values of a decoded Any item; a store item keeps its values in the slab's value arena (ItemRef.Values.cs).
internal sealed class ContentAny : IContentEx
{
    internal const int _ref = 8;

    private readonly object[] _values;

    /// <summary>Content that takes <paramref name="values"/> over, without a copy.</summary>
    internal ContentAny(object[] values) => _values = values;

    public bool Countable => true;
    public int Length => _values.Length;

    int IContentEx.Ref => _ref;

    internal ReadOnlySpan<object> AsSpan() => _values;

    public IReadOnlyList<object> GetContent() => new ContentView(_values);

    internal object ValueAt(int index) => ValueFreezer.OnRead(_values[index]);

    public IContent Copy() => new ContentAny((object[])_values.Clone());

    // A decoded Any is read, never changed: a store item splits and merges its range in the arena.
    public IContent Splice(int offset) => throw new NotSupportedException("A decoded Any is not split; the store item's range is.");

    public bool MergeWith(IContent right) => throw new NotSupportedException("A decoded Any is not merged; the store item's range is.");

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
        encoder.WriteLength(_values.Length - offset);

        for (var i = offset; i < _values.Length; i++)
        {
            encoder.WriteAny(_values[i]);
        }
    }

    internal static ContentAny Read(IUpdateDecoder decoder)
    {
        var length = decoder.ReadLength();

        // Each value takes at least one byte: a length beyond what remains throws before the array is outgrown.
        var values = new object[Limits.Capacity(length, decoder.Reader.Remaining)];

        for (var i = 0; i < length; i++)
        {
            values[i] = decoder.ReadAny();
        }

        return new ContentAny(values);
    }
}
