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

    // Above this length a run that grows by merging moves into a StringBuilder.
    private const int BuilderThreshold = 32;

    // Yjs keeps a string and appends to it on merge. Here the text is a string as long as it is short
    // or never grew: an insert keeps the caller's string, a decoded item the decoder's, without a
    // copy. A run that keeps growing by merges (typing at its end) moves into a StringBuilder once it
    // is longer than BuilderThreshold, where '+=' would copy the whole run on every keystroke.
    // Exactly one of the two is set.
    private string? _string;
    private StringBuilder? _builder;

    internal ContentString(string value)
    {
        _string = value;
    }

    int IContentEx.Ref => _ref;

    public bool Countable => true;

    /// <summary>Length in UTF-16 code units, as in JavaScript.</summary>
    public int Length => _string?.Length ?? _builder!.Length;

    public string GetString() => _string ?? _builder!.ToString();

    /// <summary>Copies the text into <paramref name="destination"/>, which is at least <see cref="Length"/> long.</summary>
    internal void CopyTo(Span<char> destination)
    {
        if (_string != null)
        {
            _string.CopyTo(destination);
        }
        else
        {
            _builder!.CopyTo(0, destination, _builder.Length);
        }
    }

    /// <summary>
    /// One string per UTF-16 code unit, as Yjs' <c>str.split('')</c> (a surrogate pair gives two). Prefer
    /// <see cref="GetString"/>.
    /// </summary>
    public IReadOnlyList<object> GetContent()
    {
        var chars = GetString();
        var result = new object[chars.Length];
        for (int i = 0; i < chars.Length; i++)
        {
            result[i] = chars[i].ToString();
        }

        return result;
    }

    public IContent Copy() => new ContentString(GetString());

    public IContent Splice(int offset)
    {
        string right;
        if (_string != null)
        {
            right = _string.Substring(offset);
            _string = _string.Substring(0, offset);
        }
        else
        {
            right = _builder!.ToString(offset, _builder.Length - offset);
            _builder.Length = offset;
        }

        // Prevent encoding invalid documents because of splitting of surrogate pairs.
        var firstCharCode = _string != null ? _string[offset - 1] : _builder![offset - 1];
        if (firstCharCode >= 0xD800 && firstCharCode <= 0xDBFF)
        {
            // Last character of the left split is the start of a surrogate utf16/ucs2 pair.
            // We don't support splitting of surrogate pairs because this may lead to invalid documents.
            // Replace the invalid character with a unicode replacement character U+FFFD.
            if (_string != null)
            {
                _string = string.Concat(_string.AsSpan(0, offset - 1), "\uFFFD");
            }
            else
            {
                _builder![offset - 1] = '\uFFFD';
            }

            // Replace right as well.
            right = string.Concat("\uFFFD", right.AsSpan(1));
        }

        return new ContentString(right);
    }

    public bool MergeWith(IContent right)
    {
        Debug.Assert(right is ContentString);
        var other = (ContentString)right;
        if (_builder == null)
        {
            var length = _string!.Length + other.Length;
            if (length <= BuilderThreshold)
            {
                _string = string.Concat(_string, other.GetString());
                return true;
            }

            if (other._builder != null)
            {
                // A short run merging into a long one in front of it (deleting backwards, or undoing
                // typing): the long run's builder is taken over and this one inserted at its front,
                // which costs the short run, not the long one. The right content is dropped with its item.
                _builder = other._builder.Insert(0, _string);
                _string = null;
                other._builder = null;
                other._string = string.Empty;
                return true;
            }

            _builder = new StringBuilder(Math.Max(2 * length, 2 * BuilderThreshold)).Append(_string);
            _string = null;
        }

        if (other._string != null)
        {
            _builder.Append(other._string);
        }
        else
        {
            _builder.Append(other._builder);
        }

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
        if (_string != null)
        {
            encoder.WriteString(_string.AsSpan(offset));
            return;
        }

        // Copy out only the part that is written, without an intermediate string.
        var length = _builder!.Length - offset;
        var buffer = System.Buffers.ArrayPool<char>.Shared.Rent(length);
        try
        {
            _builder.CopyTo(offset, buffer, 0, length);
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
