// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

using System.Diagnostics;

namespace Ycs;

public class ContentJson : IContentEx
{
    internal const int _ref = 2;

    // Yjs reads the string 'undefined' as undefined and writes it back: it is kept as JsValue.Undefined
    // and read as null (GetContent).
    private readonly List<object> _content;

    internal ContentJson(IEnumerable<object> data)
    {
        _content = new List<object>(data);
    }

    private ContentJson(List<object> other)
    {
        _content = other;
    }

    int IContentEx.Ref => _ref;

    public bool Countable => true;
    public int Length => _content?.Count ?? 0;

    public IReadOnlyList<object> GetContent() =>
        _content.Contains(JsValue.Undefined) ? _content.Select(c => c == JsValue.Undefined ? null : c).ToList() : _content.AsReadOnly();

    public IContent Copy() => new ContentJson(_content.ToList());

    public IContent Splice(int offset)
    {
        var right = new ContentJson(_content.GetRange(offset, _content.Count - offset));
        _content.RemoveRange(offset, _content.Count - offset);
        return right;
    }

    public bool MergeWith(IContent right)
    {
        Debug.Assert(right is ContentJson);
        _content.AddRange((right as ContentJson)._content);
        return true;
    }

    void IContentEx.Integrate(Transaction transaction, Item item)
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
        var len = _content.Count;
        encoder.WriteLength(len - offset);
        for (int i = offset; i < len; i++)
        {
            var value = _content[i];
            encoder.WriteString(value == JsValue.Undefined ? "undefined" : YJson.Stringify(value));
        }
    }

    internal static ContentJson Read(IUpdateDecoder decoder)
    {
        var len = decoder.ReadLength();

        // Each value takes at least one byte (in v1; in v2 the strings are in their own column).
        var content = new List<object>(Limits.Capacity(len, decoder.Reader.Remaining));

        for (int i = 0; i < len; i++)
        {
            var jsonStr = decoder.ReadString();
            var jsonObj = string.Equals(jsonStr, "undefined", StringComparison.Ordinal)
                ? JsValue.Undefined
                : YJson.Parse(jsonStr);
            content.Add(jsonObj);
        }

        return new ContentJson(content);
    }
}
