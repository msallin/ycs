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
    private ChangesCollection _changes;

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
    /// Yjs' <c>YTextEvent#changes</c>: the <see cref="Keys"/> and the text <see cref="Delta"/>; no
    /// added or deleted items.
    /// </summary>
    public override ChangesCollection Changes => _changes ??= new ChangesCollection
    {
        Keys = Keys,
        Delta = Delta,
        Added = new HashSet<Item>(),
        Deleted = new HashSet<Item>(),
    };

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
                            Delta op = null;

                            switch (action)
                            {
                                case ChangeType.Delete:
                                    if (deleteLen > 0)
                                    {
                                        op = new Delta { Delete = deleteLen };
                                    }

                                    deleteLen = 0;
                                    break;
                                case ChangeType.Insert:
                                    if (insert is not string pending || pending.Length > 0)
                                    {
                                        op = new Delta { Insert = insert };
                                        if (currentAttributes.Count > 0)
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
                                    }

                                    insert = string.Empty;
                                    break;
                                case ChangeType.Retain:
                                    if (retain > 0)
                                    {
                                        op = new Delta { Retain = retain };
                                        if (attributes.Count > 0)
                                        {
                                            op.Attributes = new Dictionary<string, object>(attributes);
                                        }
                                    }

                                    retain = 0;
                                    break;
                            }

                            if (op != null)
                            {
                                delta.Add(op);
                            }

                            action = null;
                        }
                    }

                    while (item != null)
                    {
                        switch (item.Content)
                        {
                            case ContentType:
                            case ContentEmbed:
                                if (Adds(item))
                                {
                                    if (!Deletes(item))
                                    {
                                        addOp();
                                        action = ChangeType.Insert;
                                        insert = YText.EmbedOf(item.Content);
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
                                        else if (cf.Value != null)
                                        {
                                            // In the delta's own transaction, not the event's: its observers are running.
                                            item.Delete(transaction);
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
                                                attributes.Remove(cf.Key);
                                            }
                                            else
                                            {
                                                attributes[cf.Key] = cf.Value;
                                            }
                                        }
                                        else if (attr != null)
                                        {
                                            // A repeated null is left to the contextless cleanup, as in Yjs.
                                            item.Delete(transaction);
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
/// <remarks>
/// Changes made before the text is in a document are replayed when it is added to one; as in Yjs, a
/// change that fails then ends the replay, and the changes after it are dropped.
/// </remarks>
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
        public OrderedDictionary<string, object> CurrentAttributes;

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
        public void InsertNegatedAttributes(Transaction transaction, AbstractType parent, OrderedDictionary<string, object> negatedAttributes)
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

            foreach (var kvp in negatedAttributes)
            {
                var left = Left;
                var right = Right;
                var nextFormat = new Item(new ID(ownClientId, doc.Store.GetState(ownClientId)), left, left?.LastId, right, right?.Id, parent, null, new ContentFormat(kvp.Key, kvp.Value));
                nextFormat.Integrate(transaction, 0);
                Right = nextFormat;
                Forward();
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

    /// <summary>The <c>ychange</c> attribute of <see cref="ToDelta"/>: whether, and by whom, the text was added or removed.</summary>
    public class YTextChangeAttributes
    {
        public YTextChangeType Type { get; set; }

        /// <summary>The client id of the change; null when <c>computeYChange</c> does not set it (as the default does not).</summary>
        public long? User { get; set; }
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

        void computeDelta()
        {
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

                            str += cs.GetString();
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

                n = n.Right as Item;
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
            Doc.Transact((self: this, index, length, attributes), static (s, tr) =>
            {
                var pos = s.self.FindPosition(tr, s.index, useSearchMarker: false);
                if (pos.Right == null)
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

    internal override void Integrate(YDoc doc, Item item)
    {
        base.Integrate(doc, item);

        var pending = _pending;
        _pending = null;
        if (pending == null)
        {
            return;
        }

        // As Yjs: an operation that throws ends the replay, and the operations after it are dropped.
        try
        {
            ReplayPending(pending);
        }
        catch (Exception)
        {
            // Yjs logs it to the console; the integration of the text goes on.
        }
    }

    private void ReplayPending(List<PendingOperation> pending)
    {
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

    internal override AbstractType InternalCopy() => new YText();

    internal override AbstractType InternalClone()
    {
        var text = new YText();
        text.ApplyDelta(ToDelta());
        return text;
    }

    internal override void CallObserver(Transaction transaction, ISet<string> parentSubs)
    {
        base.CallObserver(transaction, parentSubs);

        if (IsObserved(transaction))
        {
            CallTypeObservers(transaction, new YTextEvent(this, transaction, parentSubs));
        }

        // If a remote change happened, potential formatting duplicates are cleaned up once the
        // transaction's observers and AfterTransaction handlers ran (CleanupAfterTransaction). As in
        // Yjs 13.6, only a text that has held formatting can have any.
        if (!transaction.Local && HasFormatting)
        {
            transaction._needFormattingCleanup = true;
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
    private OrderedDictionary<string, object> InsertAttributes(Transaction transaction, ref ItemTextListPosition currPos, IDictionary<string, object> attributes)
    {
        if (attributes == null)
        {
            return null;
        }

        var doc = transaction.Doc;
        var ownClientId = doc.ClientId;
        OrderedDictionary<string, object> negatedAttributes = null;

        // Insert format-start items.
        foreach (var kvp in InJavaScriptKeyOrder(attributes))
        {
            var key = kvp.Key;
            var value = kvp.Value;
            var currentVal = GetAttribute(currPos.CurrentAttributes, key);

            if (!EqualAttrs(currentVal, value))
            {
                // Save negated attribute (set null if currentVal is not set).
                (negatedAttributes ??= new OrderedDictionary<string, object>())[key] = currentVal;

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
        if (text is AbstractType)
        {
            CheckCanNest();
        }

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
        var content = text switch
        {
            string s => (IContent)new ContentString(s),
            AbstractType type => new ContentType(type),
            _ => new ContentEmbed(text),
        };

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
        var negatedAttributes = InsertAttributes(transaction, ref curPos, attributes) ?? new OrderedDictionary<string, object>();

        // Iterate until the first non-format or null is found, and delete all formats with
        // attributes[format.Key] != null. Also check the formats after the formatted range, so that
        // no redundant negated attributes are inserted there.
        while (curPos.Right != null &&
            (length > 0 || (negatedAttributes.Count > 0 && (curPos.Right.Deleted || curPos.Right.Content is ContentFormat))))
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
                                if (length == 0)
                                {
                                    // No need to extend negatedAttributes further.
                                    goto InsertNegated;
                                }

                                negatedAttributes[cf.Key] = cf.Value;
                            }

                            curPos.Right.Delete(transaction);
                        }
                        else
                        {
                            (curPos.CurrentAttributes ??= new OrderedDictionary<string, object>())[cf.Key] = cf.Value;
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
            curPos.Right = new Item(new ID(ownClientId, doc.Store.GetState(ownClientId)), curPos.Left, curPos.Left?.LastId, curPos.Right, curPos.Right?.Id, this, null, new ContentString(new string('\n', length)));
            curPos.Right.Integrate(transaction, 0);
            curPos.Forward();
        }

    InsertNegated:
        curPos.InsertNegatedAttributes(transaction, this, negatedAttributes);
    }

    /// <summary>
    /// Call this function after string content has been deleted in order to clean up formatting
    /// items (Yjs 13.6's <c>cleanupFormattingGap</c>): between <paramref name="start"/> and the next
    /// countable item that is not deleted, a format is deleted when a later one in the gap sets the
    /// same key, or when the attribute already had its value at <paramref name="start"/>.
    /// </summary>
    /// <param name="curr">Exclusive end of the part of the gap before the position; <paramref name="currAttributes"/> is corrected for what is deleted there.</param>
    /// <param name="startAttributes">The attributes at <paramref name="start"/>; null for none.</param>
    /// <param name="currAttributes">The attributes at <paramref name="curr"/>; null for none.</param>
    /// <returns>The number of formatting items deleted.</returns>
    private static int CleanupFormattingGap(Transaction transaction, Item start, Item curr, OrderedDictionary<string, object> startAttributes, ref OrderedDictionary<string, object> currAttributes)
    {
        var end = start;
        Dictionary<string, ContentFormat> endFormats = null;
        while (end != null && (!end.Countable || end.Deleted))
        {
            if (!end.Deleted && end.Content is ContentFormat format)
            {
                (endFormats ??= new Dictionary<string, ContentFormat>())[format.Key] = format;
            }

            end = end.Right as Item;
        }

        var cleanups = 0;
        var reachedCurr = false;
        while (start != end)
        {
            if (curr == start)
            {
                reachedCurr = true;
            }

            if (!start.Deleted && start.Content is ContentFormat cf)
            {
                var key = cf.Key;
                var value = cf.Value;
                var startAttrValue = GetAttribute(startAttributes, key);
                if (endFormats == null || !endFormats.TryGetValue(key, out var endFormat) || endFormat != cf || StrictEquals(startAttrValue, value))
                {
                    // Either this format is overwritten or it is not necessary because the attribute already existed.
                    start.Delete(transaction);
                    cleanups++;
                    if (!reachedCurr && StrictEquals(GetAttribute(currAttributes, key), value) && !StrictEquals(startAttrValue, value))
                    {
                        if (startAttrValue == null)
                        {
                            currAttributes?.Remove(key);
                        }
                        else
                        {
                            (currAttributes ??= new OrderedDictionary<string, object>())[key] = startAttrValue;
                        }
                    }
                }

                if (!reachedCurr && !start.Deleted)
                {
                    UpdateCurrentAttributes(ref currAttributes, cf);
                }
            }

            start = start.Right as Item;
        }

        return cleanups;
    }

    /// <summary>
    /// Yjs 13.6's <c>cleanupContextlessFormattingGap</c>: in the run of deleted and uncountable items
    /// around <paramref name="item"/>, deletes every format whose key a later format of the run sets.
    /// </summary>
    private static void CleanupContextlessFormattingGap(Transaction transaction, Item item)
    {
        // Iterate until item.Right is null or content.
        while (item?.Right is Item right && (right.Deleted || !right.Countable))
        {
            item = right;
        }

        HashSet<string> attrs = null;

        // Iterate back until a content item is found.
        while (item != null && (item.Deleted || !item.Countable))
        {
            if (!item.Deleted && item.Content is ContentFormat cf && !(attrs ??= new HashSet<string>()).Add(cf.Key))
            {
                item.Delete(transaction);
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
        var res = 0;

        Doc.Transact(transaction =>
        {
            var start = _start;
            var end = _start;
            OrderedDictionary<string, object> startAttributes = null;
            OrderedDictionary<string, object> currentAttributes = null;

            while (end != null)
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

                end = end.Right as Item;
            }
        });

        return res;
    }

    /// <summary>
    /// Yjs 13.6's <c>cleanupYTextAfterTransaction</c>: once per transaction that changed a text with
    /// formatting remotely (<see cref="Transaction._needFormattingCleanup"/>), after its
    /// <see cref="YDoc.AfterTransaction"/> handlers, and in a transaction of its own. A text that got a
    /// format, or lost one, is cleaned up as a whole; around other deleted items, the formats without
    /// effect are deleted.
    /// </summary>
    internal static void CleanupAfterTransaction(Transaction transaction)
    {
        var doc = transaction.Doc;
        var store = doc.Store;
        List<YText> needFullCleanup = null;

        // Check if another formatting item was inserted.
        foreach (var (client, afterClock) in transaction.AfterState)
        {
            var clock = transaction.BeforeState.TryGetValue(client, out var before) ? before : 0;
            if (afterClock == clock)
            {
                continue;
            }

            // As Yjs, with the end clock as the length: to the end of the client's structs.
            store.IterateStructs(transaction, store.Clients[client], clock, afterClock, str =>
            {
                if (str is Item { Deleted: false, Content: ContentFormat, Parent: YText text })
                {
                    AddOnce(ref needFullCleanup, text);
                }

                return true;
            });
        }

        // Cleanup in a new transaction.
        doc.Transact(t =>
        {
            transaction.DeleteSet.IterateDeletedStructs(transaction, str =>
            {
                if (str is not Item item || item.Parent is not YText { HasFormatting: true } parent || (needFullCleanup?.Contains(parent) ?? false))
                {
                    return true;
                }

                if (item.Content is ContentFormat)
                {
                    AddOnce(ref needFullCleanup, parent);
                }
                else
                {
                    // If no formatting attribute was inserted or deleted, we can make due with contextless
                    // formatting cleanups. Contextless: it is not necessary to compute currentAttributes
                    // for the affected position.
                    CleanupContextlessFormattingGap(t, item);
                }

                return true;
            });

            // If a formatting item was inserted, we simply clean the whole type. We need to compute
            // currentAttributes for the current position anyway.
            if (needFullCleanup != null)
            {
                foreach (var text in needFullCleanup)
                {
                    text.CleanupFormatting();
                }
            }
        });

        static void AddOnce(ref List<YText> texts, YText text)
        {
            texts ??= new List<YText>();
            if (!texts.Contains(text))
            {
                texts.Add(text);
            }
        }
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
            CleanupFormattingGap(transaction, start, curPos.Right, startAttrs, ref curPos.CurrentAttributes);
        }

        // Yjs reads the parent from the position's items and fails with a TypeError when the text has none.
        var parent = (curPos.Left ?? curPos.Right ?? throw new InvalidOperationException("The text has no content to delete. Nothing was changed.")).Parent as YText;
        if (parent?._searchMarkers?.Count > 0)
        {
            parent._searchMarkers.UpdateMarkerChanges(curPos.Index, -startLength + length);
        }
    }

    /// <summary>The value an embed or an embedded shared type stands for in a delta.</summary>
    internal static object EmbedOf(IContent content) => content is ContentEmbed embed ? embed.Embed : ((ContentType)content).Type;

    private static OrderedDictionary<string, object> Copy(OrderedDictionary<string, object> attributes) =>
        attributes?.Count > 0 ? new OrderedDictionary<string, object>(attributes) : null;

    /// <summary>The value of <paramref name="key"/> in <paramref name="attributes"/> (null for none), or null.</summary>
    private static object GetAttribute(OrderedDictionary<string, object> attributes, string key) =>
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

    /// <summary>
    /// Yjs' <c>equalAttrs</c>: <c>a === b</c>, or two objects (a map, a list or a byte array) with the
    /// same own properties, each compared with <c>===</c>; a list's properties are its indexes, so
    /// <c>[1, 2]</c> equals <c>{ "0": 1, "1": 2 }</c>.
    /// </summary>
    internal static bool EqualAttrs(object attr1, object attr2) =>
        StrictEquals(attr1, attr2) ||
        (attr1 is IDictionary<string, object> map1 && attr2 is IDictionary<string, object> map2 && EqualFlat(map1, map2)) ||
        (attr1 is System.Collections.IList list1 && attr2 is System.Collections.IList list2 && EqualFlat(list1, list2)) ||
        (attr1 is IDictionary<string, object> map && attr2 is System.Collections.IList list && EqualFlat(map, list)) ||
        (attr1 is System.Collections.IList list3 && attr2 is IDictionary<string, object> map3 && EqualFlat(map3, list3));

    private static bool EqualFlat(IDictionary<string, object> a, IDictionary<string, object> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        foreach (var (key, value) in a)
        {
            if (!b.TryGetValue(key, out var other) || !StrictEquals(value, other))
            {
                return false;
            }
        }

        return true;
    }

    private static bool EqualFlat(System.Collections.IList a, System.Collections.IList b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!StrictEquals(a[i], b[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool EqualFlat(IDictionary<string, object> map, System.Collections.IList list)
    {
        if (map.Count != list.Count)
        {
            return false;
        }

        foreach (var (key, value) in map)
        {
            if (!IsArrayIndex(key, out var index) || index >= list.Count || !StrictEquals(value, list[(int)index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether <paramref name="key"/> is a JavaScript array index: a canonical integer below 2^32 - 1.</summary>
    private static bool IsArrayIndex(string key, out uint index)
    {
        index = 0;
        if (key.Length == 0 || key.Length > 10 || (key[0] == '0' && key.Length > 1))
        {
            return false;
        }

        ulong value = 0;
        foreach (var c in key)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }

            value = (value * 10) + (ulong)(c - '0');
        }

        if (value >= uint.MaxValue)
        {
            return false;
        }

        index = (uint)value;
        return true;
    }

    /// <summary>
    /// The keys of <paramref name="attributes"/> in the order of JavaScript's <c>for..in</c>: array
    /// indexes first, ascending, then the others in insertion order. The dictionary itself when it
    /// has no array index (nothing is allocated).
    /// </summary>
    private static IEnumerable<KeyValuePair<string, object>> InJavaScriptKeyOrder(IDictionary<string, object> attributes)
    {
        foreach (var key in attributes.Keys)
        {
            if (IsArrayIndex(key, out _))
            {
                return attributes.OrderBy(kvp => IsArrayIndex(kvp.Key, out var i) ? i : uint.MaxValue).ToList();
            }
        }

        return attributes;
    }

    /// <summary>
    /// JavaScript's <c>===</c> on the values an attribute can hold: numbers by value (whatever their
    /// CLR type), strings, booleans and big integers by value, maps, lists and byte arrays by reference.
    /// </summary>
    internal static bool StrictEquals(object a, object b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        switch (a)
        {
            case null:
                return false;
            case string sa:
                return b is string sb && string.Equals(sa, sb, StringComparison.Ordinal);
            case bool ba:
                return b is bool bb && ba == bb;
            case System.Numerics.BigInteger ia:
                return b is System.Numerics.BigInteger ib && ia == ib;
        }

        return TryGetNumber(a, out var na) && TryGetNumber(b, out var nb) && na == nb;

        static bool TryGetNumber(object value, out double number)
        {
            switch (value)
            {
                case int i: number = i; return true;
                case long l: number = l; return true;
                case double d: number = d; return true;
                case float f: number = f; return true;
                case short s: number = s; return true;
                case uint u: number = u; return true;
                case ulong ul: number = ul; return true;
                case byte b: number = b; return true;
                case sbyte sb: number = sb; return true;
                case ushort us: number = us; return true;
                case decimal m: number = (double)m; return true;
                default: number = 0; return false;
            }
        }
    }

    /// <summary>Applies <paramref name="format"/> to <paramref name="attributes"/>, which is created when a value is set.</summary>
    private static void UpdateCurrentAttributes(ref OrderedDictionary<string, object> attributes, ContentFormat format)
    {
        if (format.Value == null)
        {
            attributes?.Remove(format.Key);
        }
        else
        {
            (attributes ??= new OrderedDictionary<string, object>())[format.Key] = format.Value;
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
