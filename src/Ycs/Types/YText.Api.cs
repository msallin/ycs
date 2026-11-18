#nullable enable

using System.Buffers;
using System.Diagnostics;
using System.Text;

namespace Ycs;

public partial class YText
{
    /// <summary>The number of UTF-16 code units of text, and one for each embed.</summary>
    public new int Length => Doc == null ? Preview()?.Length ?? 0 : base.Length;

    /// <summary>
    /// Observes the events of this text (Yjs' <c>observe(f)</c>). Disposing the result unsubscribes.
    /// </summary>
    public IDisposable Observe(Action<YTextEvent> handler) => AddObserver(handler);

    public YText(string? text = null)
    {
        if (text != null)
        {
            AddPending(new PendingOperation(PendingKind.Insert, 0, 0, null, text, null));
        }
    }

    /// <summary>
    /// Applies <paramref name="delta"/> (Yjs' <c>applyDelta</c>): inserts, retains (which formats) and deletes, in order, from the start.
    /// </summary>
    /// <param name="sanitize">Whether a newline that ends the last insert is kept (Quill's trailing newline is not part of the text when false).</param>
    /// <exception cref="ArgumentException">The delta holds an <see cref="Delta.InsertItems"/>, which is for arrays; nothing was applied.</exception>
    public void ApplyDelta(IEnumerable<Delta> delta, bool sanitize = true)
    {
        ArgumentNullException.ThrowIfNull(delta);

        var ops = delta as IReadOnlyList<Delta> ?? delta.ToList();
        foreach (var op in ops)
        {
            if (op is Delta.InsertItems)
            {
                throw new ArgumentException("A text takes Insert, Retain and Delete operations; InsertItems is for arrays. Nothing was applied.", nameof(delta));
            }

            if (op is Delta.Insert insert)
            {
                CheckNotDocument(insert.Value, nameof(delta));
            }
        }

        if (Doc != null)
        {
            Doc.Transact(tr =>
            {
                var curPos = new ItemTextListPosition(default, _start, 0);
                for (int i = 0; i < ops.Count; i++)
                {
                    var op = ops[i];

                    switch (op)
                    {
                        case Delta.Insert insert:
                            // Quill assumes that the content starts with an empty paragraph.
                            // Yjs/Y.Text assumes that it starts empty. We always hide that
                            // there is a newline at the end of the content.
                            // If we omit this step, clients will see a different number of paragraphs,
                            // but nothing bad will happen.
                            var ins = insert.Value.Stored();
                            if (!sanitize && ins is string insertStr && i == ops.Count - 1 && curPos.Right.IsNull && insertStr.EndsWith('\n'))
                            {
                                ins = insertStr.Substring(0, insertStr.Length - 1);
                            }

                            if (ins is string str)
                            {
                                if (str.Length > 0)
                                {
                                    InsertText(tr, ref curPos, str.AsSpan(), StoredAttributes(op.Attributes));
                                }
                            }
                            else
                            {
                                InsertText(tr, ref curPos, ins, StoredAttributes(op.Attributes));
                            }

                            break;
                        case Delta.Retain retain:
                            FormatText(tr, ref curPos, retain.Length, StoredAttributes(op.Attributes) ?? new Dictionary<string, object?>());
                            break;
                        case Delta.Delete delete:
                            DeleteText(tr, ref curPos, delete.Length);
                            break;
                    }
                }
            });
        }
        else
        {
            // As Yjs ('this._pending.push(() => this.applyDelta(delta))'): applied with the default 'sanitize' once
            // the text is in a document, whatever was given.
            AddPending(new PendingOperation(PendingKind.ApplyDelta, 0, 1, null, ops.ToArray(), null));
        }
    }

    /// <summary>The text as a delta: runs of inserted text and embeds, with the attributes in effect (Yjs' <c>toDelta()</c>).</summary>
    public IReadOnlyList<Delta> ToDelta() => Doc == null ? Preview()?.ToDelta() ?? [] : ComputeDelta(null, null, null);

    /// <summary>
    /// The text as of <paramref name="snapshot"/> (the current text when null, with <paramref name="previous"/> given); with
    /// <paramref name="previous"/>, also what was added since it and what was removed, each run with a
    /// <c>ychange</c> attribute (an object of <c>type</c>, <c>added</c> or <c>removed</c>, and <c>user</c> when
    /// <paramref name="computeYChange"/> sets one).
    /// </summary>
    /// <exception cref="InvalidOperationException">The text is not in a document yet.</exception>
    public IReadOnlyList<Delta> ToDelta(Snapshot? snapshot, Snapshot? previous = null, Func<YChangeKind, ItemId, YChange>? computeYChange = null)
    {
        if (snapshot == null && previous == null)
        {
            throw new ArgumentNullException(nameof(snapshot), "Pass a snapshot, a previous snapshot or both; ToDelta() reads the current text.");
        }

        return ComputeDelta(snapshot, previous, computeYChange);
    }

    /// <summary>
    /// The runs of <see cref="ToDelta(Snapshot?, Snapshot?, Func{YChangeKind, ItemId, YChange}?)"/> without their characters: each run's length,
    /// attributes and, for an embed, its value, split exactly as the delta splits them, and in time and memory
    /// that do not depend on how many characters a run holds. With no snapshot the runs are those of <see cref="ToDelta()"/>.
    /// The lengths add up to <see cref="Length"/> when no snapshot is given.
    /// </summary>
    /// <exception cref="InvalidOperationException">A snapshot is given and the text is not in a document yet.</exception>
    public IReadOnlyList<TextRun> ToRuns(Snapshot? snapshot = null, Snapshot? previous = null, Func<YChangeKind, ItemId, YChange>? computeYChange = null)
    {
        if (snapshot == null && previous == null && Doc == null)
        {
            return Preview()?.ToRuns() ?? [];
        }

        var sink = new RunSink();
        Walk(snapshot, previous, computeYChange, sink);
        return sink.Runs;
    }

    private interface IRunSink
    {
        void Chars(ItemRef item);

        void Text(int length, YAttributes? attributes);

        void Embed(YValue value, YAttributes? attributes);
    }

    private sealed class RunSink : IRunSink
    {
        internal List<TextRun> Runs { get; } = [];

        public void Chars(ItemRef item)
        {
        }

        public void Text(int length, YAttributes? attributes) => Runs.Add(new TextRun(length, attributes, null));

        public void Embed(YValue value, YAttributes? attributes) => Runs.Add(new TextRun(1, attributes, value));
    }

    private sealed class DeltaSink : IRunSink
    {
        internal List<Delta> Ops { get; } = [];

        // Appending to a string is quadratic in the run's length.
        private readonly StringBuilder _str = new();

        public void Chars(ItemRef item) => _str.Append(item.Chars);

        public void Text(int length, YAttributes? attributes)
        {
            Ops.Add(new Delta.Insert(_str.ToString()) { Attributes = attributes });
            _str.Clear();
        }

        public void Embed(YValue value, YAttributes? attributes) => Ops.Add(new Delta.Insert(value) { Attributes = attributes });
    }

    private List<Delta> ComputeDelta(Snapshot? snapshot, Snapshot? prevSnapshot, Func<YChangeKind, ItemId, YChange>? computeYChange)
    {
        var sink = new DeltaSink();
        Walk(snapshot, prevSnapshot, computeYChange, sink);
        return sink.Ops;
    }

    // The walk of ToDelta and ToRuns: the sink gets each visible string item's characters, then the run they end, and each embed.
    private void Walk(Snapshot? snapshot, Snapshot? prevSnapshot, Func<YChangeKind, ItemId, YChange>? computeYChange, IRunSink sink)
    {
        var currentAttributes = new Dictionary<string, object?>();
        var doc = Doc;
        var strLength = 0;

        var n = _start;

        void packStr()
        {
            if (strLength > 0)
            {
                sink.Text(strLength, currentAttributes.Count > 0 ? AttributeValues(currentAttributes, skipNull: false) : null);
                strLength = 0;
            }
        }

        void computeDelta()
        {
            while (!n.IsNull)
            {
                if (n.IsVisible(snapshot) || (prevSnapshot != null && n.IsVisible(prevSnapshot)))
                {
                    switch (n.Kind)
                    {
                        case SlotKind.String:
                            if (!currentAttributes.TryGetValue(ChangeKey, out var val))
                            {
                                val = null;
                            }

                            var cur = val as YChange?;

                            if (snapshot != null && !n.IsVisible(snapshot))
                            {
                                if (cur == null || cur.Value.User != n.Id.Client || cur.Value.Kind != YChangeKind.Removed)
                                {
                                    packStr();
                                    currentAttributes[ChangeKey] = computeYChange != null ? computeYChange(YChangeKind.Removed, n.Id) : new YChange(YChangeKind.Removed, null);
                                }
                            }
                            else if (prevSnapshot != null && !n.IsVisible(prevSnapshot))
                            {
                                if (cur == null || cur.Value.User != n.Id.Client || cur.Value.Kind != YChangeKind.Added)
                                {
                                    packStr();
                                    currentAttributes[ChangeKey] = computeYChange != null ? computeYChange(YChangeKind.Added, n.Id) : new YChange(YChangeKind.Added, null);
                                }
                            }
                            else if (cur != null)
                            {
                                packStr();
                                currentAttributes.Remove(ChangeKey);
                            }

                            sink.Chars(n);
                            strLength += n.Length;
                            break;
                        case SlotKind.Type:
                        case SlotKind.Embed:
                            packStr();
                            sink.Embed(n.Embedded, currentAttributes.Count > 0 ? AttributeValues(currentAttributes, skipNull: false) : null);
                            break;
                        case SlotKind.Format:
                            if (n.IsVisible(snapshot))
                            {
                                packStr();
                                UpdateCurrentAttributes(currentAttributes, n.FormatKey, n.FormatValue);
                            }
                            break;
                    }
                }

                n = n.Right.AsItem;
            }

            packStr();
        }

        if (snapshot != null || prevSnapshot != null)
        {
            if (doc == null)
            {
                throw new InvalidOperationException("The text is not in a document yet, so it has no state as of a snapshot. Nothing was read.");
            }

            // Snapshots are merged again after the transaction, so we need to keep the
            // transaction alive until we are done.
            doc.Transact(tr =>
            {
                // Visibility is decided per item, by its first clock: Yjs' items.
                tr.MaterializeRunExtensions();
                if (snapshot != null)
                {
                    Transaction.SplitSnapshotAffectedStructs(tr, snapshot);
                }

                if (prevSnapshot != null)
                {
                    Transaction.SplitSnapshotAffectedStructs(tr, prevSnapshot);
                }

                computeDelta();
            }, "cleanup");
        }
        else
        {
            computeDelta();
        }
    }

    public void Insert(int index, string text, YAttributes? attributes = null) => Insert(index, text.AsMemory(), attributes);

    /// <summary>
    /// Inserts <paramref name="text"/> at <paramref name="index"/> from the caller's memory, with no string in between: the
    /// characters are copied once, into the document. In a text that is not in a document yet the operation keeps its own copy.
    /// </summary>
    public void Insert(int index, ReadOnlyMemory<char> text, YAttributes? attributes = null)
    {
        // As in Yjs, the search marker is only used for inserts without explicit attributes.
        if (text.IsEmpty)
        {
            return;
        }

        var doc = Doc;
        if (doc != null)
        {
            doc.Transact((self: this, index, text, attributes: StoredAttributes(attributes)), static (s, tr) => s.self.InsertInTransaction(tr, s.index, s.text, s.attributes));
        }
        else
        {
            // ToString of a memory that spans a whole string is that string.
            AddPending(new PendingOperation(PendingKind.Insert, index, 0, null, text.ToString(), attributes));
        }
    }

    /// <summary>
    /// Inserts an embed: a shared type (which becomes part of this text, as in an array), or any
    /// other value as JSON. It counts one towards <see cref="Length"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The embed is a <see cref="YDoc"/>: a subdocument goes into an array or a map, as a value; nothing was inserted.</exception>
    public void InsertEmbed(int index, YValue embed, YAttributes? attributes = null)
    {
        CheckNotDocument(embed, nameof(embed));
        if (Doc != null)
        {
            Doc.Transact((self: this, index, embed: embed.Stored(), attributes: StoredAttributes(attributes)), static (s, tr) =>
            {
                var pos = s.self.FindPosition(tr, s.index, useSearchMarker: s.attributes == null);
                s.self.InsertText(tr, ref pos, s.embed, s.attributes ?? new Dictionary<string, object?>());
            });
        }
        else
        {
            AddPending(new PendingOperation(PendingKind.InsertEmbed, index, 0, null, embed, attributes ?? YAttributes.Empty));
        }
    }

    private static void CheckNotDocument(YValue embed, string paramName)
    {
        if (embed.Kind == YValueKind.Document)
        {
            throw new ArgumentException("A text cannot embed a subdocument. Put the YDoc in a YArray or a YMap, as a value. Nothing was inserted.", paramName);
        }
    }

    /// <summary>Inserts <paramref name="text"/> at the end.</summary>
    public void Append(string text, YAttributes? attributes = null) => Insert(Length, text, attributes);

    public void Remove(int index, int length)
    {
        if (length == 0)
        {
            return;
        }

        if (Doc != null)
        {
            Doc.Transact((self: this, index, length), static (s, tr) => s.self.DeleteInTransaction(tr, s.index, s.length));
        }
        else
        {
            AddPending(new PendingOperation(PendingKind.Delete, index, length, null, null, null));
        }
    }

    public void Format(int index, int length, YAttributes attributes)
    {
        ArgumentNullException.ThrowIfNull(attributes);

        if (length == 0)
        {
            return;
        }

        if (Doc != null)
        {
            Doc.Transact((self: this, index, length, attributes: StoredAttributes(attributes)), static (s, tr) =>
            {
                var pos = s.self.FindPosition(tr, s.index, useSearchMarker: false);
                if (pos.Right.IsNull)
                {
                    return;
                }

                s.self.FormatText(tr, ref pos, s.length, s.attributes);
            });
        }
        else
        {
            AddPending(new PendingOperation(PendingKind.Format, index, length, null, null, attributes));
        }
    }

    /// <summary>
    /// Makes this text plain text: deletes, in one transaction, every embed, nested type and other
    /// content that is not a string, and every formatting attribute. Afterwards <c>Length</c> is the
    /// length of <see cref="ToString"/>, and an index into one is an index into the other. For a peer
    /// that shows the text in a plain-text editor (a textarea), where such content would shift every
    /// later position. Strings are left as they are.
    /// </summary>
    /// <returns>Whether anything was deleted; when not, no transaction is started.</returns>
    /// <exception cref="InvalidOperationException">The text is not in a document yet.</exception>
    public bool RemoveNonText()
    {
        if (Doc == null)
        {
            throw new InvalidOperationException("The text is not in a document yet; add it to one first. Nothing was changed.");
        }

        List<ItemRef>? remove = null;
        for (var n = _start; !n.IsNull; n = n.Right.AsItem)
        {
            if (!n.Deleted && (n.Kind == SlotKind.Format || (n.Countable && n.Kind != SlotKind.String)))
            {
                (remove ??= new List<ItemRef>()).Add(n);
            }
        }

        if (remove == null)
        {
            return false;
        }

        Doc.Transact(tr =>
        {
            foreach (var item in remove)
            {
                item.Delete(tr);
            }

            // The items are deleted directly, not by index, so the markers' indexes no longer hold.
            ClearSearchMarkers();
        });

        return true;
    }

    /// <summary>
    /// The text, without formatting and embeds (Yjs' <c>toString()</c>). The string is kept until the
    /// text changes, so asking again for an unchanged text allocates nothing; to fill a buffer of your
    /// own instead, use <see cref="CopyTo"/> or <see cref="WriteTo"/>.
    /// </summary>
    public override string ToString()
    {
        if (Doc == null)
        {
            return Preview()?.ToString() ?? string.Empty;
        }

        var store = Doc?.Store;
        if (_string != null && store != null && _stringVersion == store.Version && _stringLength == Length)
        {
            return _string;
        }

        // Length also counts the embeds, which write no character: one walk counts the characters, then they are written
        // straight into the string.
        var count = 0;
        for (var n = _start; !n.IsNull; n = n.Right.AsItem)
        {
            if (!n.Deleted && n.Countable && n.Kind == SlotKind.String)
            {
                count += n.Length;
            }
        }

        var result = count == 0 ? string.Empty : string.Create(count, this, static (span, text) => text.CopyTo(span));

        if (store != null)
        {
            _string = result;
            _stringVersion = store.Version;
            _stringLength = Length;
        }

        return result;
    }

    /// <summary>
    /// The characters of the range <paramref name="start"/>, <paramref name="length"/> long, in the index space of <see cref="Length"/>,
    /// <see cref="Insert"/>, <see cref="Remove"/> and <see cref="Format"/>: an embed in the range counts one but adds nothing to the
    /// result. It walks the text (a pooled copy, no string of the whole), so reading a character at a time is quadratic: use <see cref="CopyTo"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The range is not within the text, that is within 0 and <see cref="Length"/>.</exception>
    public string Substring(int start, int length)
    {
        var total = Length;
        if (start < 0 || length < 0 || start > total - length)
        {
            throw new ArgumentOutOfRangeException(nameof(start), $"The range {start}..{(long)start + length} is not within the text of {total} positions.");
        }

        if (Doc == null)
        {
            return Preview()?.Substring(start, length) ?? string.Empty;
        }

        var buffer = ArrayPool<char>.Shared.Rent(total);
        try
        {
            // The characters before the range, and before its end, in the string without the embeds.
            var from = 0;
            var to = 0;
            var written = 0;
            var position = 0;
            for (var n = _start; !n.IsNull; n = n.Right.AsItem)
            {
                if (n.Deleted || !n.Countable)
                {
                    continue;
                }

                var count = n.Length;
                if (n.Kind == SlotKind.String)
                {
                    n.CopyCharsTo(buffer.AsSpan(written));
                    written += count;
                    from += Math.Clamp(start - position, 0, count);
                    to += Math.Clamp(start + length - position, 0, count);
                }

                position += count;
            }

            return new string(buffer, from, to - from);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer, clearArray: true);
        }
    }

    /// <summary>
    /// The characters of the text as of <paramref name="snapshot"/>, as <see cref="ToString()"/> returns the current ones: embeds are left out.
    /// </summary>
    /// <exception cref="InvalidOperationException">The text is not in a document yet.</exception>
    public string ToString(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var result = new StringBuilder();
        foreach (var op in ComputeDelta(snapshot, null, null))
        {
            if (op is Delta.Insert insert && insert.Value.TryGetString(out var str))
            {
                result.Append(str);
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Copies the text, as <see cref="ToString()"/> returns it, into <paramref name="destination"/>
    /// without allocating. <see cref="Length"/> is always enough room (it also counts
    /// embeds, which add nothing to the characters).
    /// </summary>
    /// <returns>How many characters were written.</returns>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short; it may have been written to.</exception>
    public int CopyTo(Span<char> destination)
    {
        if (Doc == null)
        {
            return Preview() is { } preview ? preview.CopyTo(destination) : 0;
        }

        var written = 0;
        for (var n = _start; !n.IsNull; n = n.Right.AsItem)
        {
            if (!n.Deleted && n.Countable && n.Kind == SlotKind.String)
            {
                var length = n.Length;
                if (length > destination.Length - written)
                {
                    throw new ArgumentException($"The destination holds {destination.Length} characters; the text has more. Pass at least Length ({Length}).", nameof(destination));
                }

                n.CopyCharsTo(destination.Slice(written));
                written += length;
            }
        }

        return written;
    }

    /// <summary>
    /// Writes the text, as <see cref="ToString"/> returns it, to <paramref name="writer"/>, one run of
    /// characters at a time, without allocating. If the writer's <c>GetSpan</c> edits the text, each run is written as it is
    /// when its span is given: a run deleted meanwhile writes nothing, one split or merged writes its current text from where it began.
    /// </summary>
    public void WriteTo(IBufferWriter<char> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (Doc == null)
        {
            Preview()?.WriteTo(writer);
            return;
        }

        var n = _start;
        while (!n.IsNull)
        {
            Debug.Assert(Doc != null);
            n = !n.Deleted && n.Countable && n.Kind == SlotKind.String ? n.WriteCharsTo(writer, Doc.Store) : n.Right.AsItem;
        }
    }

    public YText Clone() => (YText)InternalClone();

    /// <summary>
    /// Returns the unformatted string representation of this YText type. Mirrors Yjs' <c>toJSON()</c>
    /// (<c>toJSON() { return this.toString() }</c>).
    /// </summary>
    internal override object ToJsonTree() => ToString();

    public void RemoveAttribute(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (Doc != null)
        {
            Doc.Transact((self: this, name), static (s, tr) => s.self.TypeMapDelete(tr, s.name));
        }
        else
        {
            AddPending(new PendingOperation(PendingKind.RemoveAttribute, 0, 0, name, null, null));
        }
    }

    public void SetAttribute(string name, YValue value)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (Doc != null)
        {
            Doc.Transact((self: this, name, value: value.Stored()), static (s, tr) => s.self.TypeMapSet(tr, s.name, s.value));
        }
        else
        {
            AddPending(new PendingOperation(PendingKind.SetAttribute, 0, 0, name, value, null));
        }
    }

    /// <summary>The text attribute <paramref name="name"/> (Yjs' <c>getAttribute</c>); <see cref="YValue.Null"/> when it is not set (<see cref="TryGetAttribute"/> tells it from one set to null).</summary>
    public YValue GetAttribute(string name) => TryGetAttribute(name, out var value) ? value : YValue.Null;

    /// <summary>Reads the text attribute <paramref name="name"/>; false when it is not set.</summary>
    public bool TryGetAttribute(string name, out YValue value)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (Doc == null)
        {
            if (Preview() is { } preview)
            {
                return preview.TryGetAttribute(name, out value);
            }

            value = default;
            return false;
        }

        if (TryTypeMapGet(name, out var stored))
        {
            value = YValue.FromStored(stored);
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// All attribute name/value pairs (null values included), as of <paramref name="snapshot"/> when it is given (it only reads:
    /// an item merged across the snapshot's boundary is seen whole, as in Yjs).
    /// </summary>
    public IReadOnlyDictionary<string, YValue> GetAttributes(Snapshot? snapshot = null)
    {
        if (snapshot != null)
        {
            return MapAtSnapshot(snapshot);
        }

        if (Doc == null)
        {
            return Preview()?.GetAttributes() ?? new Dictionary<string, YValue>();
        }

        var attributes = new Dictionary<string, YValue>();
        foreach (var kvp in TypeMapEnumerateValues())
        {
            attributes[kvp.Key] = YValue.FromStored(kvp.Value);
        }

        return attributes;
    }

    /// <summary>
    /// Removes the formatting attributes that no longer format anything (Yjs' <c>cleanupYTextFormatting</c>): in one
    /// transaction, over the whole text. The text is in a document.
    /// </summary>
    /// <returns>How many formatting attributes were removed.</returns>
    internal int CleanupFormatting()
    {
        var res = 0;

        Debug.Assert(Doc != null);
        Doc.Transact(transaction =>
        {
            var start = _start;
            var end = _start;
            OrderedDictionary<string, object?>? startAttributes = null;
            OrderedDictionary<string, object?>? currentAttributes = null;

            while (!end.IsNull)
            {
                if (!end.Deleted)
                {
                    switch (end.Kind)
                    {
                        case SlotKind.Format:
                            UpdateCurrentAttributes(ref currentAttributes, end.FormatKey, end.FormatValue);
                            break;
                        default:
                            res += CleanupFormattingGap(transaction, start, end, startAttributes, ref currentAttributes);
                            startAttributes = Copy(currentAttributes);
                            start = end;
                            break;
                    }
                }

                end = end.Right.AsItem;
            }
        });

        return res;
    }
}
