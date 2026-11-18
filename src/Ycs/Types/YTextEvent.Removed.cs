#nullable enable

namespace Ycs;

public sealed partial class YTextEvent
{
    /// <summary>
    /// The text this event's transaction removed, as runs in ascending order of position in the text as it was
    /// before the transaction: <c>foreach (var run in e.Removed)</c>. Characters inserted and deleted within the
    /// transaction are in neither the old text nor the runs. Formatting counts nothing and yields nothing; the characters of
    /// a formatted run come without their attributes (<see cref="Delta"/> has those). One run is one item of the document:
    /// text that was typed by two clients, or at two times, comes as several runs that follow each other. Which items there are, and so
    /// how many runs, can differ between documents; their joined text and their positions do not. A surrogate half that this
    /// transaction's delete split off reads U+FFFD (as in <see cref="YText.ToString()"/>, where its other half shows so); its
    /// <see cref="RemovedRun.Length"/> is right.
    /// </summary>
    /// <remarks>
    /// Computed on demand, without allocating and without keeping anything: <see cref="RemovedRun.Text"/> is valid
    /// until the next <c>MoveNext</c> or any edit of the document, and the runs themselves only while the removed text exists.
    /// That is in the observers of the transaction, in the deep observers and in <see cref="YDoc.AfterTransaction"/> handlers,
    /// whether or not the document collects garbage; afterwards (<see cref="YDoc.AfterTransactionCleanup"/>, the update
    /// handlers, once the transaction has ended) reading it throws <see cref="InvalidOperationException"/>. Copy what you keep
    /// while it can be read.
    /// <para>
    /// Between two retains of <see cref="Delta"/>, the <see cref="RemovedRun.Length"/> of the runs add up to its
    /// <see cref="Delta.Delete"/>. Do not edit the document while enumerating: the walk is over the items of the text
    /// when each step runs. An observer that edits the text before reading sees only what this event's transaction
    /// removed (the edit is a transaction of its own); text that edit deleted counts nothing, so the positions are those of
    /// the text before this transaction less that text; likewise a transaction that an observer started reports positions in the text after
    /// the earlier transactions' changes.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The removed text can no longer be read.</exception>
    public RemovedRunEnumerator Removed
    {
        get
        {
            if (Transaction.ContentReleased)
            {
                throw new InvalidOperationException(
                    "The removed text of an event can be read only while the transaction's observers and its AfterTransaction " +
                    "handlers run; after that the document may have released it, whether or not it collects garbage. " +
                    "Copy what you keep there.");
            }

            return new RemovedRunEnumerator(this, Target._start);
        }
    }

    /// <summary>One run of removed text: valid while the text can be read (see <see cref="Removed"/>).</summary>
    public readonly ref struct RemovedRun
    {
        internal RemovedRun(int start, int length, ReadOnlySpan<char> text)
        {
            Start = start;
            Length = length;
            Text = text;
        }

        /// <summary>The position of the run in the text before the transaction, in the index space of <see cref="YText.Length"/>.</summary>
        public int Start { get; }

        /// <summary>The positions the run takes in that index space: its characters, and one for an embed or a nested type.</summary>
        public int Length { get; }

        /// <summary>Whether the run is an embed or a nested type: it has a length and no text.</summary>
        public bool IsEmbed => Text.IsEmpty;

        /// <summary>The removed characters; empty for an embed or a nested type. Valid until the next <c>MoveNext</c> or any edit of the document.</summary>
        public ReadOnlySpan<char> Text { get; }
    }

    /// <summary>The removed runs of a <see cref="YTextEvent"/> (<see cref="Removed"/>).</summary>
    public ref struct RemovedRunEnumerator
    {
        private readonly YTextEvent _event;
        private ItemRef _next;
        private int _position;
        private RemovedRun _current;

        internal RemovedRunEnumerator(YTextEvent @event, ItemRef start)
        {
            _event = @event;
            _next = start;
        }

        /// <summary>The current run.</summary>
        public readonly RemovedRun Current => _current;

        /// <summary>Goes on to the next run; false at the end of the text.</summary>
        public bool MoveNext()
        {
            _current = default;
            while (!_next.IsNull)
            {
                var item = _next;
                _next = item.Right.AsItem;

                // Formats are not countable; an item that this transaction added is not in the old text.
                if (!item.Countable || _event.Adds(item))
                {
                    continue;
                }

                var deletes = _event.Deletes(item);
                if (item.Deleted && !deletes)
                {
                    continue;
                }

                var start = _position;
                var length = item.Length;
                _position += length;
                if (deletes)
                {
                    _current = new RemovedRun(start, length, item.Kind == SlotKind.String ? item.Chars : default);
                    return true;
                }
            }

            return false;
        }

        /// <summary>This enumerator, for <c>foreach</c>.</summary>
        public readonly RemovedRunEnumerator GetEnumerator() => this;
    }
}
