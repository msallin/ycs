// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

using System.Buffers;
using System.Text;

namespace Ycs;

public partial class YText
{
    public YText()
        : this(null)
    {
        // Do nothing.
    }

    public YText(string str)
    {
        if (str != null)
        {
            AddPending(new PendingOperation(PendingKind.Insert, 0, 0, null, str, null));
        }
    }

    public void ApplyDelta(IList<Delta> delta, bool sanitize = true)
    {
        if (Doc != null)
        {
            Doc.Transact(tr =>
            {
                var curPos = new ItemTextListPosition(default, _start, 0);
                for (int i = 0; i < delta.Count; i++)
                {
                    var op = delta[i];

                    if (op.Insert != null)
                    {
                        // Quill assumes that the content starts with an empty paragraph.
                        // Yjs/Y.Text assumes that it starts empty. We always hide that
                        // there is a newline at the end of the content.
                        // If we omit this step, clients will see a different number of paragraphs,
                        // but nothing bad will happen.
                        var insertStr = op.Insert as string;
                        var ins = (!sanitize && insertStr != null && i == delta.Count - 1 && curPos.Right.IsNull && insertStr.EndsWith("\n")) ? insertStr.Substring(0, insertStr.Length - 1)
                            : op.Insert == Delta.NullEmbed ? null
                            : op.Insert;
                        if (!(ins is string) || ((string)ins).Length > 0)
                        {
                            InsertText(tr, ref curPos, ins, op.Attributes);
                        }
                    }
                    else if (op.Retain != null)
                    {
                        FormatText(tr, ref curPos, op.Retain.Value, op.Attributes ?? new Dictionary<string, object>());
                    }
                    else if (op.Delete != null)
                    {
                        DeleteText(tr, ref curPos, op.Delete.Value);
                    }
                }
            });
        }
        else
        {
            // As Yjs ('this._pending.push(() => this.applyDelta(delta))'): applied with the default 'sanitize' once
            // the text is in a document, whatever was given.
            AddPending(new PendingOperation(PendingKind.ApplyDelta, 0, 1, null, delta, null));
        }
    }

    public IList<Delta> ToDelta(Snapshot snapshot = null, Snapshot prevSnapshot = null, Func<YTextChangeType, ID, YTextChangeAttributes> computeYChange = null)
    {
        var ops = new List<Delta>();
        var currentAttributes = new Dictionary<string, object>();
        var doc = Doc;
        // Appending to a string is quadratic in the run's length.
        var str = new StringBuilder();

        var n = _start;

        void packStr()
        {
            if (str.Length > 0)
            {
                // Pack str with attributes to ops.
                var attributes = new Dictionary<string, object>();
                var addAttributes = false;

                foreach (var kvp in currentAttributes)
                {
                    addAttributes = true;
                    attributes[kvp.Key] = kvp.Value;
                }

                var op = new Delta { Insert = str.ToString() };
                if (addAttributes)
                {
                    op.Attributes = attributes;
                }

                ops.Add(op);
                str.Clear();
            }
        }

        void computeDelta()
        {
            while (!n.IsNull)
            {
                if (n.IsVisible(snapshot) || (prevSnapshot != null && n.IsVisible(prevSnapshot)))
                {
                    switch (n.Content)
                    {
                        case ContentString cs:
                            if (!currentAttributes.TryGetValue(ChangeKey, out var val))
                            {
                                val = null;
                            }

                            var cur = val as YTextChangeAttributes;

                            if (snapshot != null && !n.IsVisible(snapshot))
                            {
                                if (cur == null || cur.User != n.Id.Client || cur.Type != YTextChangeType.Removed)
                                {
                                    packStr();
                                    currentAttributes[ChangeKey] = computeYChange != null ? computeYChange(YTextChangeType.Removed, n.Id) : new YTextChangeAttributes { Type = YTextChangeType.Removed };
                                }
                            }
                            else if (prevSnapshot != null && !n.IsVisible(prevSnapshot))
                            {
                                if (cur == null || cur.User != n.Id.Client || cur.Type != YTextChangeType.Added)
                                {
                                    packStr();
                                    currentAttributes[ChangeKey] = computeYChange != null ? computeYChange(YTextChangeType.Added, n.Id) : new YTextChangeAttributes { Type = YTextChangeType.Added };
                                }
                            }
                            else if (cur != null)
                            {
                                packStr();
                                currentAttributes.Remove(ChangeKey);
                            }

                            str.Append(cs.GetString());
                            break;
                        case ContentType:
                        case ContentEmbed:
                            packStr();
                            var op = new Delta { Insert = EmbedOf(n.Content) };
                            if (currentAttributes.Count > 0)
                            {
                                var attrs = new Dictionary<string, object>();
                                op.Attributes = attrs;
                                foreach (var kvp in currentAttributes)
                                {
                                    attrs[kvp.Key] = kvp.Value;
                                }
                            }
                            ops.Add(op);
                            break;
                        case ContentFormat cf:
                            if (n.IsVisible(snapshot))
                            {
                                packStr();
                                UpdateCurrentAttributes(currentAttributes, cf);
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

    public void Insert(int index, string text, IDictionary<string, object> attributes = null)
    {
        // As in Yjs, the search marker is only used for inserts without explicit attributes.
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var doc = Doc;
        if (doc != null)
        {
            doc.Transact((self: this, index, text, attributes), static (s, tr) => s.self.InsertInTransaction(tr, s.index, s.text, s.attributes));
        }
        else
        {
            AddPending(new PendingOperation(PendingKind.Insert, index, 0, null, text, attributes));
        }
    }

    /// <summary>
    /// Inserts an embed: a shared type (which becomes part of this text, as in an array), or any
    /// other value as JSON. It counts one towards <see cref="AbstractType.Length"/>.
    /// </summary>
    public void InsertEmbed(int index, object embed, IDictionary<string, object> attributes = null)
    {
        if (Doc != null)
        {
            Doc.Transact((self: this, index, embed, attributes), static (s, tr) =>
            {
                var pos = s.self.FindPosition(tr, s.index, useSearchMarker: s.attributes == null);
                s.self.InsertText(tr, ref pos, s.embed, s.attributes ?? new Dictionary<string, object>());
            });
        }
        else
        {
            AddPending(new PendingOperation(PendingKind.InsertEmbed, index, 0, null, embed, attributes ?? new Dictionary<string, object>()));
        }
    }

    public void Delete(int index, int length)
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

    public void Format(int index, int length, IDictionary<string, object> attributes)
    {
        if (length == 0)
        {
            return;
        }

        if (Doc != null)
        {
            Doc.Transact((self: this, index, length, attributes), static (s, tr) =>
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

        List<ItemRef> remove = null;
        for (var n = _start; !n.IsNull; n = n.Right.AsItem)
        {
            if (!n.Deleted && (n.Content is ContentFormat || (n.Countable && n.Content is not ContentString)))
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
    /// without allocating. <see cref="AbstractType.Length"/> is always enough room (it also counts
    /// embeds, which the text leaves out).
    /// </summary>
    /// <returns>How many characters were written.</returns>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is too short; it may have been written to.</exception>
    public int CopyTo(Span<char> destination)
    {
        var written = 0;
        for (var n = _start; !n.IsNull; n = n.Right.AsItem)
        {
            if (!n.Deleted && n.Countable && n.Content is ContentString cs)
            {
                if (cs.Length > destination.Length - written)
                {
                    throw new ArgumentException($"The destination holds {destination.Length} characters; the text has more. Pass at least Length ({Length}).", nameof(destination));
                }

                cs.CopyTo(destination.Slice(written));
                written += cs.Length;
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
            var id = n.Id;
            var departures = Doc.Store.Departures;
            if (!n.Deleted && n.Countable && n.Content is ContentString cs)
            {
                cs.CopyTo(writer.GetSpan(cs.Length));
                writer.Advance(cs.Length);
            }

            n = Doc.Store.NextAfter(n, id, departures);
        }
    }

    public YText Clone() => InternalClone() as YText;

    /// <summary>
    /// Returns the unformatted string representation of this YText type. Mirrors Yjs' <c>toJSON()</c>
    /// (<c>toJSON() { return this.toString() }</c>).
    /// </summary>
    public override object ToJson() => ToString();

    public void RemoveAttribute(string name)
    {
        if (Doc != null)
        {
            Doc.Transact((self: this, name), static (s, tr) => s.self.TypeMapDelete(tr, s.name));
        }
        else
        {
            AddPending(new PendingOperation(PendingKind.RemoveAttribute, 0, 0, name, null, null));
        }
    }

    public void SetAttribute(string name, object value)
    {
        if (Doc != null)
        {
            Doc.Transact((self: this, name, value), static (s, tr) => s.self.TypeMapSet(tr, s.name, s.value));
        }
        else
        {
            AddPending(new PendingOperation(PendingKind.SetAttribute, 0, 0, name, value, null));
        }
    }

    public object GetAttribute(string name) => TryTypeMapGet(name, out var value) ? value : null;

    public IEnumerable<KeyValuePair<string, object>> GetAttributes() => TypeMapEnumerateValues();

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

        Doc.Transact(transaction =>
        {
            var start = _start;
            var end = _start;
            OrderedDictionary<string, object> startAttributes = null;
            OrderedDictionary<string, object> currentAttributes = null;

            while (!end.IsNull)
            {
                if (!end.Deleted)
                {
                    switch (end.Content)
                    {
                        case ContentFormat cf:
                            UpdateCurrentAttributes(ref currentAttributes, cf);
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

    /// <summary>
    /// This function is experimental and subject to change / be removed. Mirrors Yjs' exported
    /// free function <c>cleanupYTextFormatting(type)</c>; identical to the instance method
    /// <see cref="CleanupFormatting"/>, provided as a static under the Yjs-familiar name for parity.
    /// </summary>
    /// <returns>How many formatting attributes have been cleaned up.</returns>
    public static int CleanupYTextFormatting(YText type) => type.CleanupFormatting();
}
