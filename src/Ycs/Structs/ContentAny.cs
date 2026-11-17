#nullable disable // Not annotated for nullable reference types yet.

using System.Collections;
using System.Diagnostics;

namespace Ycs;

internal class ContentAny : IContentEx
{
    internal const int _ref = 8;

    private List<object> _content;

    internal ContentAny(IEnumerable content)
    {
        _content = new List<object>();
        foreach (var v in content)
        {
            _content.Add(v);
        }
    }

    internal ContentAny(IEnumerable<object> content)
        : this(content.ToList())
    {
        // Do nothing.
    }

    private ContentAny(List<object> content)
    {
        _content = content;
    }

    /// <summary>Content of one value.</summary>
    internal static ContentAny Single(object value) => new ContentAny(new List<object>(1) { value });

    /// <summary>Content that takes <paramref name="content"/> over, without a copy.</summary>
    internal static ContentAny Own(List<object> content) => new ContentAny(content);

    public bool Countable => true;
    public int Length => _content.Count;

    int IContentEx.Ref => _ref;

    public IReadOnlyList<object> GetContent() => new ContentView(_content);

    internal object ValueAt(int index) => ValueFreezer.OnRead(_content[index]);

    public IContent Copy() => new ContentAny(_content.ToList());

    public IContent Splice(int offset)
    {
        var right = new ContentAny(_content.GetRange(offset, _content.Count - offset));
        _content.RemoveRange(offset, _content.Count - offset);
        return right;
    }

    public bool MergeWith(IContent right)
    {
        Debug.Assert(right is ContentAny);
        _content.AddRange((right as ContentAny)._content);
        return true;
    }

    /// <summary>Appends <paramref name="values"/>, as <see cref="MergeWith"/> appends a content of them.</summary>
    internal void Append(List<object> values) => _content.AddRange(values);

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
        int length = _content.Count;
        encoder.WriteLength(length - offset);

        for (int i = offset; i < length; i++)
        {
            var c = _content[i];
            encoder.WriteAny(c);
        }
    }

    internal static ContentAny Read(IUpdateDecoder decoder)
    {
        var length = decoder.ReadLength();

        // Each value takes at least one byte.
        var cs = new List<object>(Limits.Capacity(length, decoder.Reader.Remaining));

        for (int i = 0; i < length; i++)
        {
            var c = decoder.ReadAny();
            cs.Add(c);
        }

        return new ContentAny(cs);
    }
}
