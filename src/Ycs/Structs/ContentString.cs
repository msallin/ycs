// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System.Diagnostics;
using System.Text;

namespace Ycs;

public class ContentString : IContentEx
{
    internal const int _ref = 4;

    // Yjs keeps a string and appends to it on merge; a StringBuilder gives the same amortized
    // cost in .NET, where '+=' on a string would copy the whole run on every merged keystroke.
    private readonly StringBuilder _content;

    internal ContentString(string value)
    {
        _content = new StringBuilder(value);
    }

    private ContentString(StringBuilder content)
    {
        _content = content;
    }

    int IContentEx.Ref => _ref;

    public bool Countable => true;

    /// <summary>Length in UTF-16 code units, as in JavaScript.</summary>
    public int Length => _content.Length;

    internal void AppendToBuilder(StringBuilder sb) => sb.Append(_content);

    public string GetString() => _content.ToString();

    /// <summary>The characters, boxed one by one. Prefer <see cref="GetString"/>.</summary>
    public IReadOnlyList<object> GetContent()
    {
        var chars = _content.ToString();
        var result = new object[chars.Length];
        for (int i = 0; i < chars.Length; i++)
        {
            result[i] = chars[i];
        }

        return result;
    }

    public IContent Copy() => new ContentString(new StringBuilder().Append(_content));

    public IContent Splice(int offset)
    {
        var right = new ContentString(_content.ToString(offset, _content.Length - offset));
        _content.Length = offset;

        // Prevent encoding invalid documents because of splitting of surrogate pairs.
        var firstCharCode = _content[offset - 1];
        if (firstCharCode >= 0xD800 && firstCharCode <= 0xDBFF)
        {
            // Last character of the left split is the start of a surrogate utf16/ucs2 pair.
            // We don't support splitting of surrogate pairs because this may lead to invalid documents.
            // Replace the invalid character with a unicode replacement character U+FFFD.
            _content[offset - 1] = '\uFFFD';

            // Replace right as well.
            right._content[0] = '\uFFFD';
        }

        return right;
    }

    public bool MergeWith(IContent right)
    {
        Debug.Assert(right is ContentString);
        _content.Append(((ContentString)right)._content);
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
        // Copy out only the part that is written, without an intermediate string.
        var length = _content.Length - offset;
        var buffer = System.Buffers.ArrayPool<char>.Shared.Rent(length);
        try
        {
            _content.CopyTo(offset, buffer, 0, length);
            encoder.WriteString(buffer.AsSpan(0, length));
        }
        finally
        {
            System.Buffers.ArrayPool<char>.Shared.Return(buffer);
        }
    }

    internal static ContentString Read(IUpdateDecoder decoder)
    {
        return new ContentString(decoder.ReadString());
    }
}
