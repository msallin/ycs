#nullable enable

namespace Ycs;

public partial class YText
{
    /// <summary>
    /// The characters of the whole text, as <see cref="ToString()"/> returns them, as a sequence of spans, without
    /// allocating: <c>foreach (var chunk in text.Chunks())</c>. See <see cref="Chunks(int, int)"/> for what a chunk is.
    /// The walk goes to the end of the text as it is when each chunk is read: text inserted ahead of it is read, and an edit
    /// of the body does not cut the walk short.
    /// </summary>
    public ChunkEnumerator Chunks() => Open(0, int.MaxValue);

    /// <summary>
    /// The characters of the range <paramref name="start"/>, <paramref name="length"/> long, in the index space of
    /// <see cref="Length"/>, <see cref="Insert"/> and <see cref="Substring"/>, as a sequence of spans, without
    /// allocating, without a transaction and without changing the document (a read, as <see cref="CopyTo"/> is).
    /// An embed or a nested type in the range counts one and yields nothing; formatting yields nothing and counts nothing.
    /// </summary>
    /// <remarks>
    /// Where a chunk ends means nothing: the same text may come as one chunk or as a hundred, and a surrogate pair may
    /// straddle two chunks (use a stateful <see cref="System.Text.Decoder"/> or <see cref="System.Text.Encoder"/> to
    /// convert). A chunk is never empty. A text that is not in a document yet enumerates what was inserted into it.
    /// <para>
    /// <see cref="ChunkEnumerator.Current"/> is valid until the next <see cref="ChunkEnumerator.MoveNext"/> or any edit of the
    /// document. The body of the loop may edit the document (the enumerator is a <c>ref struct</c>: it cannot be stored,
    /// captured or held across an <c>await</c>), but each chunk is read as the text is when <c>MoveNext</c> runs: a run
    /// deleted meanwhile yields nothing, one split or merged yields its current text from where the walk stopped, and
    /// text inserted before the walk's position is not read. The document is not thread-safe: read it as you write it.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The range is not within the text, that is within 0 and <see cref="Length"/>.</exception>
    public ChunkEnumerator Chunks(int start, int length)
    {
        var total = Length;
        if (start < 0 || length < 0 || start > total - length)
        {
            throw new ArgumentOutOfRangeException(nameof(start), $"The range {start}..{(long)start + length} is not within the text of {total} positions.");
        }

        return Open(start, length);
    }

    private ChunkEnumerator Open(int start, int length)
    {
        if (Doc == null)
        {
            return Preview() is { } preview ? preview.Open(start, length) : default;
        }

        if (length == 0)
        {
            return default;
        }

        // The seek reads: it does not split the item the range starts in (that needs a transaction and a version
        // bump), it keeps the offset into it.
        var doc = Doc;
        var skip = start;
        var steps = 0;
        var n = _start;
        while (!n.IsNull)
        {
            if (++steps == doc.LiteralWalkStepsForTests && !doc.RunHeadIndexDisabledForTests)
            {
                steps = 0;
                var reached = doc.Store.SeekRight(n, ref skip, stopAtFormats: false);
                if (reached != n)
                {
                    n = reached;
                    continue;
                }
            }

            if (!n.Deleted && n.Countable)
            {
                if (skip < n.Length)
                {
                    break;
                }

                skip -= n.Length;
            }

            n = n.Right.AsItem;
        }

        return n.IsNull ? default : new ChunkEnumerator(doc.Store, n, new ItemId(n.Id.Client, n.Id.Clock + skip), length);
    }

    /// <summary>
    /// The chunks of a text (<see cref="Chunks()"/>): <c>foreach</c> over it, or <see cref="MoveNext"/> and <see cref="Current"/>.
    /// </summary>
    public ref struct ChunkEnumerator
    {
        private readonly StructStore? _store;
        private ItemRef _held;
        private ItemId _id;
        private int _departures;
        private int _remaining;
        private ReadOnlySpan<char> _current;

        // _held is a hint for the item that holds _id, the first position not read yet.
        internal ChunkEnumerator(StructStore store, ItemRef held, ItemId id, int remaining)
        {
            _store = store;
            _held = held;
            _id = id;
            _departures = store.Departures;
            _remaining = remaining;
        }

        /// <summary>The current chunk: never empty, valid until the next <see cref="MoveNext"/> or any edit of the document.</summary>
        public readonly ReadOnlySpan<char> Current => _current;

        /// <summary>Goes on to the next chunk; false at the end of the range.</summary>
        public bool MoveNext()
        {
            _current = default;
            var store = _store;
            while (_remaining > 0 && !_held.IsNull)
            {
                // The item is looked up again: user code between two steps may have split it (the id then lies beyond
                // its end), or merged and freed it.
                var held = store!.Resolve(_held, _id, _departures);
                if (!held.IsNull && _id.Clock >= held.Id.Clock + held.Length)
                {
                    held = store.Find(_id).AsItem;
                }

                _departures = store.Departures;
                if (held.IsNull)
                {
                    _held = default;
                    return false;
                }

                if (held.Deleted || !held.Countable)
                {
                    GoToRightOf(held);
                    continue;
                }

                var offset = (int)(_id.Clock - held.Id.Clock);
                var count = Math.Min(_remaining, held.Length - offset);
                _remaining -= count;
                var isString = held.Kind == SlotKind.String;
                if (isString)
                {
                    _current = held.Chars.Slice(offset, count);
                }

                if (offset + count == held.Length)
                {
                    GoToRightOf(held);
                }
                else
                {
                    _held = held;
                    _id = new ItemId(_id.Client, _id.Clock + count);
                }

                if (isString)
                {
                    return true;
                }
            }

            return false;
        }

        private void GoToRightOf(ItemRef held)
        {
            _held = held.Right.AsItem;
            if (!_held.IsNull)
            {
                _id = _held.Id;
            }
        }

        /// <summary>This enumerator, for <c>foreach</c>.</summary>
        public readonly ChunkEnumerator GetEnumerator() => this;
    }
}
