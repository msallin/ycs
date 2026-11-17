// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable enable

using System.Buffers;
using System.Diagnostics;
using System.Text;

namespace Ycs;

public partial class YText
{
    /// <summary>The number of UTF-16 code units of text, and one for each embed.</summary>
    public new int Length => base.Length;

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

                            if (ins is not string str || str.Length > 0)
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
    public IReadOnlyList<Delta> ToDelta() => ComputeDelta(null, null, null);

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

    private List<Delta> ComputeDelta(Snapshot? snapshot, Snapshot? prevSnapshot, Func<YChangeKind, ItemId, YChange>? computeYChange)
    {
        var ops = new List<Delta>();
        var currentAttributes = new Dictionary<string, object?>();
        var doc = Doc;
        // Appending to a string is quadratic in the run's length.
        var str = new StringBuilder();

        var n = _start;

        void packStr()
        {
            if (str.Length > 0)
            {
                // Pack str with attributes to ops.
                ops.Add(new Delta.Insert(str.ToString()) { Attributes = currentAttributes.Count > 0 ? AttributeValues(currentAttributes, skipNull: false) : null });
                str.Clear();
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

                            str.Append(n.Chars);
                            break;
                        case SlotKind.Type:
                        case SlotKind.Embed:
                            packStr();
                            ops.Add(new Delta.Insert(n.Embedded) { Attributes = currentAttributes.Count > 0 ? AttributeValues(currentAttributes, skipNull: false) : null });
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

        return ops;
    }

    public void Insert(int index, string text, IReadOnlyDictionary<string, YValue>? attributes = null)
    {
        // As in Yjs, the search marker is only used for inserts without explicit attributes.
        if (string.IsNullOrEmpty(text))
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
            AddPending(new PendingOperation(PendingKind.Insert, index, 0, null, text, Copy(attributes)));
        }
    }

    /// <summary>
    /// Inserts an embed: a shared type (which becomes part of this text, as in an array), or any
    /// other value as JSON. It counts one towards <see cref="Length"/>.
    /// </summary>
    public void InsertEmbed(int index, YValue embed, IReadOnlyDictionary<string, YValue>? attributes = null)
    {
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
            AddPending(new PendingOperation(PendingKind.InsertEmbed, index, 0, null, embed, Copy(attributes) ?? new Dictionary<string, YValue>()));
        }
    }

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

    public void Format(int index, int length, IReadOnlyDictionary<string, YValue> attributes)
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
            AddPending(new PendingOperation(PendingKind.Format, index, length, null, null, Copy(attributes)));
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
        var store = Doc?.Store;
        if (_string != null && store != null && _stringVersion == store.Version && _stringLength == Length)
        {
            return _string;
        }

        // One walk over the items into a pooled buffer (Length is enough room), then one copy.
        string result;
        var buffer = ArrayPool<char>.Shared.Rent(Length);
        try
        {
            var written = CopyTo(buffer);
            result = written == 0 ? string.Empty : new string(buffer, 0, written);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }

        if (store != null)
        {
            _string = result;
            _stringVersion = store.Version;
            _stringLength = Length;
        }

        return result;
    }

    /// <summary>
    /// Copies the text, as <see cref="ToString"/> returns it, into <paramref name="destination"/>
    /// without allocating. <see cref="Length"/> is always enough room (it also counts
    /// embeds, which the text leaves out).
    /// </summary>
    /// <returns>How many characters were written.</returns>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short; it may have been written to.</exception>
    public int CopyTo(Span<char> destination)
    {
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
    /// characters at a time, without allocating.
    /// </summary>
    public void WriteTo(IBufferWriter<char> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        var n = _start;
        while (!n.IsNull)
        {
            Debug.Assert(Doc != null);
            var id = n.Id;
            var departures = Doc.Store.Departures;
            if (!n.Deleted && n.Countable && n.Kind == SlotKind.String)
            {
                n.WriteCharsTo(writer);
            }

            n = Doc.Store.NextAfter(n, id, departures);
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

    /// <summary>The text attribute <paramref name="name"/> (Yjs' <c>getAttribute</c>); null when it is not set.</summary>
    public YValue? GetAttribute(string name) => TryTypeMapGet(name, out var value) ? YValue.FromStored(value) : default(YValue?);

    public IEnumerable<KeyValuePair<string, YValue>> GetAttributes()
    {
        foreach (var kvp in TypeMapEnumerateValues())
        {
            yield return new KeyValuePair<string, YValue>(kvp.Key, YValue.FromStored(kvp.Value));
        }
    }

    /// <summary>
    /// This function is experimental and subject to change / be removed.
    /// <br/>
    /// Ideally, we don't need this function at all. Formatting attributes should be cleaned up
    /// automatically after each change. This function iterates twice over the complete YText type
    /// and removes unnecessary formatting attributes. This is also helpful for testing.
    /// <br/>
    /// This function won't be exported anymore as soon as there is confidence that the YText type works as intended.
    /// </summary>
    /// <returns>How many formatting attributes have been cleaned up.</returns>
    public int CleanupFormatting()
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
