// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;

namespace Ycs;

/// <summary>
/// Event that describes the changes on a YText type.
/// </summary>
public sealed class YTextEvent : YEvent
{
    private enum ChangeType
    {
        Insert,
        Delete,
        Retain
    }

    private readonly ISet<string?> _subs;
    private IReadOnlySet<string>? _keysChanged;
    private List<Delta>? _delta;
    private YChanges? _changes;

    internal YTextEvent(YText arr, Transaction transaction, ISet<string?> subs)
        : base(arr, transaction)
    {
        _subs = subs;
    }

    /// <summary>
    /// Gets the changed attribute names.
    /// </summary>
    public IReadOnlySet<string> KeysChanged => _keysChanged ??= ChangedKeys.WithoutChildList(_subs);

    /// <summary>
    /// Gets whether the children keys changed.
    /// </summary>
    public bool ChildListChanged => _subs.Contains(null);

    /// <summary>
    /// Yjs' <c>YTextEvent#changes</c>: the <see cref="Keys"/> and the text <see cref="Delta"/>; no
    /// added or deleted items.
    /// </summary>
    public override YChanges Changes => _changes ??= new YChanges(new HashSet<ItemView>(), new HashSet<ItemView>(), Delta, Keys);

    /// <summary>
    /// Compute the changes in the delta format. Overrides <see cref="YEvent.Delta"/> — for
    /// YText this is a Quill-style attribute-aware delta, not the plain insert/retain/delete
    /// shape used by <see cref="YEvent.Changes"/>.Delta, exactly as Yjs does.
    /// </summary>
    public override IReadOnlyList<Delta> Delta
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
                    var currentAttributes = new Dictionary<string, object?>();
                    var oldAttributes = new Dictionary<string, object?>();
                    var item = Target._start;
                    ChangeType? action = null;
                    var attributes = new Dictionary<string, object?>();

                    // An embed, or null while the text inserted so far is in insertText: appending to a string is
                    // quadratic in the run's length.
                    YValue? insert = null;
                    var insertText = new StringBuilder();
                    int retain = 0;
                    int deleteLen = 0;

                    void addOp()
                    {
                        if (action != null)
                        {
                            Delta? op = null;

                            switch (action)
                            {
                                case ChangeType.Delete:
                                    if (deleteLen > 0)
                                    {
                                        op = new Delta.Delete(deleteLen);
                                    }

                                    deleteLen = 0;
                                    break;
                                case ChangeType.Insert:
                                    var inserted = insert ?? insertText.ToString();
                                    if (!inserted.TryGetString(out var pending) || pending.Length > 0)
                                    {
                                        op = new Delta.Insert(inserted)
                                        {
                                            Attributes = currentAttributes.Count > 0 ? YText.AttributeValues(currentAttributes, skipNull: true) : null,
                                        };
                                    }

                                    insert = null;
                                    insertText.Clear();
                                    break;
                                case ChangeType.Retain:
                                    if (retain > 0)
                                    {
                                        op = new Delta.Retain(retain)
                                        {
                                            Attributes = attributes.Count > 0 ? YText.AttributeValues(attributes, skipNull: false) : null,
                                        };
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

                    while (!item.IsNull)
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

                                        insertText.Append(((ContentString)item.Content).GetString());
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

                                    YText.UpdateCurrentAttributes(currentAttributes, (ContentFormat)item.Content);
                                }
                                break;
                        }

                        item = item.Right.AsItem;
                    }

                    addOp();

                    while (delta.Count > 0)
                    {
                        var lastOp = delta[delta.Count - 1];
                        if (lastOp is Delta.Retain && lastOp.Attributes == null)
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
        public ItemRef Left;
        public ItemRef Right;
        public int Index;
        public OrderedDictionary<string, object?>? CurrentAttributes;

        public ItemTextListPosition(ItemRef left, ItemRef right, int index)
        {
            Left = left;
            Right = right;
            Index = index;
            CurrentAttributes = null;
        }

        public void Forward()
        {
            var right = Right;
            if (right.IsNull)
            {
                throw new Exception("Unexpected");
            }

            switch (right.Content)
            {
                default:
                    if (!right.Deleted)
                    {
                        Index += right.Length;
                    }
                    break;
                case ContentFormat cf:
                    if (!right.Deleted)
                    {
                        UpdateCurrentAttributes(ref CurrentAttributes, cf);
                    }
                    break;
            }

            Left = right;
            Right = right.Right.AsItem;
        }

        public void FindNextPosition(Transaction transaction, int count)
        {
            var doc = transaction.Doc;
            var steps = 0;
            while (!Right.IsNull && count > 0)
            {
                // Within a run, the index jumps over what this loop passes and only counts (no live format: the loop
                // reads those).
                if (++steps == doc.LiteralWalkStepsForTests && !doc.RunHeadIndexDisabledForTests)
                {
                    steps = 0;
                    var before = count;
                    var reached = doc.Store.SeekRight(Right, ref count, stopAtFormats: true);
                    if (reached != Right)
                    {
                        Index += before - count;
                        Right = reached;
                        Left = reached.Left.AsItem;
                        continue;
                    }
                }

                var right = Right;
                switch (right.Content)
                {
                    default:
                        if (!right.Deleted)
                        {
                            if (count < right.Length)
                            {
                                // Split right.
                                transaction.Doc.Store.GetItemCleanStart(transaction, new ID(right.Id.Client, right.Id.Clock + count));
                            }

                            Index += right.Length;
                            count -= right.Length;
                        }
                        break;
                    case ContentFormat cf:
                        if (!right.Deleted)
                        {
                            UpdateCurrentAttributes(ref CurrentAttributes, cf);
                        }
                        break;
                }

                Left = right;
                Right = right.Right.AsItem;
                // We don't forward() because that would halve the performance because we already do the checks above.
            }
        }

        /// <summary>
        /// Negate applied formats.
        /// </summary>
        public void InsertNegatedAttributes(Transaction transaction, YType parent, OrderedDictionary<string, object?>? negatedAttributes)
        {
            // Check if we really need to remove attributes.
            while (
                !Right.IsNull && (
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
                    Debug.Assert(negatedAttributes != null);
                    negatedAttributes.Remove(((ContentFormat)Right.Content).Key);
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
                var nextFormat = ItemRef.New(new ID(ownClientId, doc.Store.GetState(ownClientId)), left, right, parent, null, new ContentFormat(kvp.Key, kvp.Value));
                nextFormat.Integrate(transaction, 0);
                Right = nextFormat;
                Forward();
            }
        }

        public void MinimizeAttributeChanges(IDictionary<string, object?>? attributes)
        {
            // Go right while attributes[right.Key] == right.Value (or right is deleted).
            while (!Right.IsNull)
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
    private readonly record struct PendingOperation(PendingKind Kind, int Index, int Length, string? Name, object? Value, IReadOnlyDictionary<string, YValue>? Attributes);

    private List<PendingOperation>? _pending;

    // The last result of ToString, valid while the store's version and Length are as they were then.
    private string? _string;
    private long _stringVersion;
    private int _stringLength;

    /// <summary>
    /// Whether a formatting attribute was ever integrated into this text (Yjs' <c>_hasFormatting</c>).
    /// Set by <see cref="ContentFormat"/>; it is never reset.
    /// </summary>
    internal bool HasFormatting { get; set; }

    private void AddPending(PendingOperation operation)
    {
        (_pending ??= new List<PendingOperation>()).Add(operation);
    }

    private void InsertInTransaction(Transaction transaction, int index, string text, IDictionary<string, object?>? attributes)
    {
        var pos = FindPosition(transaction, index, useSearchMarker: attributes == null);
        if (attributes == null && pos.CurrentAttributes?.Count > 0)
        {
            // Without attributes, the text takes the attributes at its position.
            attributes = new Dictionary<string, object?>(pos.CurrentAttributes);
        }

        InsertText(transaction, ref pos, text, attributes);
    }

    private void DeleteInTransaction(Transaction transaction, int index, int length)
    {
        var pos = FindPosition(transaction, index, useSearchMarker: true);
        DeleteText(transaction, ref pos, length);
    }

    internal override void Integrate(YDoc doc, ItemRef item)
    {
        // Recurses once per level of nested types: an exception, not a crash, where the stack runs out.
        RuntimeHelpers.EnsureSufficientExecutionStack();
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
                    Debug.Assert(op.Value != null);
                    Insert(op.Index, (string)op.Value, op.Attributes);
                    break;
                case PendingKind.InsertEmbed:
                    Debug.Assert(op.Value != null);
                    InsertEmbed(op.Index, (YValue)op.Value, op.Attributes);
                    break;
                case PendingKind.Delete:
                    Remove(op.Index, op.Length);
                    break;
                case PendingKind.Format:
                    Debug.Assert(op.Attributes != null);
                    Format(op.Index, op.Length, op.Attributes);
                    break;
                case PendingKind.ApplyDelta:
                    Debug.Assert(op.Value != null);
                    ApplyDelta((Delta[])op.Value, sanitize: op.Length != 0);
                    break;
                case PendingKind.SetAttribute:
                    Debug.Assert(op.Name != null && op.Value != null);
                    SetAttribute(op.Name, (YValue)op.Value);
                    break;
                case PendingKind.RemoveAttribute:
                    Debug.Assert(op.Name != null);
                    RemoveAttribute(op.Name);
                    break;
            }
        }
    }

    internal override YType InternalCopy() => new YText();

    internal override YType InternalClone()
    {
        // Recurses once per level of nested types: an exception, not a crash, where the stack runs out.
        RuntimeHelpers.EnsureSufficientExecutionStack();
        var text = new YText();
        text.ApplyDelta(ToDelta());
        return text;
    }

    internal override void CallObserver(Transaction transaction, ISet<string?> parentSubs)
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
            var pos = new ItemTextListPosition(marker.P.Left.AsItem, marker.P, marker.Index);
            pos.FindNextPosition(transaction, index - marker.Index);
            return pos;
        }
        else
        {
            var pos = new ItemTextListPosition(default, _start, 0);
            pos.FindNextPosition(transaction, index);
            return pos;
        }
    }

    /// <returns>The attributes to negate after the content (null for none).</returns>
    private OrderedDictionary<string, object?>? InsertAttributes(Transaction transaction, ref ItemTextListPosition currPos, IDictionary<string, object?>? attributes)
    {
        if (attributes == null)
        {
            return null;
        }

        var doc = transaction.Doc;
        var ownClientId = doc.ClientId;
        OrderedDictionary<string, object?>? negatedAttributes = null;

        // Insert format-start items.
        foreach (var kvp in InJavaScriptKeyOrder(attributes))
        {
            var key = kvp.Key;
            var value = kvp.Value;
            var currentVal = GetAttribute(currPos.CurrentAttributes, key);

            if (!EqualAttrs(currentVal, value))
            {
                // Save negated attribute (set null if currentVal is not set).
                (negatedAttributes ??= new OrderedDictionary<string, object?>())[key] = currentVal;

                currPos.Right = ItemRef.New(new ID(ownClientId, doc.Store.GetState(ownClientId)), currPos.Left, currPos.Right, this, null, new ContentFormat(key, ValueFreezer.Freeze(value, doc.Limits.MaxDepth)));
                currPos.Right.Integrate(transaction, 0);
                currPos.Forward();
            }
        }

        return negatedAttributes;
    }

    /// <param name="attributes">The attributes of the text; null for none. Attributes at the position that are not in them are set to null in them, as in Yjs.</param>
    private void InsertText(Transaction transaction, ref ItemTextListPosition currPos, object? text, IDictionary<string, object?>? attributes)
    {
        if (text is YType)
        {
            CheckCanNest();
        }

        if (currPos.CurrentAttributes?.Count > 0)
        {
            attributes ??= new Dictionary<string, object?>();
            foreach (var kvp in currPos.CurrentAttributes)
            {
                attributes.TryAdd(kvp.Key, null);
            }
        }

        var doc = transaction.Doc;
        var ownClientId = doc.ClientId;

        currPos.MinimizeAttributeChanges(attributes);
        var negatedAttributes = InsertAttributes(transaction, ref currPos, attributes);

        // Insert content (a text's content is made only when no run takes it).
        var str = text as string;
        IContent? content = null;
        int length;
        if (str != null)
        {
            length = str.Length;
        }
        else
        {
            content = text is YType type ? new ContentType(type) : new ContentEmbed(Validated(text, doc.Limits.MaxDepth));
            length = content.Length;
        }

        if (_searchMarkers?.Count > 0)
        {
            _searchMarkers.UpdateMarkerChanges(transaction, currPos.Index, length);
            currPos.Left = LeftOf(currPos.Left, currPos.Right);
        }

        if (str != null && length > 0 && negatedAttributes == null && !currPos.Left.IsNull
            && currPos.Left.CanExtendRun(transaction, this, currPos.Right, typeof(ContentString), ownClientId))
        {
            // Typing on at the end of this client's run: the run takes the text in place of a new item.
            var left = currPos.Left;
            ((ContentString)left.Content).Append(str);
            left.ExtendRun(transaction, length);
            currPos.Index += length;
            currPos.InsertNegatedAttributes(transaction, this, null);
            return;
        }

        currPos.Right = ItemRef.New(new ID(ownClientId, doc.Store.GetState(ownClientId)), currPos.Left, currPos.Right, this, null, str != null ? new ContentString(str) : content);
        currPos.Right.Integrate(transaction, 0);
        currPos.Forward();

        currPos.InsertNegatedAttributes(transaction, this, negatedAttributes);
    }

    private void FormatText(Transaction transaction, ref ItemTextListPosition curPos, int length, Dictionary<string, object?> attributes)
    {
        var doc = transaction.Doc;
        var ownClientId = doc.ClientId;

        curPos.MinimizeAttributeChanges(attributes);
        var negatedAttributes = InsertAttributes(transaction, ref curPos, attributes) ?? new OrderedDictionary<string, object?>();

        // Iterate until the first non-format or null is found, and delete all formats with
        // attributes[format.Key] != null. Also check the formats after the formatted range, so that
        // no redundant negated attributes are inserted there.
        while (!curPos.Right.IsNull &&
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
                            (curPos.CurrentAttributes ??= new OrderedDictionary<string, object?>())[cf.Key] = cf.Value;
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
            curPos.Right = ItemRef.New(new ID(ownClientId, doc.Store.GetState(ownClientId)), curPos.Left, curPos.Right, this, null, new ContentString(new string('\n', length)));
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
    private static int CleanupFormattingGap(Transaction transaction, ItemRef start, ItemRef curr, OrderedDictionary<string, object?>? startAttributes, ref OrderedDictionary<string, object?>? currAttributes)
    {
        var end = start;
        Dictionary<string, ContentFormat>? endFormats = null;
        while (!end.IsNull && (!end.Countable || end.Deleted))
        {
            if (!end.Deleted && end.Content is ContentFormat format)
            {
                (endFormats ??= new Dictionary<string, ContentFormat>())[format.Key] = format;
            }

            end = end.Right.AsItem;
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
                if (endFormats == null || !endFormats.TryGetValue(key, out var endFormat) || endFormat != cf || JsValue.StrictEquals(startAttrValue, value))
                {
                    // Either this format is overwritten or it is not necessary because the attribute already existed.
                    start.Delete(transaction);
                    cleanups++;
                    if (!reachedCurr && JsValue.StrictEquals(GetAttribute(currAttributes, key), value) && !JsValue.StrictEquals(startAttrValue, value))
                    {
                        if (startAttrValue == null)
                        {
                            currAttributes?.Remove(key);
                        }
                        else
                        {
                            (currAttributes ??= new OrderedDictionary<string, object?>())[key] = startAttrValue;
                        }
                    }
                }

                if (!reachedCurr && !start.Deleted)
                {
                    UpdateCurrentAttributes(ref currAttributes, cf);
                }
            }

            start = start.Right.AsItem;
        }

        return cleanups;
    }

    /// <summary>
    /// Yjs 13.6's <c>cleanupContextlessFormattingGap</c>: in the run of deleted and uncountable items
    /// around <paramref name="item"/>, deletes every format whose key a later format of the run sets.
    /// </summary>
    private static void CleanupContextlessFormattingGap(Transaction transaction, ItemRef item)
    {
        // Iterate until item.Right is null or content.
        while (!item.IsNull && item.Right.IsItem && (item.Right.Deleted || !item.Right.Countable))
        {
            item = item.Right;
        }

        HashSet<string>? attrs = null;

        // Iterate back until a content item is found.
        while (!item.IsNull && (item.Deleted || !item.Countable))
        {
            if (!item.Deleted && item.Content is ContentFormat cf && !(attrs ??= new HashSet<string>()).Add(cf.Key))
            {
                item.Delete(transaction);
            }

            item = item.Left.AsItem;
        }
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
        // Enumerated in the order of adding, as Yjs' Set: nothing is removed.
        HashSet<YText>? needFullCleanup = null;

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
                if (str.IsItem && !str.Deleted && str.Content is ContentFormat && str.Parent is YText text)
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
                if (!str.IsItem || str.Parent is not YText { HasFormatting: true } parent || (needFullCleanup?.Contains(parent) ?? false))
                {
                    return true;
                }

                var item = str;

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

        static void AddOnce(ref HashSet<YText>? texts, YText text) => (texts ??= new HashSet<YText>()).Add(text);
    }

    private static void DeleteText(Transaction transaction, ref ItemTextListPosition curPos, int length)
    {
        var startLength = length;
        var startAttrs = Copy(curPos.CurrentAttributes);
        var start = curPos.Right;

        while (length > 0 && !curPos.Right.IsNull)
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

        if (!start.IsNull)
        {
            CleanupFormattingGap(transaction, start, curPos.Right, startAttrs, ref curPos.CurrentAttributes);
        }

        // Yjs reads the parent from the position's items and fails with a TypeError when the text has none.
        var parent = (!curPos.Left.IsNull ? curPos.Left : !curPos.Right.IsNull ? curPos.Right : throw new InvalidOperationException("The text has no content to delete. Nothing was changed.")).Parent as YText;
        if (parent?._searchMarkers?.Count > 0)
        {
            parent._searchMarkers.UpdateMarkerChanges(transaction, curPos.Index, -startLength + length);
        }
    }

    /// <summary>The value an embed or an embedded shared type stands for in a delta.</summary>
    internal static YValue EmbedOf(IContent content) => content is ContentEmbed embed ? YValue.FromStored(embed.Embed) : YValue.FromStored(((ContentType)content).Type);

    /// <summary>The attributes as a delta carries them; a change (<see cref="ChangeKey"/>) as the object it is in Yjs.</summary>
    internal static IReadOnlyDictionary<string, YValue> AttributeValues(Dictionary<string, object?> attributes, bool skipNull)
    {
        var values = new AttributeSet(attributes.Count);
        foreach (var kvp in attributes)
        {
            if (kvp.Value == null && skipNull)
            {
                continue;
            }

            values.Add(kvp.Key, kvp.Value is YChange change ? ChangeValue(change) : YValue.FromStored(kvp.Value));
        }

        return values;
    }

    private static YValue ChangeValue(YChange change)
    {
        var type = new KeyValuePair<string, YValue>("type", change.Kind == YChangeKind.Added ? "added" : "removed");
        return change.User is { } user
            ? YValue.Object(type, new KeyValuePair<string, YValue>("user", user))
            : YValue.Object(type);
    }

    // The attributes of the operation being made, as the document stores them. The text's insert and format code changes the
    // dictionary it is given and does not keep it, so one is reused: each operation is finished before the next converts.
    private Dictionary<string, object?>? _storedAttributes;

    /// <summary>The attributes of an operation of the caller's as the document stores them, in a dictionary the text may change; null for none.</summary>
    [return: NotNullIfNotNull(nameof(attributes))]
    private Dictionary<string, object?>? StoredAttributes(IReadOnlyDictionary<string, YValue>? attributes)
    {
        if (attributes == null)
        {
            return null;
        }

        var stored = _storedAttributes ??= new Dictionary<string, object?>();
        stored.Clear();
        if (attributes is Dictionary<string, YValue> dictionary)
        {
            // The struct enumerator: the interface's allocates one.
            foreach (var kvp in dictionary)
            {
                stored[kvp.Key] = kvp.Value.Stored();
            }
        }
        else
        {
            foreach (var kvp in attributes)
            {
                stored[kvp.Key] = kvp.Value.Stored();
            }
        }

        return stored;
    }

    private static Dictionary<string, YValue>? Copy(IReadOnlyDictionary<string, YValue>? attributes) =>
        attributes == null ? null : new Dictionary<string, YValue>(attributes);

    private static OrderedDictionary<string, object?>? Copy(OrderedDictionary<string, object?>? attributes) =>
        attributes?.Count > 0 ? new OrderedDictionary<string, object?>(attributes) : null;

    /// <summary>The value of <paramref name="key"/> in <paramref name="attributes"/> (null for none), or null.</summary>
    private static object? GetAttribute(OrderedDictionary<string, object?>? attributes, string key) =>
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
    /// Yjs' <c>equalAttrs</c>: <c>a === b</c>, or two objects (a map, a list or a byte array) with the
    /// same own properties, each compared with <c>===</c>; a list's properties are its indexes, so
    /// <c>[1, 2]</c> equals <c>{ "0": 1, "1": 2 }</c>.
    /// </summary>
    private static object Validated(object? embed, int maxDepth)
    {
        return ValueFreezer.Freeze(embed, maxDepth)!;
    }

    internal static bool EqualAttrs(object? attr1, object? attr2) =>
        JsValue.StrictEquals(attr1, attr2) ||
        (attr1 is IDictionary<string, object?> map1 && attr2 is IDictionary<string, object?> map2 && EqualFlat(map1, map2)) ||
        (attr1 is System.Collections.IList list1 && attr2 is System.Collections.IList list2 && EqualFlat(list1, list2)) ||
        (attr1 is IDictionary<string, object?> map && attr2 is System.Collections.IList list && EqualFlat(map, list)) ||
        (attr1 is System.Collections.IList list3 && attr2 is IDictionary<string, object?> map3 && EqualFlat(map3, list3));

    private static bool EqualFlat(IDictionary<string, object?> a, IDictionary<string, object?> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        foreach (var (key, value) in a)
        {
            if (!b.TryGetValue(key, out var other) || !JsValue.StrictEquals(value, other))
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
            if (!JsValue.StrictEquals(a[i], b[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool EqualFlat(IDictionary<string, object?> map, System.Collections.IList list)
    {
        if (map.Count != list.Count)
        {
            return false;
        }

        foreach (var (key, value) in map)
        {
            if (!IsArrayIndex(key, out var index) || index >= list.Count || !JsValue.StrictEquals(value, list[(int)index]))
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
    private static IEnumerable<KeyValuePair<string, object?>> InJavaScriptKeyOrder(IDictionary<string, object?> attributes)
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

    /// <summary>Applies <paramref name="format"/> to <paramref name="attributes"/>, which is created when a value is set.</summary>
    private static void UpdateCurrentAttributes(ref OrderedDictionary<string, object?>? attributes, ContentFormat format)
    {
        if (format.Value == null)
        {
            attributes?.Remove(format.Key);
        }
        else
        {
            (attributes ??= new OrderedDictionary<string, object?>())[format.Key] = format.Value;
        }
    }

    internal static void UpdateCurrentAttributes(IDictionary<string, object?> attributes, ContentFormat format)
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
