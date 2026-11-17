using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Ycs;

internal class ContentString : IContentEx
{
    internal const int _ref = 4;

    // Above this length a run that grows by merging moves into a StringBuilder.
    private const int BuilderThreshold = 32;

    // Yjs keeps a string, appends to it on merge and slices it on split (V8 shares a sliced string's
    // characters). Here the text is a slice of a string (_string from _start, _length long) as long as
    // it is short or never grew: an insert keeps the caller's string, a decoded item the decoder's, a
    // split shares its string between both halves, and merging two halves that are still adjacent in
    // it only lengthens the slice. A run that keeps growing by other merges (typing at its end) moves
    // into a StringBuilder once it is longer than BuilderThreshold, where '+=' would copy the whole run
    // on every keystroke. Exactly one of _string and _builder is set.
    private string? _string;
    private int _start;
    private int _length;
    private StringBuilder? _builder;

    internal ContentString(string value)
        : this(value, 0, value.Length)
    {
    }

    private ContentString(string value, int start, int length)
    {
        _string = value;
        _start = start;
        _length = length;
    }

    int IContentEx.Ref => _ref;

    public bool Countable => true;

    /// <summary>Length in UTF-16 code units, as in JavaScript.</summary>
    public int Length => _builder?.Length ?? _length;

    private ReadOnlySpan<char> Slice => _string.AsSpan(_start, _length);

    public string GetString()
    {
        if (_builder != null)
        {
            return _builder.ToString();
        }

        if (_start != 0 || _length != _string!.Length)
        {
            // Keeps the copy: the slice no longer holds on to the rest of the string.
            _string = Slice.ToString();
            _start = 0;
        }

        return _string;
    }

    /// <summary>The text as a span when it is held as a string (not in a builder), without materializing it.</summary>
    internal bool TryGetSpan(out ReadOnlySpan<char> text)
    {
        text = _builder == null ? Slice : default;
        return _builder == null;
    }

    /// <summary>Copies the text into <paramref name="destination"/>, which is at least <see cref="Length"/> long.</summary>
    internal void CopyTo(Span<char> destination)
    {
        if (_builder != null)
        {
            _builder.CopyTo(0, destination, _builder.Length);
        }
        else
        {
            Slice.CopyTo(destination);
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
        if (_builder != null)
        {
            // Copied once into a string, which the halves (and their later splits) then share.
            _string = _builder.ToString();
            _start = 0;
            _length = _string.Length;
            _builder = null;
        }

        var right = new ContentString(_string!, _start + offset, _length - offset);
        _length = offset;

        // Prevent encoding invalid documents because of splitting of surrogate pairs.
        var firstCharCode = _string![_start + offset - 1];
        if (firstCharCode >= 0xD800 && firstCharCode <= 0xDBFF)
        {
            // Last character of the left split is the start of a surrogate utf16/ucs2 pair.
            // We don't support splitting of surrogate pairs because this may lead to invalid documents.
            // Replace the invalid character with a unicode replacement character U+FFFD.
            _string = string.Concat(_string.AsSpan(_start, offset - 1), "\uFFFD");
            _start = 0;

            // Replace right as well.
            right._string = string.Concat("\uFFFD", right.Slice[1..]);
            right._start = 0;
        }

        return right;
    }

    public bool MergeWith(IContent right)
    {
        Debug.Assert(right is ContentString);
        var other = (ContentString)right;
        if (_builder == null)
        {
            if (other._builder == null && ReferenceEquals(_string, other._string) && _start + _length == other._start)
            {
                // Two halves of one split, still adjacent in the string they share.
                _length += other._length;
                return true;
            }

            if (other._builder != null && _length + other.Length > BuilderThreshold)
            {
                // A short run merging into a long one in front of it (deleting backwards, or undoing
                // typing): the long run's builder is taken over and this one inserted at its front,
                // which costs the short run, not the long one. The right content is dropped with its item.
                _builder = other._builder.Insert(0, GetString());
                _string = null;
                other._builder = null;
                other._string = string.Empty;
                other._start = 0;
                other._length = 0;
                return true;
            }
        }

        if (AppendsToString(other.Length))
        {
            SetString(string.Concat(Slice, other.GetString()));
        }
        else if (other._builder != null)
        {
            _builder.Append(other._builder);
        }
        else
        {
            _builder.Append(other.Slice);
        }

        return true;
    }

    internal bool EndsWithHighSurrogate => Length > 0 && char.IsHighSurrogate(_builder != null ? _builder[_builder.Length - 1] : _string![_start + _length - 1]);

    /// <summary>Appends <paramref name="text"/>, as <see cref="MergeWith"/> appends a content of it.</summary>
    internal void Append(string text)
    {
        if (AppendsToString(text.Length))
        {
            SetString(string.Concat(Slice, text));
        }
        else
        {
            _builder.Append(text);
        }
    }

    /// <summary>
    /// Whether <paramref name="added"/> more characters keep this content a string; if not, they go to the builder,
    /// which this makes (with room to grow) when there is none yet.
    /// </summary>
    [MemberNotNullWhen(false, nameof(_builder))]
    private bool AppendsToString(int added)
    {
        if (_builder != null)
        {
            return false;
        }

        var length = _length + added;
        if (length <= BuilderThreshold)
        {
            return true;
        }

        _builder = new StringBuilder(Math.Max(2 * length, 2 * BuilderThreshold)).Append(Slice);
        _string = null;
        return false;
    }

    private void SetString(string value)
    {
        _string = value;
        _start = 0;
        _length = value.Length;
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
        if (_builder == null)
        {
            encoder.WriteString(Slice[offset..]);
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
