// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

public class StackItem
{
    public IDictionary<long, long> BeforeState;
    public IDictionary<long, long> AfterState;
    // Use this to save and restore metadata like selection range.
    public IDictionary<string, object> Meta;

    internal DeleteSet DeleteSet;

    internal StackItem(DeleteSet ds, IDictionary<long, long> beforeState, IDictionary<long, long> afterState)
    {
        DeleteSet = ds;
        BeforeState = beforeState;
        AfterState = afterState;
        // TODO: [alekseyk] Always needed?
        Meta = new Dictionary<string, object>();
    }
}

/// <summary>
/// Fires 'stack-item-added' event when a stack item was added to either the undo- or
/// the redo-stack. You may store additional stack information via the metadata property
/// on 'event.stackItem.meta' (it is a collection of metadata properties).
/// Fires 'stack-item-popped' event when a stack item was popped from either the undo- or
/// the redo-stack. You may restore the saved stack information from 'event.stackItem.Meta'.
/// Fires 'stack-cleared' (<see cref="StackCleared"/>) event when <see cref="Clear()"/> empties
/// either (or both) stacks.
/// </summary>
public class UndoManager
{
    public enum OperationType
    {
        Undo,
        Redo
    }

    public class StackEventArgs : EventArgs
    {
        // TODO: [alekseyk] To Read-Only.
        public StackEventArgs(StackItem item, OperationType type, IDictionary<AbstractType, IList<YEvent>> changedParentTypes, object origin)
        {
            StackItem = item;
            Type = type;
            ChangedParentTypes = changedParentTypes;
            Origin = origin;
        }

        public StackItem StackItem { get; }
        public OperationType Type { get; }
        public IDictionary<AbstractType, IList<YEvent>> ChangedParentTypes { get; }
        public object Origin { get; }
    }

    /// <summary>
    /// Payload for the <see cref="StackCleared"/> event. Mirrors Yjs' <c>'stack-cleared'</c>
    /// event, which reports which of the two stacks were actually emptied.
    /// </summary>
    public class StackClearedEventArgs : EventArgs
    {
        public StackClearedEventArgs(bool undoStackCleared, bool redoStackCleared)
        {
            UndoStackCleared = undoStackCleared;
            RedoStackCleared = redoStackCleared;
        }

        public bool UndoStackCleared { get; }
        public bool RedoStackCleared { get; }
    }

    // Scope entries are either an AbstractType (only that type's subtree is tracked) or a YDoc
    // (the whole document is tracked). C# idiom: Yjs represents this as
    // `Array<AbstractType<any> | Doc>`; since AbstractType and YDoc are unrelated classes in
    // this port, `object` plays the role of that union, exactly as elsewhere in this codebase
    // (e.g. `ISet<object> trackedOrigins`).
    private readonly List<object> _scope = new List<object>();
    private Func<Item, bool> _deleteFilter;
    private ISet<object> _trackedOrigins;
    private Stack<StackItem> _undoStack;
    private Stack<StackItem> _redoStack;

    // Whether the client is currently undoing (calling UndoManager.Undo()).
    private bool _undoing;
    private bool _redoing;
    private YDoc _doc;
    private DateTime _lastChange;
    private int _captureTimeout;
    private bool _destroyed;

    // The transaction of the running Undo or Redo, and its changed parent types as they were at its
    // 'AfterTransaction' (the transaction object is reused once it has ended).
    private Transaction _popTransaction;
    private IDictionary<AbstractType, IList<YEvent>> _poppedChangedParentTypes;

    public UndoManager(AbstractType typeScope)
        : this(new[] { typeScope }, 500, it => true, new HashSet<object> { null })
    {
        // Do nothing.
    }

    // TODO: [alekseyk] Set default parameters (not for the Func<>), or create options, like YDocOptions.
    public UndoManager(IList<AbstractType> typeScopes, int captureTimeout, Func<Item, bool> deleteFilter, ISet<object> trackedOrigins)
        : this(typeScopes?.Cast<object>(), typeScopes != null && typeScopes.Count > 0 ? typeScopes[0].Doc : null, captureTimeout, deleteFilter, trackedOrigins)
    {
        // Do nothing.
    }

    /// <summary>
    /// Limits the scope of the UndoManager to the whole document: every change on <paramref name="doc"/>
    /// will be tracked, regardless of which type it touched. Mirrors Yjs' <c>new UndoManager(doc, options)</c>.
    /// </summary>
    public UndoManager(YDoc doc, int captureTimeout = 500, Func<Item, bool> deleteFilter = null, ISet<object> trackedOrigins = null)
        : this(new object[] { doc ?? throw new ArgumentNullException(nameof(doc)) }, doc, captureTimeout, deleteFilter, trackedOrigins)
    {
        // Do nothing.
    }

    /// <summary>
    /// Limits the scope of the UndoManager to a mix of types and/or the owning document.
    /// Mirrors Yjs' <c>new UndoManager(typeScope, options)</c> where <c>typeScope</c> may be an
    /// array containing both <c>AbstractType</c>s and a <c>Doc</c>.
    /// </summary>
    public UndoManager(IEnumerable<object> scope, int captureTimeout = 500, Func<Item, bool> deleteFilter = null, ISet<object> trackedOrigins = null)
        : this(scope, InferDoc(scope), captureTimeout, deleteFilter, trackedOrigins)
    {
        // Do nothing.
    }

    private UndoManager(IEnumerable<object> scope, YDoc doc, int captureTimeout, Func<Item, bool> deleteFilter, ISet<object> trackedOrigins)
    {
        if (doc == null)
        {
            throw new ArgumentException("Could not determine the owning YDoc from the given scope; pass a non-empty scope or use the (YDoc, ...) constructor.");
        }

        _doc = doc;
        _deleteFilter = deleteFilter ?? (_ => true);
        // Match Yjs' actual default (`trackedOrigins = new Set([null])`): untracked transactions
        // (the common case -- a plain edit with no explicit origin) must be tracked by default.
        _trackedOrigins = trackedOrigins ?? new HashSet<object> { null };
        _trackedOrigins.Add(this);
        _undoStack = new Stack<StackItem>();
        _redoStack = new Stack<StackItem>();
        _undoing = false;
        _redoing = false;
        _lastChange = DateTime.MinValue;
        _captureTimeout = captureTimeout;

        AddToScope(scope);

        _doc.AfterTransaction += OnAfterTransaction;
        _doc.Destroyed += OnDocDestroyed;
    }

    private static YDoc InferDoc(IEnumerable<object> scope)
    {
        foreach (var entry in scope ?? Enumerable.Empty<object>())
        {
            switch (entry)
            {
                case YDoc d:
                    return d;
                case AbstractType at when at.Doc != null:
                    return at.Doc;
            }
        }

        return null;
    }

    public event EventHandler<StackEventArgs> StackItemAdded;
    public event EventHandler<StackEventArgs> StackItemPopped;

    /// <summary>
    /// Fires when <see cref="Clear()"/> (or <see cref="Clear(bool, bool)"/>) empties either stack.
    /// Mirrors Yjs' <c>'stack-cleared'</c> event.
    /// </summary>
    public event EventHandler<StackClearedEventArgs> StackCleared;

    public int Count => _undoStack.Count;

    /// <summary>
    /// The document this UndoManager operates on. Mirrors Yjs' <c>undoManager.doc</c>.
    /// </summary>
    public YDoc Doc => _doc;

    /// <summary>
    /// The current tracking scope (a read-only view of <see cref="AbstractType"/> and/or
    /// <see cref="YDoc"/> entries). Mirrors Yjs' <c>undoManager.scope</c>.
    /// </summary>
    public IReadOnlyList<object> Scope => _scope;

    /// <summary>
    /// Extend the scope with one more tracked type (or the whole document). Mirrors Yjs'
    /// <c>addToScope(ytype)</c>.
    /// </summary>
    public void AddToScope(object scopeEntry)
    {
        AddToScope(new[] { scopeEntry });
    }

    /// <summary>
    /// Extend the scope with more tracked types (and/or the whole document). Each entry must be
    /// an <see cref="AbstractType"/> or a <see cref="YDoc"/>. Mirrors Yjs' <c>addToScope(ytypes)</c>.
    /// </summary>
    public void AddToScope(IEnumerable<object> scopeEntries)
    {
        if (scopeEntries == null)
        {
            return;
        }

        foreach (var entry in scopeEntries)
        {
            if (entry == null || _scope.Contains(entry))
            {
                continue;
            }

            if (!(entry is AbstractType) && !(entry is YDoc))
            {
                throw new ArgumentException($"UndoManager scope entries must be an {nameof(AbstractType)} or a {nameof(YDoc)}.", nameof(scopeEntries));
            }

            _scope.Add(entry);
        }
    }

    /// <summary>
    /// Track an additional transaction origin. Mirrors Yjs' <c>addTrackedOrigin(origin)</c>.
    /// </summary>
    public void AddTrackedOrigin(object origin) => _trackedOrigins.Add(origin);

    /// <summary>
    /// Stop tracking a transaction origin. Mirrors Yjs' <c>removeTrackedOrigin(origin)</c>.
    /// </summary>
    public void RemoveTrackedOrigin(object origin) => _trackedOrigins.Remove(origin);

    /// <summary>
    /// Are undo steps available? Mirrors Yjs' <c>canUndo()</c>.
    /// </summary>
    public bool CanUndo() => _undoStack.Count > 0;

    /// <summary>
    /// Are redo steps available? Mirrors Yjs' <c>canRedo()</c>.
    /// </summary>
    public bool CanRedo() => _redoStack.Count > 0;

    public void Clear() => Clear(true, true);

    /// <summary>
    /// Clears the undo and/or redo stack, releasing the items they were keeping alive for
    /// potential redo. Fires <see cref="StackCleared"/> if either stack was actually emptied.
    /// Mirrors Yjs' <c>clear(clearUndoStack, clearRedoStack)</c>.
    /// </summary>
    public void Clear(bool clearUndoStack, bool clearRedoStack)
    {
        if (!((clearUndoStack && CanUndo()) || (clearRedoStack && CanRedo())))
        {
            return;
        }

        _doc.Transact(tr =>
        {
            void clearItem(StackItem stackItem)
            {
                stackItem.DeleteSet.IterateDeletedStructs(tr, i =>
                {
                    if (i is Item item && InScope(item))
                    {
                        item.KeepItemAndParents(false);
                    }

                    return true;
                });
            }

            if (clearUndoStack)
            {
                foreach (var item in _undoStack)
                {
                    clearItem(item);
                }

                _undoStack.Clear();
            }

            if (clearRedoStack)
            {
                foreach (var item in _redoStack)
                {
                    clearItem(item);
                }

                _redoStack.Clear();
            }
        });

        StackCleared?.Invoke(this, new StackClearedEventArgs(clearUndoStack, clearRedoStack));
    }

    /// <summary>
    /// UndoManager merges Undo-StackItem if they are created within time-gap
    /// smaller than 'captureTimeout'. Call this method so that the next StackItem
    /// won't be merged.
    /// </summary>
    public void StopCapturing()
    {
        _lastChange = DateTime.MinValue;
    }

    /// <summary>
    /// Undo last changes on type.
    /// </summary>
    /// <returns>
    /// Returns stack item if a change was applied.
    /// </returns>
    public StackItem Undo()
    {
        _undoing = true;
        StackItem res;

        try
        {
            res = PopStackItem(_undoStack, OperationType.Undo);
        }
        finally
        {
            _undoing = false;
        }

        return res;
    }

    /// <summary>
    /// Redo last changes on type.
    /// </summary>
    /// <returns>
    /// Returns stack item if a change was applied.
    /// </returns>
    public StackItem Redo()
    {
        _redoing = true;
        StackItem res;

        try
        {
            res = PopStackItem(_redoStack, OperationType.Redo);
        }
        finally
        {
            _redoing = false;
        }

        return res;
    }

    /// <summary>
    /// Unhooks this UndoManager from its document and stops tracking. Mirrors Yjs' <c>destroy()</c>.
    /// Safe to call more than once.
    /// </summary>
    public void Destroy()
    {
        if (_destroyed)
        {
            return;
        }

        _destroyed = true;
        _trackedOrigins.Remove(this);
        _doc.AfterTransaction -= OnAfterTransaction;
        _doc.Destroyed -= OnDocDestroyed;
    }

    private void OnDocDestroyed(object sender, EventArgs e) => Destroy();

    private void OnAfterTransaction(object sender, Transaction transaction)
    {
        if (transaction == _popTransaction)
        {
            _poppedChangedParentTypes = CopyChangedParentTypes(transaction, StackItemPopped);
        }

        // Only track certain transactions.
        if (!ScopeChanged(transaction) ||
            (!_trackedOrigins.Contains(transaction.Origin) &&
                (transaction.Origin == null || !_trackedOrigins.Contains(transaction.Origin.GetType()))))
        {
            return;
        }

        var undoing = _undoing;
        var redoing = _redoing;
        var stack = undoing ? _redoStack : _undoStack;

        if (undoing)
        {
            // Next undo should not be appended to last stack item.
            StopCapturing();
        }
        else if (!redoing)
        {
            // Neither undoing nor redoing: delete redoStack.
            Clear(false, true);
        }

        // The stack item keeps the transaction's states and delete set, which the transaction then
        // does not reuse.
        var now = DateTime.UtcNow;
        if ((now - _lastChange).TotalMilliseconds < _captureTimeout && stack.Count > 0 && !undoing && !redoing)
        {
            // Append change to last stack op.
            var lastOp = stack.Peek();
            lastOp.DeleteSet = new DeleteSet(new List<DeleteSet>() { lastOp.DeleteSet, transaction.DeleteSet });
            lastOp.AfterState = transaction.TakeAfterState();
        }
        else
        {
            // Create a new stack op.
            var item = new StackItem(transaction.TakeDeleteSet(), transaction.TakeBeforeState(), transaction.TakeAfterState());
            stack.Push(item);
        }

        if (!undoing && !redoing)
        {
            _lastChange = now;
        }

        // Make sure that deleted structs are not GC'd.
        transaction.DeleteSet.IterateDeletedStructs(transaction, i =>
        {
            if (i is Item item && InScope(item))
            {
                item.KeepItemAndParents(true);
            }

            return true;
        });

        var added = StackItemAdded;
        added?.Invoke(this, new StackEventArgs(stack.Peek(), undoing ? OperationType.Redo : OperationType.Undo, CopyChangedParentTypes(transaction, added), transaction.Origin));
    }

    private bool ScopeChanged(Transaction transaction)
    {
        foreach (var entry in _scope)
        {
            if (entry is YDoc || (entry is AbstractType at && transaction._changedParentTypes.ContainsKey(at)))
            {
                return true;
            }
        }

        return false;
    }

    private bool InScope(Item item)
    {
        foreach (var entry in _scope)
        {
            if (IsParentOf(entry, item))
            {
                return true;
            }
        }

        return false;
    }

    // A stack event outlives the transaction; a copy is made only for a subscribed event.
    private static Dictionary<AbstractType, IList<YEvent>> CopyChangedParentTypes(Transaction transaction, Delegate handler) =>
        handler == null ? null : new Dictionary<AbstractType, IList<YEvent>>(transaction._changedParentTypes);

    private StackItem PopStackItem(Stack<StackItem> stack, OperationType eventType)
    {
        StackItem result = null;

        // Keep a reference to the transaction so we can fire the event with the 'changedParentTypes'.
        Transaction tr = null;

        // Reset here too: a Transact that threw left them set.
        _popTransaction = null;
        _poppedChangedParentTypes = null;

        _doc.Transact(transaction =>
        {
            tr = transaction;
            _popTransaction = transaction;

            while (stack.Count > 0 && result == null)
            {
                var stackItem = stack.Pop();
                var itemsToRedo = new HashSet<Item>();
                var itemsToDelete = new List<Item>();
                var performedChange = false;

                foreach (var kvp in stackItem.AfterState)
                {
                    var client = kvp.Key;
                    var endClock = kvp.Value;

                    if (!stackItem.BeforeState.TryGetValue(client, out var startClock))
                    {
                        startClock = 0;
                    }

                    var len = endClock - startClock;
                    var structs = _doc.Store.Clients[client];

                    if (startClock != endClock)
                    {
                        // Make sure structs don't overlap with the range of created operations [stackItem.start, stackItem.start + stackItem.end).
                        // This must be executed before deleted structs are iterated.
                        _doc.Store.GetItemCleanStart(transaction, new ID(client, startClock));

                        if (endClock < _doc.Store.GetState(client))
                        {
                            _doc.Store.GetItemCleanStart(transaction, new ID(client, endClock));
                        }

                        _doc.Store.IterateStructs(transaction, structs, startClock, len, str =>
                        {
                            if (str is Item it)
                            {
                                if (it.Redone != null)
                                {
                                    var redoneResult = _doc.Store.FollowRedone(str.Id);
                                    var diff = redoneResult.diff;
                                    var item = redoneResult.item;

                                    if (diff > 0)
                                    {
                                        item = _doc.Store.GetItemCleanStart(transaction, new ID(item.Id.Client, item.Id.Clock + diff)) as Item;
                                    }

                                    if (item.Length > len)
                                    {
                                        _doc.Store.GetItemCleanStart(transaction, new ID(item.Id.Client, endClock));
                                    }

                                    str = it = item as Item;
                                }

                                if (!it.Deleted && InScope(it))
                                {
                                    itemsToDelete.Add(it);
                                }
                            }

                            return true;
                        });
                    }
                }

                stackItem.DeleteSet.IterateDeletedStructs(transaction, str =>
                {
                    var id = str.Id;
                    var clock = id.Clock;
                    var client = id.Client;

                    if (!stackItem.BeforeState.TryGetValue(client, out var startClock))
                    {
                        startClock = 0;
                    }

                    if (!stackItem.AfterState.TryGetValue(client, out var endClock))
                    {
                        endClock = 0;
                    }

                    if (str is Item item &&
                        InScope(item) &&
                        // Never redo structs in [stackItem.start, stackItem.start + stackItem.end), because they were created and deleted in the same capture interval.
                        !(clock >= startClock && clock < endClock))
                    {
                        itemsToRedo.Add(item);
                    }

                    return true;
                });

                foreach (var str in itemsToRedo)
                {
                    performedChange |= transaction.RedoItem(str, itemsToRedo) != null;
                }

                // We want to delete in reverse order so that children are deleted before
                // parents, so we have more information available when items are filtered.
                for (int i = itemsToDelete.Count - 1; i >= 0; i--)
                {
                    var item = itemsToDelete[i];
                    if (_deleteFilter(item))
                    {
                        item.Delete(transaction);
                        performedChange = true;
                    }
                }

                result = stackItem;
            }

            foreach (var kvp in transaction.Changed)
            {
                var type = kvp.Key;
                var subProps = kvp.Value;

                // Destroy search marker if necessary.
                if (subProps.Contains(null) && type is YArrayBase arr)
                {
                    arr.ClearSearchMarkers();
                }
            }
        }, origin: this);

        // Within an outer transaction, this one is not cleaned up yet: its types are still being collected.
        var changedParentTypes = _poppedChangedParentTypes ?? tr._changedParentTypes;
        _popTransaction = null;
        _poppedChangedParentTypes = null;

        if (result != null)
        {
            StackItemPopped?.Invoke(this, new StackEventArgs(result, eventType, changedParentTypes, this));
        }

        return result;
    }

    private static bool IsParentOf(object scopeEntry, Item child)
    {
        if (scopeEntry is YDoc)
        {
            // Whole-document scope: every item in this document is "in scope".
            return true;
        }

        var parent = scopeEntry as AbstractType;

        while (child != null)
        {
            if (child.Parent == parent)
            {
                return true;
            }

            child = (child.Parent as AbstractType)._item;
        }

        return false;
    }
}
