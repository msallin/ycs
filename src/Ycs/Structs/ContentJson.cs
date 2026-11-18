using System.Diagnostics;

namespace Ycs;

internal class ContentJson : IContent
{
    internal const int _ref = 2;

    // Yjs reads the string 'undefined' as undefined and writes it back: it is kept as JsValue.Undefined
    // and read as null (GetContent, ValueAt).
    private readonly List<object?> _content;

    // Whether _content may hold JsValue.Undefined (set when one is read, kept by Splice, Copy and MergeWith).
    private bool _mayHoldUndefined;

    private ContentJson(List<object?> content, bool mayHoldUndefined)
    {
        _content = content;
        _mayHoldUndefined = mayHoldUndefined;
    }

    int IContent.Ref => _ref;

    public bool Countable => true;
    public int Length => _content.Count;

    public IReadOnlyList<object?> GetContent() =>
        _mayHoldUndefined ? _content.Select(c => c == JsValue.Undefined ? null : c).ToList() : _content.AsReadOnly();

    internal object? ValueAt(int index) => _content[index] == JsValue.Undefined ? null : _content[index];

    public IContent Copy() => new ContentJson(_content.ToList(), _mayHoldUndefined);

    public IContent Splice(int offset)
    {
        var right = Tail(offset);
        _content.RemoveRange(offset, _content.Count - offset);
        return right;
    }

    /// <summary>The values from <paramref name="offset"/> on as a new content; this one is not changed.</summary>
    internal ContentJson Tail(int offset) => new(_content.GetRange(offset, _content.Count - offset), _mayHoldUndefined);

    public bool MergeWith(IContent right)
    {
        Debug.Assert(right is ContentJson);
        var other = (ContentJson)right;
        _content.AddRange(other._content);
        _mayHoldUndefined |= other._mayHoldUndefined;
        return true;
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
        var len = _content.Count;
        encoder.WriteLength(len - offset);
        for (int i = offset; i < len; i++)
        {
            var value = _content[i];
            encoder.WriteString(value == JsValue.Undefined ? "undefined" : YJson.Stringify(value));
        }
    }

    internal static ContentJson Read<TDecoder>(ref TDecoder decoder, ReadOnlySpan<byte> input)
        where TDecoder : struct, IUpdateDecoder
    {
        var len = decoder.ReadLength(input);

        // Each value takes at least one byte (in v1; in v2 the strings are in their own column).
        var content = new List<object?>(Limits.Capacity(len, decoder.Reader.Remaining));

        var undefined = false;
        for (int i = 0; i < len; i++)
        {
            var jsonStr = decoder.ReadString(input);
            if (string.Equals(jsonStr, "undefined", StringComparison.Ordinal))
            {
                undefined = true;
                content.Add(JsValue.Undefined);
            }
            else
            {
                content.Add(YJson.Parse(jsonStr, decoder.Reader.MaxDepth));
            }
        }

        return new ContentJson(content, undefined);
    }
}
