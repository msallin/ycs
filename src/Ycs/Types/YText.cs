// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

using System.Buffers;

namespace Ycs;

/// <summary>
/// Event that describes the changes on a YText type.
/// </summary>
public class YTextEvent : YEvent
{
    private enum ChangeType
    {
        Insert,
        Delete,
        Retain
    }

    private readonly ISet<string> _subs;
    private ISet<string> _keysChanged;
    private IList<Delta> _delta = null;

    internal YTextEvent(YText arr, Transaction transaction, ISet<string> subs)
        : base(arr, transaction)
    {
        _subs = subs;
    }

    /// <summary>
    /// Gets the changed attribute names.
    /// </summary>
    public ISet<string> KeysChanged => _keysChanged ??= ChangedKeys.WithoutChildList(_subs);

    /// <summary>
    /// Gets whether the children keys changed.
    /// </summary>
    public bool ChildListChanged => _subs?.Contains(null) ?? false;

    /// <summary>
    /// Compute the changes in the delta format. Overrides <see cref="YEvent.Delta"/> — for
    /// YText this is a Quill-style attribute-aware delta, not the plain insert/retain/delete
    /// shape used by <see cref="YEvent.Changes"/>.Delta, exactly as Yjs does.
    /// </summary>
    public override IList<Delta> Delta
    {
        get
        {
            if (_delta == null)
            {
                // From the transaction, so that an ended one throws before anything is started.
                var doc = Transaction.Doc;
                _delta = new List<Delta>();

                doc.Transact(transaction =>
                {
                    var delta = _delta;

                    // Saves all current attributes for insert.
                    var currentAttributes = new Dictionary<string, object>();
                    var oldAttributes = new Dictionary<string, object>();
                    var item = Target._start;
                    ChangeType? action = null;
                    var attributes = new Dictionary<string, object>();

                    object insert = string.Empty;
                    int retain = 0;
                    int deleteLen = 0;

                    void addOp()
                    {
                        if (action != null)
                        {
                            Delta op;

                            switch (action)
                            {
                                case ChangeType.Delete:
                                    op = new Delta { Delete = deleteLen };
                                    deleteLen = 0;
                                    break;
                                case ChangeType.Insert:
                                    op = new Delta { Insert = insert };
                                    if (currentAttributes?.Count > 0)
                                    {
                                        op.Attributes = new Dictionary<string, object>();
                                        foreach (var kvp in currentAttributes)
                                        {
                                            if (kvp.Value != null)
                                            {
                                                op.Attributes[kvp.Key] = kvp.Value;
                                            }
                                        }
                                    }
                                    break;
                                case ChangeType.Retain:
                                    op = new Delta { Retain = retain };
                                    if (attributes.Count > 0)
                                    {
                                        op.Attributes = new Dictionary<string, object>();
                                        foreach (var kvp in attributes)
                                        {
                                            op.Attributes[kvp.Key] = attributes[kvp.Key];
                                        }
                                    }
                                    retain = 0;
                                    break;
                                default:
                                    throw new InvalidOperationException($"Unexpected action: {action}");
                            }

                            delta.Add(op);
                            action = null;
                        }
                    }

                    while (item != null)
                    {
                        switch (item.Content)
                        {
                            case ContentEmbed _:
                                if (Adds(item))
                                {
                                    if (!Deletes(item))
                                    {
                                        addOp();
                                        action = ChangeType.Insert;
                                        insert = (item.Content as ContentEmbed).Embed;
                                        addOp();
                                    }
                                }
                                else if (Deletes(item))
                                {
                                    if (action != ChangeType.Delete)
                                    {
                                        addOp();
                                        action = ChangeType.Delete;
                                    }

                                    deleteLen++;
                                }
                                else if (!item.Deleted)
                                {
                                    if (action != ChangeType.Retain)
                                    {
                                        addOp();
                                        action = ChangeType.Retain;
                                    }

                                    retain++;
                                }
                                break;
                            case ContentString _:
                                if (Adds(item))
                                {
                                    if (!Deletes(item))
                                    {
                                        if (action != ChangeType.Insert)
                                        {
                                            addOp();
                                            action = ChangeType.Insert;
                                        }

                                        insert += (item.Content as ContentString).GetString();
                                    }
                                }
                                else if (Deletes(item))
                                {
                                    if (action != ChangeType.Delete)
                                    {
                                        addOp();
                                        action = ChangeType.Delete;
                                    }

                                    deleteLen += item.Length;
                                }
                                else if (!item.Deleted)
                                {
                                    if (action != ChangeType.Retain)
                                    {
                                        addOp();
                                        action = ChangeType.Retain;
                                    }

                                    retain += item.Length;
                                }
                                break;
                            case ContentFormat cf:
                                if (Adds(item))
                                {
                                    if (!Deletes(item))
                                    {
                                        if (!currentAttributes.TryGetValue(cf.Key, out var curVal))
                                        {
                                            curVal = null;
                                        }

                                        if (!YText.EqualAttrs(curVal, cf.Value))
                                        {
                                            if (action == ChangeType.Retain)
                                            {
                                                addOp();
                                            }

                                            if (!oldAttributes.TryGetValue(cf.Key, out var oldVal))
                                            {
                                                oldVal = null;
                                            }

                                            if (YText.EqualAttrs(cf.Value, oldVal))
                                            {
                                                attributes.Remove(cf.Key);
                                            }
                                            else
                                            {
                                                attributes[cf.Key] = cf.Value;
                                            }
                                        }
                                        else
                                        {
                                            item.Delete(Transaction);
                                        }
                                    }
                                }
                                else if (Deletes(item))
                                {
                                    oldAttributes[cf.Key] = cf.Value;

                                    if (!currentAttributes.TryGetValue(cf.Key, out var curVal))
                                    {
                                        curVal = null;
                                    }

                                    if (!YText.EqualAttrs(curVal, cf.Value))
                                    {
                                        if (action == ChangeType.Retain)
                                        {
                                            addOp();
                                        }

                                        attributes[cf.Key] = curVal;
                                    }
                                }
                                else if (!item.Deleted)
                                {
                                    oldAttributes[cf.Key] = cf.Value;

                                    if (attributes.TryGetValue(cf.Key, out var attr))
                                    {
                                        if (!YText.EqualAttrs(attr, cf.Value))
                                        {
                                            if (action == ChangeType.Retain)
                                            {
                                                addOp();
                                            }

                                            if (cf.Value == null)
                                            {
                                                attributes[cf.Key] = null;
                                            }
                                            else
                                            {
                                                attributes.Remove(cf.Key);
                                            }
                                        }
                                        else
                                        {
                                            item.Delete(Transaction);
                                        }
                                    }
                                }

                                if (!item.Deleted)
                                {
                                    if (action == ChangeType.Insert)
                                    {
                                        addOp();
                                    }

                                    YText.UpdateCurrentAttributes(currentAttributes, item.Content as ContentFormat);
                                }
                                break;
                        }

                        item = item.Right as Item;
                    }

                    addOp();

                    while (delta.Count > 0)
                    {
                        var lastOp = delta[delta.Count - 1];
                        if (lastOp.Retain != null && lastOp.Attributes == null)
                        {
                            // Drop trailing retains that don't assign attributes; a retain that
                            // *does* carry attributes is a real formatting op and must survive.
                            delta.RemoveAt(delta.Count - 1);
                        }
                        else
                        {
                            break;
                        }
                    }
                });
            }

            return _delta;
        }
    }
}

/// <summary>
/// Type that represents text with formatting information.
/// </summary>
public partial class YText : YArrayBase
{
    /// <summary>
    /// A position in the item list while text is inserted, deleted or formatted. A struct, passed by
    /// reference; <see cref="CurrentAttributes"/> is null until a format is passed.
    /// </summary>
    private struct ItemTextListPosition
    {
        public Item Left;
        public Item Right;
        public int Index;
        public Dictionary<string, object> CurrentAttributes;

        public ItemTextListPosition(Item left, Item right, int index)
        {
            Left = left;
            Right = right;
            Index = index;
            CurrentAttributes = null;
        }

        public void Forward()
        {
            if (Right == null)
            {
                throw new Exception("Unexpected");
            }

            switch (Right.Content)
            {
                default:
                    if (!Right.Deleted)
                    {
                        Index += Right.Length;
                    }
                    break;
                case ContentFormat cf:
                    if (!Right.Deleted)
                    {
                        UpdateCurrentAttributes(ref CurrentAttributes, cf);
                    }
                    break;
            }

            Left = Right;
            Right = Right.Right as Item;
        }

        public void FindNextPosition(Transaction transaction, int count)
        {
            while (Right != null && count > 0)
            {
                switch (Right.Content)
                {
                    default:
                        if (!Right.Deleted)
                        {
                            if (count < Right.Length)
                            {
                                // Split right.
                                transaction.Doc.Store.GetItemCleanStart(transaction, new ID(Right.Id.Client, Right.Id.Clock + count));
                            }

                            Index += Right.Length;
                            count -= Right.Length;
                        }
                        break;
                    case ContentFormat cf:
                        if (!Right.Deleted)
                        {
                            UpdateCurrentAttributes(ref CurrentAttributes, cf);
                        }
                        break;
                }

                Left = Right;
                Right = Right.Right as Item;
                // We don't forward() because that would halve the performance because we already do the checks above.
            }
        }

        /// <summary>
        /// Negate applied formats.
        /// </summary>
        public void InsertNegatedAttributes(Transaction transaction, AbstractType parent, Dictionary<string, object> negatedAttributes)
        {
            // Check if we really need to remove attributes.
            while (
                Right != null && (
                    Right.Deleted || (
                        Right.Content is ContentFormat cf &&
                        negatedAttributes != null &&
                        negatedAttributes.TryGetValue(cf.Key, out var negated) &&
                        EqualAttrs(negated, cf.Value)
                    )
                )
            )
            {
                if (!Right.Deleted)
                {
                    negatedAttributes.Remove((Right.Content as ContentFormat).Key);
                }

                Forward();
            }

            if (negatedAttributes == null)
            {
                return;
            }

            var doc = transaction.Doc;
            var ownClientId = doc.ClientId;
            var left = Left;
            var right = Right;

            foreach (var kvp in negatedAttributes)
            {
                left = new Item(new ID(ownClientId, doc.Store.GetState(ownClientId)), left, left?.LastId, right, right?.Id, parent, null, new ContentFormat(kvp.Key, kvp.Value));
                left.Integrate(transaction, 0);

                (CurrentAttributes ??= new Dictionary<string, object>())[kvp.Key] = kvp.Value;
                UpdateCurrentAttributes(ref CurrentAttributes, left.Content as ContentFormat);
            }
        }

        public void MinimizeAttributeChanges(IDictionary<string, object> attributes)
        {
            // Go right while attributes[right.Key] == right.Value (or right is deleted).
            while (Right != null)
            {
                if (Right.Deleted || (Right.Content is ContentFormat cf && EqualAttrs(attributes != null && attributes.TryGetValue(cf.Key, out var val) ? val : null, cf.Value)))
                {
                    Forward();
                }
                else
                {
                    break;
                }
            }
        }
    }

    public enum YTextChangeType
    {
        Added,
        Removed
    }

    public class YTextChangeAttributes
    {
        public YTextChangeType Type { get; set; }
        public int User { get; set; }
        public YTextChangeType State { get; set; }
    }

    // TODO: [alekseyk] To util class? Might not be needed here.
    internal const int YTextRefId = 2;
    internal const string ChangeKey = "ychange";

    private enum PendingKind
    {
        Insert,
        InsertEmbed,
        Delete,
        Format,
        ApplyDelta,
        SetAttribute,
        RemoveAttribute,
    }

    /// <summary>A change made before the text is in a document, replayed when it is integrated.</summary>
    private readonly record struct PendingOperation(PendingKind Kind, int Index, int Length, string Name, object Value, IDictionary<string, object> Attributes);

    private List<PendingOperation> _pending;

    // The last result of ToString, valid while the store's version and Length are as they were then.
    private string _string;
    private long _stringVersion;
    private int _stringLength;

    /// <summary>
    /// Whether a formatting attribute was ever integrated into this text (Yjs' <c>_hasFormatting</c>).
    /// Set by <see cref="ContentFormat"/>; it is never reset.
    /// </summary>
    internal bool HasFormatting { get; set; }

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

    private void AddPending(PendingOperation operation)
    {
        (_pending ??= new List<PendingOperation>()).Add(operation);
    }

    public void ApplyDelta(IList<Delta> delta, bool sanitize = true)
    {
        if (Doc != null)
        {
            Doc.Transact(tr =>
            {
                var curPos = new ItemTextListPosition(null, _start, 0);
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
                        var ins = (!sanitize && insertStr != null && i == delta.Count - 1 && curPos.Right == null && insertStr.EndsWith("\n")) ? insertStr.Substring(0, insertStr.Length - 1) : op.Insert;
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
            AddPending(new PendingOperation(PendingKind.ApplyDelta, 0, sanitize ? 1 : 0, null, delta, null));
        }
    }

    public IList<Delta> ToDelta(Snapshot snapshot = null, Snapshot prevSnapshot = null, Func<YTextChangeType, ID, YTextChangeAttributes> computeYChange = null)
    {
        var ops = new List<Delta>();
        var currentAttributes = new Dictionary<string, object>();
        var doc = Doc;
        var str = string.Empty;

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

                var op = new Delta { Insert = str };
                if (addAttributes)
                {
                    op.Attributes = attributes;
                }

                ops.Add(op);
                str = string.Empty;
            }
        }

        // Snapshots are merged again after the transaction, so we need to keep the
        // transaction alive until we are done.
        doc.Transact(tr =>
        {
            if (snapshot != null)
            {
                Transaction.SplitSnapshotAffectedStructs(tr, snapshot);
            }

            if (prevSnapshot != null)
            {
                Transaction.SplitSnapshotAffectedStructs(tr, prevSnapshot);
            }

            while (n != null)
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
                                if (cur == null || cur.User != n.Id.Client || cur.State != YTextChangeType.Removed)
                                {
                                    packStr();
                                    currentAttributes[ChangeKey] = computeYChange != null ? computeYChange(YTextChangeType.Removed, n.Id) : new YTextChangeAttributes { Type = YTextChangeType.Removed };
                                }
                            }
                            else if (prevSnapshot != null && !n.IsVisible(prevSnapshot))
                            {
                                if (cur == null || cur.User != n.Id.Client || cur.State != YTextChangeType.Added)
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

                            str += cs.GetString();
                            break;
                        case ContentEmbed ce:
                            packStr();
                            var op = new Delta { Insert = ce.Embed };
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

                n = n.Right as Item;
            }

            packStr();
        }, "splitSnapshotAffectedStructs");

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
            // TODO: Transact<TState>((this, index, text, attributes), static (tr, s) => s.Item1.InsertInTransaction(tr, s.index, s.text, s.attributes)) avoids this closure.
            doc.Transact(tr => InsertInTransaction(tr, index, text, attributes));
        }
        else
        {
            AddPending(new PendingOperation(PendingKind.Insert, index, 0, null, text, attributes));
        }
    }

    private void InsertInTransaction(Transaction transaction, int index, string text, IDictionary<string, object> attributes)
    {
        var pos = FindPosition(transaction, index, useSearchMarker: attributes == null);
        if (attributes == null && pos.CurrentAttributes?.Count > 0)
        {
            // Without attributes, the text takes the attributes at its position.
            attributes = new Dictionary<string, object>(pos.CurrentAttributes);
        }

        InsertText(transaction, ref pos, text, attributes);
    }

    public void InsertEmbed(int index, object embed, IDictionary<string, object> attributes = null)
    {
        attributes = attributes ?? new Dictionary<string, object>();

        if (Doc != null)
        {
            Doc.Transact(tr =>
            {
                var pos = FindPosition(tr, index, useSearchMarker: false);
                InsertText(tr, ref pos, embed, attributes);
            });
        }
        else
        {
            AddPending(new PendingOperation(PendingKind.InsertEmbed, index, 0, null, embed, attributes));
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
            // TODO: Transact<TState>((this, index, length), static (tr, s) => s.Item1.DeleteInTransaction(tr, s.index, s.length)) avoids this closure.
            Doc.Transact(tr => DeleteInTransaction(tr, index, length));
        }
        else
        {
            AddPending(new PendingOperation(PendingKind.Delete, index, length, null, null, null));
        }
    }

    private void DeleteInTransaction(Transaction transaction, int index, int length)
    {
        var pos = FindPosition(transaction, index, useSearchMarker: true);
        DeleteText(transaction, ref pos, length);
    }

    public void Format(int index, int length, IDictionary<string, object> attributes)
    {
        if (length == 0)
        {
            return;
        }

        if (Doc != null)
        {
            Doc.Transact(tr =>
            {
                var pos = FindPosition(tr, index, useSearchMarker: false);
                if (pos.Right == null)
                {
                    return;
                }

                FormatText(tr, ref pos, length, attributes);
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

        List<Item> remove = null;
        for (var n = _start; n != null; n = n.Right as Item)
        {
            if (!n.Deleted && (n.Content is ContentFormat || (n.Countable && n.Content is not ContentString)))
            {
                (remove ??= new List<Item>()).Add(n);
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
        for (var n = _start; n != null; n = n.Right as Item)
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
        for (var n = _start; n != null; n = n.Right as Item)
        {
            if (!n.Deleted && n.Countable && n.Content is ContentString cs)
            {
                cs.CopyTo(writer.GetSpan(cs.Length));
                writer.Advance(cs.Length);
            }
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
            Doc.Transact(tr =>
            {
                TypeMapDelete(tr, name);
            });
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
            Doc.Transact(tr =>
            {
                TypeMapSet(tr, name, value);
            });
        }
        else
        {
            AddPending(new PendingOperation(PendingKind.SetAttribute, 0, 0, name, value, null));
        }
    }

    public object GetAttribute(string name) => TryTypeMapGet(name, out var value) ? value : null;

    public IEnumerable<KeyValuePair<string, object>> GetAttributes() => TypeMapEnumerateValues();

    internal override void Integrate(YDoc doc, Item item)
    {
        base.Integrate(doc, item);

        var pending = _pending;
        _pending = null;
        if (pending == null)
        {
            return;
        }

        foreach (var op in pending)
        {
            switch (op.Kind)
            {
                case PendingKind.Insert:
                    Insert(op.Index, (string)op.Value, op.Attributes);
                    break;
                case PendingKind.InsertEmbed:
                    InsertEmbed(op.Index, op.Value, op.Attributes);
                    break;
                case PendingKind.Delete:
                    Delete(op.Index, op.Length);
                    break;
                case PendingKind.Format:
                    Format(op.Index, op.Length, op.Attributes);
                    break;
                case PendingKind.ApplyDelta:
                    ApplyDelta((IList<Delta>)op.Value, sanitize: op.Length != 0);
                    break;
                case PendingKind.SetAttribute:
                    SetAttribute(op.Name, op.Value);
                    break;
                case PendingKind.RemoveAttribute:
                    RemoveAttribute(op.Name);
                    break;
            }
        }
    }

    internal override AbstractType InternalClone()
    {
        var text = new YText();
        text.ApplyDelta(ToDelta());
        return text;
    }

    internal override void CallObserver(Transaction transaction, ISet<string> parentSubs)
    {
        base.CallObserver(transaction, parentSubs);

        var doc = transaction.Doc;

        // If a remote change happened, we try to cleanup potential formatting duplicates. As in
        // Yjs 13.6, only a text that has held formatting can have any; the others skip the second
        // transaction this needs.
        if (!transaction.Local && HasFormatting)
        {
            // Check if another formatting item was inserted.
            var foundFormattingItem = false;
            foreach (var kvp in transaction.AfterState)
            {
                var client = kvp.Key;
                var afterClock = kvp.Value;

                if (!transaction.BeforeState.TryGetValue(kvp.Key, out var clock))
                {
                    clock = 0;
                }

                if (afterClock == clock)
                {
                    continue;
                }

                transaction.Doc.Store.IterateStructs(transaction, doc.Store.Clients[client], clock, afterClock, item =>
                {
                    if (item is Item it && !it.Deleted && it.Content is ContentFormat)
                    {
                        foundFormattingItem = true;

                        // Stop loop.
                        return false;
                    }

                    return true;
                });

                if (foundFormattingItem)
                {
                    break;
                }
            }

            if (!foundFormattingItem)
            {
                transaction.DeleteSet.IterateDeletedStructs(transaction, item =>
                {
                    var it = item as Item;
                    if (it != null && it.Parent == this && it.Content is ContentFormat)
                    {
                        foundFormattingItem = true;

                        // Don't iterate further.
                        return false;
                    }

                    return true;
                });
            }

            doc.Transact(tr =>
            {
                if (foundFormattingItem)
                {
                    // If a formatting item was inserted, we simply clean the whole type.
                    // We need to compuyte currentAttributes for the current position anyway.
                    CleanupFormatting();
                }
                else
                {
                    // If no formatting attribute was inserted, we can make due with contextless formatting cleanups.
                    // Contextless: it is not necessary to compute currentAttributes for the affected position.
                    tr.DeleteSet.IterateDeletedStructs(tr, item =>
                    {
                        var it = item as Item;
                        if (it != null && it.Parent == this)
                        {
                            CleanupContextlessFormattingGap(tr, it);
                        }

                        return true;
                    });
                }
            });
        }

        if (IsObserved(transaction))
        {
            CallTypeObservers(transaction, new YTextEvent(this, transaction, parentSubs));
        }
    }

    private ItemTextListPosition FindPosition(Transaction transaction, int index, bool useSearchMarker)
    {
        var marker = useSearchMarker ? FindMarker(index) : null;

        if (marker != null)
        {
            var pos = new ItemTextListPosition(marker.P.Left as Item, marker.P, marker.Index);
            pos.FindNextPosition(transaction, index - marker.Index);
            return pos;
        }
        else
        {
            var pos = new ItemTextListPosition(null, _start, 0);
            pos.FindNextPosition(transaction, index);
            return pos;
        }
    }

    /// <returns>The attributes to negate after the content (null for none).</returns>
    private Dictionary<string, object> InsertAttributes(Transaction transaction, ref ItemTextListPosition currPos, IDictionary<string, object> attributes)
    {
        if (attributes == null)
        {
            return null;
        }

        var doc = transaction.Doc;
        var ownClientId = doc.ClientId;
        Dictionary<string, object> negatedAttributes = null;

        // Insert format-start items.
        foreach (var kvp in attributes)
        {
            var key = kvp.Key;
            var value = kvp.Value;
            var currentVal = GetAttribute(currPos.CurrentAttributes, key);

            if (!EqualAttrs(currentVal, value))
            {
                // Save negated attribute (set null if currentVal is not set).
                (negatedAttributes ??= new Dictionary<string, object>())[key] = currentVal;

                currPos.Right = new Item(new ID(ownClientId, doc.Store.GetState(ownClientId)), currPos.Left, currPos.Left?.LastId, currPos.Right, currPos.Right?.Id, this, null, new ContentFormat(key, value));
                currPos.Right.Integrate(transaction, 0);
                currPos.Forward();
            }
        }

        return negatedAttributes;
    }

    /// <param name="attributes">The attributes of the text; null for none. Attributes at the position that are not in them are set to null in them, as in Yjs.</param>
    private void InsertText(Transaction transaction, ref ItemTextListPosition currPos, object text, IDictionary<string, object> attributes)
    {
        if (currPos.CurrentAttributes?.Count > 0)
        {
            attributes ??= new Dictionary<string, object>();
            foreach (var kvp in currPos.CurrentAttributes)
            {
                attributes.TryAdd(kvp.Key, null);
            }
        }

        var doc = transaction.Doc;
        var ownClientId = doc.ClientId;

        currPos.MinimizeAttributeChanges(attributes);
        var negatedAttributes = InsertAttributes(transaction, ref currPos, attributes);

        // Insert content.
        var content = text is string s ? (IContent)new ContentString(s) : new ContentEmbed(text);

        if (_searchMarkers?.Count > 0)
        {
            _searchMarkers.UpdateMarkerChanges(currPos.Index, content.Length);
        }

        currPos.Right = new Item(new ID(ownClientId, doc.Store.GetState(ownClientId)), currPos.Left, currPos.Left?.LastId, currPos.Right, currPos.Right?.Id, this, null, content);
        currPos.Right.Integrate(transaction, 0);
        currPos.Forward();

        currPos.InsertNegatedAttributes(transaction, this, negatedAttributes);
    }

    private void FormatText(Transaction transaction, ref ItemTextListPosition curPos, int length, IDictionary<string, object> attributes)
    {
        var doc = transaction.Doc;
        var ownClientId = doc.ClientId;

        curPos.MinimizeAttributeChanges(attributes);
        var negatedAttributes = InsertAttributes(transaction, ref curPos, attributes) ?? new Dictionary<string, object>();

        // Iterate until first non-format or null is found.
        // Delete all formats with attributes[format.Key] != null
        while (length > 0 && curPos.Right != null)
        {
            if (!curPos.Right.Deleted)
            {
                switch (curPos.Right.Content)
                {
                    case ContentFormat cf:
                        if (attributes.TryGetValue(cf.Key, out var attr))
                        {
                            if (EqualAttrs(attr, cf.Value))
                            {
                                negatedAttributes.Remove(cf.Key);
                            }
                            else
                            {
                                negatedAttributes[cf.Key] = cf.Value;
                            }

                            curPos.Right.Delete(transaction);
                        }
                        break;
                    default:
                        if (length < curPos.Right.Length)
                        {
                            transaction.Doc.Store.GetItemCleanStart(transaction, new ID(curPos.Right.Id.Client, curPos.Right.Id.Clock + length));
                        }
                        length -= curPos.Right.Length;
                        break;
                }
            }

            curPos.Forward();
        }

        // Quill just assumes that the editor starts with a newline and that it always
        // ends with a newline. We only insert that newline when a new newline is
        // inserted - i.e. when length is bigger than type.length.
        if (length > 0)
        {
            var newLines = new string('\n', length - 1);
            curPos.Right = new Item(new ID(ownClientId, doc.Store.GetState(ownClientId)), curPos.Left, curPos.Left?.LastId, curPos.Right, curPos.Right?.Id, this, null, new ContentString(newLines));
            curPos.Right.Integrate(transaction, 0);
            curPos.Forward();
        }

        curPos.InsertNegatedAttributes(transaction, this, negatedAttributes);
    }

    /// <summary>
    /// Call this function after string content has been deleted in order to clean up formatting Items.
    /// </summary>
    /// <param name="startAttributes">The attributes at <paramref name="start"/>; null for none.</param>
    /// <param name="endAttributes">The attributes at <paramref name="end"/>, updated while it moves on; null for none.</param>
    private static int CleanupFormattingGap(Transaction transaction, Item start, Item end, Dictionary<string, object> startAttributes, Dictionary<string, object> endAttributes)
    {
        while (end != null && !(end.Content is ContentString) && !(end.Content is ContentEmbed))
        {
            if (!end.Deleted && end.Content is ContentFormat cf)
            {
                UpdateCurrentAttributes(ref endAttributes, cf);
            }

            end = end.Right as Item;
        }

        int cleanups = 0;
        while (start != end)
        {
            if (!start.Deleted)
            {
                var content = start.Content;
                switch (content)
                {
                    case ContentFormat cf:
                        var endVal = GetAttribute(endAttributes, cf.Key);
                        var startVal = GetAttribute(startAttributes, cf.Key);

                        if ((endVal != cf.Value || !(endVal?.Equals(cf.Value) ?? false) ||
                            (startVal == cf.Value || (startVal?.Equals(cf.Value) ?? false))))
                        {
                            // Either this format is overwritten or it is not necessary because the attribute already existed.
                            start.Delete(transaction);
                            cleanups++;
                        }

                        break;
                }
            }

            start = start.Right as Item;
        }

        return cleanups;
    }

    private void CleanupContextlessFormattingGap(Transaction transaction, Item item)
    {
        // Iterate until item.Right is null or content.
        while (item != null && item.Right != null && (item.Right.Deleted || (
            !((item.Right as Item).Content is ContentString) && !((item.Right as Item).Content is ContentEmbed)
            )))
        {
            item = item.Right as Item;
        }

        var attrs = new HashSet<object>();

        // Iterate back until a content item is found.
        while (item != null && (item.Deleted || (
            !(item.Content is ContentString) && !(item.Content is ContentEmbed)
            )))
        {
            if (!item.Deleted && item.Content is ContentFormat cf)
            {
                var key = cf.Key;
                if (attrs.Contains(key))
                {
                    item.Delete(transaction);
                }
                else
                {
                    attrs.Add(key);
                }
            }

            item = item.Left as Item;
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
        int res = 0;

        Doc.Transact(transaction =>
        {
            var start = _start;
            var end = _start;
            var startAttributes = new Dictionary<string, object>();
            var currentAttributes = new Dictionary<string, object>();

            while (end != null)
            {
                if (!end.Deleted)
                {
                    switch (end.Content)
                    {
                        case ContentFormat cf:
                            UpdateCurrentAttributes(currentAttributes, cf);
                            break;
                        default:
                            res += CleanupFormattingGap(transaction, start, end, startAttributes, currentAttributes);
                            startAttributes = new Dictionary<string, object>(currentAttributes);
                            start = end;
                            break;
                    }
                }

                end = end.Right as Item;
            }
        });

        return res;
    }

    private static void DeleteText(Transaction transaction, ref ItemTextListPosition curPos, int length)
    {
        var startLength = length;
        var startAttrs = Copy(curPos.CurrentAttributes);
        var start = curPos.Right;

        while (length > 0 && curPos.Right != null)
        {
            if (!curPos.Right.Deleted)
            {
                switch (curPos.Right.Content)
                {
                    case ContentType _:
                    case ContentEmbed _:
                    case ContentString _:
                        if (length < curPos.Right.Length)
                        {
                            transaction.Doc.Store.GetItemCleanStart(transaction, new ID(curPos.Right.Id.Client, curPos.Right.Id.Clock + length));
                        }
                        length -= curPos.Right.Length;
                        curPos.Right.Delete(transaction);
                        break;
                }
            }

            curPos.Forward();
        }

        if (start != null)
        {
            CleanupFormattingGap(transaction, start, curPos.Right, startAttrs, Copy(curPos.CurrentAttributes));
        }

        var parent = (curPos.Left ?? curPos.Right).Parent as YText;
        if (parent?._searchMarkers?.Count > 0)
        {
            parent._searchMarkers.UpdateMarkerChanges(curPos.Index, -startLength + length);
        }
    }

    private static Dictionary<string, object> Copy(Dictionary<string, object> attributes) =>
        attributes?.Count > 0 ? new Dictionary<string, object>(attributes) : null;

    /// <summary>The value of <paramref name="key"/> in <paramref name="attributes"/> (null for none), or null.</summary>
    private static object GetAttribute(Dictionary<string, object> attributes, string key) =>
        attributes != null && attributes.TryGetValue(key, out var value) ? value : null;

    internal override void Write(IUpdateEncoder encoder)
    {
        encoder.WriteTypeRef(YTextRefId);
    }

    internal static YText Read(IUpdateDecoder decoder)
    {
        return new YText();
    }

    /// <summary>
    /// This function is experimental and subject to change / be removed. Mirrors Yjs' exported
    /// free function <c>cleanupYTextFormatting(type)</c>; identical to the instance method
    /// <see cref="CleanupFormatting"/>, provided as a static under the Yjs-familiar name for parity.
    /// </summary>
    /// <returns>How many formatting attributes have been cleaned up.</returns>
    public static int CleanupYTextFormatting(YText type) => type.CleanupFormatting();

    internal static bool EqualAttrs(object attr1, object attr2)
    {
        return ReferenceEquals(attr1, attr2) || (attr1?.Equals(attr2) ?? false);
    }

    /// <summary>Applies <paramref name="format"/> to <paramref name="attributes"/>, which is created when a value is set.</summary>
    private static void UpdateCurrentAttributes(ref Dictionary<string, object> attributes, ContentFormat format)
    {
        if (format.Value == null)
        {
            attributes?.Remove(format.Key);
        }
        else
        {
            (attributes ??= new Dictionary<string, object>())[format.Key] = format.Value;
        }
    }

    internal static void UpdateCurrentAttributes(IDictionary<string, object> attributes, ContentFormat format)
    {
        if (format.Value == null)
        {
            attributes.Remove(format.Key);
        }
        else
        {
            attributes[format.Key] = format.Value;
        }
    }
}
