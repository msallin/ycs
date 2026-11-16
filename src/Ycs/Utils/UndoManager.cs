// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

using System.Runtime.CompilerServices;

namespace Ycs;

/// <summary>
/// Options of an <see cref="UndoManager"/>; Yjs' <c>UndoManagerOptions</c>.
/// </summary>
public sealed class UndoManagerOptions
{
    /// <summary>Changes made within this many milliseconds of the last one are merged into one stack item.</summary>
    public int CaptureTimeout { get; set; } = 500;

    /// <summary>A transaction for which this returns false is not captured. Default: every transaction.</summary>
    public Func<Transaction, bool> CaptureTransaction { get; set; }

    /// <summary>An item for which this returns false is not deleted by an undo or redo. Default: every item.</summary>
    public Func<ItemView, bool> DeleteFilter { get; set; }

    /// <summary>
    /// The origins (or origin types, compared exactly) whose transactions are captured. Default: <c>{ null }</c>.
    /// The UndoManager adds itself to the set.
    /// </summary>
    public ISet<object> TrackedOrigins { get; set; }

    /// <summary>
    /// When true, an undo or redo overwrites a key of a map (or an attribute) that another change set
    /// since; by default it leaves such a key alone.
    /// </summary>
    public bool IgnoreRemoteMapChanges { get; set; }

    /// <summary>The document; only needed when the scope is empty.</summary>
    public YDoc Doc { get; set; }
}

/// <summary>
/// Fires <see cref="StackItemAdded"/> when a stack item was added to either the undo or the redo stack,
/// <see cref="StackItemUpdated"/> when a change was merged into the last one, <see cref="StackItemPopped"/>
/// when one was popped (restore what was saved in <see cref="StackItem.Meta"/> there), and
/// <see cref="StackCleared"/> when <see cref="Clear(bool, bool)"/> emptied a stack. Yjs 13.6's <c>UndoManager</c>.
/// </summary>
public partial class UndoManager
{
    public enum OperationType
    {
        Undo,
        Redo
    }

    public class StackEventArgs : EventArgs
    {
        internal StackEventArgs(StackItem item, OperationType type, IDictionary<AbstractType, IList<YEvent>> changedParentTypes, object origin)
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
    /// Payload of <see cref="StackCleared"/>: which of the two stacks were emptied.
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

    // Each entry is an AbstractType (its subtree is tracked) or a YDoc (the whole document is).
    private readonly List<object> _scope = new List<object>();
    private readonly Func<ItemView, bool> _deleteFilter;
    private readonly Func<Transaction, bool> _captureTransaction;
    private readonly ISet<object> _trackedOrigins;
    private readonly YDoc _doc;
    private readonly int _captureTimeout;

    // The top of a stack is its last element, as in Yjs.
    private List<StackItem> _undoStack = new List<StackItem>();
    private List<StackItem> _redoStack = new List<StackItem>();

    private bool _undoing;
    private bool _redoing;
    private DateTime _lastChange;
    private bool _destroyed;

    // The transaction of the running Undo or Redo, whether it was cleaned up, and its changed parent types
    // as they were at its 'AfterTransaction' (the transaction object is reused once it has ended).
    private Transaction _popTransaction;
    private bool _popCleanedUp;
    private IDictionary<AbstractType, IList<YEvent>> _poppedChangedParentTypes;

    // An Undo or Redo inside an outer transaction: the popped events got this dictionary, which is filled at
    // that transaction's 'AfterTransaction', as Yjs hands out the transaction's live map.
    private Transaction _outerTransaction;
    private Dictionary<AbstractType, IList<YEvent>> _outerChangedParentTypes;

    private static YDoc InferDoc(IReadOnlyList<object> scope) => scope.Count == 0 ? null : scope[0] switch
    {
        YDoc d => d,
        AbstractType at => at.Doc,
        _ => null,
    };

    private void ClearStackItem(Transaction tr, StackItem stackItem)
    {
        stackItem.Deletions.IterateDeletedStructs(tr, i =>
        {
            if (i.IsItem && InScope(i))
            {
                i.KeepItemAndParents(false);
            }

            return true;
        });
    }

    private void OnDocDestroyed(object sender, EventArgs e) => Destroy();

    private void OnAfterTransaction(object sender, Transaction transaction)
    {
        if (transaction == _popTransaction)
        {
            _popCleanedUp = true;
            _poppedChangedParentTypes = CopyChangedParentTypes(transaction, StackItemPopped);
        }

        if (transaction == _outerTransaction)
        {
            foreach (var (type, events) in transaction._changedParentTypes)
            {
                _outerChangedParentTypes[type] = events;
            }

            _outerTransaction = null;
            _outerChangedParentTypes = null;
        }

        if (!_captureTransaction(transaction) ||
            !ScopeChanged(transaction) ||
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
            // The next undo must not be merged into the last stack item.
            StopCapturing();
        }
        else if (!redoing)
        {
            Clear(false, true);
        }

        var insertions = new DeleteSet();
        foreach (var (client, endClock) in transaction.AfterState)
        {
            var startClock = transaction.BeforeState.TryGetValue(client, out var clock) ? clock : 0;
            if (endClock > startClock)
            {
                insertions.Add(client, startClock, endClock - startClock);
            }
        }

        var now = DateTime.UtcNow;
        var didAdd = false;
        if ((now - _lastChange).TotalMilliseconds < _captureTimeout && stack.Count > 0 && !undoing && !redoing)
        {
            var lastOp = stack[^1];
            // Both are sorted and merged: the transaction's by its cleanup, the stack item's since it was made.
            lastOp.Deletions.AddSortedAndMerged(transaction.DeleteSet);
            lastOp.Insertions.AddSortedAndMerged(insertions);
        }
        else
        {
            // The stack item keeps the transaction's delete set, which the transaction then does not reuse.
            stack.Add(new StackItem(transaction.TakeDeleteSet(), insertions));
            didAdd = true;
        }

        if (!undoing && !redoing)
        {
            _lastChange = now;
        }

        // Deleted structs must not be garbage collected while a stack item can restore them.
        transaction.DeleteSet.IterateDeletedStructs(transaction, i =>
        {
            if (i.IsItem && InScope(i))
            {
                i.KeepItemAndParents(true);
            }

            return true;
        });

        var handler = didAdd ? StackItemAdded : StackItemUpdated;
        handler?.Invoke(this, new StackEventArgs(stack[^1], undoing ? OperationType.Redo : OperationType.Undo, CopyChangedParentTypes(transaction, handler), transaction.Origin));
    }

    private bool ScopeChanged(Transaction transaction)
    {
        foreach (var entry in _scope)
        {
            if (entry == _doc || (entry is AbstractType at && transaction._changedParentTypes.ContainsKey(at)))
            {
                return true;
            }
        }

        return false;
    }

    private bool InScope(ItemRef item)
    {
        foreach (var entry in _scope)
        {
            if (entry == _doc || (entry is AbstractType type && IsParentOf(type, item)))
            {
                return true;
            }
        }

        return false;
    }

    // A stack event outlives the transaction; a copy is made only for a subscribed event.
    private static Dictionary<AbstractType, IList<YEvent>> CopyChangedParentTypes(Transaction transaction, Delegate handler) =>
        handler == null ? null : new Dictionary<AbstractType, IList<YEvent>>(transaction._changedParentTypes);

    private StackItem PopStackItem(List<StackItem> stack, OperationType eventType)
    {
        Transaction tr = null;

        // Reset here too: a Transact that threw left them set.
        _popTransaction = null;
        _popCleanedUp = false;
        _poppedChangedParentTypes = null;
        CurrentStackItem = null;

        try
        {
            _doc.Transact(transaction =>
            {
                tr = transaction;
                _popTransaction = transaction;
                var store = _doc.Store;
                transaction.MaterializeRunExtensions();

                while (stack.Count > 0 && CurrentStackItem == null)
                {
                    var stackItem = stack[^1];
                    stack.RemoveAt(stack.Count - 1);
                    // Enumerated in the order of adding, as Yjs' Set: a HashSet keeps it while nothing is removed, and nothing is.
                    var itemsToRedo = new HashSet<ItemRef>();
                    var itemsToDelete = new List<ItemRef>();
                    var performedChange = false;

                    stackItem.Insertions.IterateDeletedStructs(transaction, str =>
                    {
                        if (str.IsItem)
                        {
                            var item = str;
                            if (item.Redone != null)
                            {
                                var (redone, diff) = store.FollowRedone(str.Id);
                                if (diff > 0)
                                {
                                    redone = store.GetItemCleanStart(transaction, new ID(redone.Id.Client, redone.Id.Clock + diff));
                                }

                                // A garbage-collected copy counts as deleted.
                                item = redone.AsItem;
                            }

                            if (!item.IsNull && !item.Deleted && InScope(item))
                            {
                                itemsToDelete.Add(item);
                            }
                        }

                        return true;
                    });

                    stackItem.Deletions.IterateDeletedStructs(transaction, str =>
                    {
                        // Structs in the insertions were created and deleted in the same capture interval: never redo them.
                        if (str.IsItem && InScope(str) && !stackItem.Insertions.IsDeleted(str.Id))
                        {
                            itemsToRedo.Add(str);
                        }

                        return true;
                    });

                    foreach (var item in itemsToRedo)
                    {
                        performedChange = !RedoItem(transaction, item, itemsToRedo, stackItem.Insertions).IsNull || performedChange;
                    }

                    // Children are deleted before their parents, so the filter sees more of them.
                    for (int i = itemsToDelete.Count - 1; i >= 0; i--)
                    {
                        var item = itemsToDelete[i];
                        if (_deleteFilter(ItemView.Of(transaction.Doc, item.ToItem())))
                        {
                            item.Delete(transaction);
                            performedChange = true;
                        }
                    }

                    CurrentStackItem = performedChange ? stackItem : null;
                }

                foreach (var (type, subProps) in transaction.Changed)
                {
                    if (subProps.Contains(null) && type is YArrayBase arr)
                    {
                        arr.ClearSearchMarkers();
                    }
                }
            }, origin: this);

            var result = CurrentStackItem;
            var handler = StackItemPopped;
            if (result != null && handler != null)
            {
                var changedParentTypes = _poppedChangedParentTypes;
                if (!_popCleanedUp)
                {
                    if (_outerTransaction != tr)
                    {
                        _outerTransaction = tr;
                        _outerChangedParentTypes = new Dictionary<AbstractType, IList<YEvent>>();
                    }

                    changedParentTypes = _outerChangedParentTypes;
                }

                _popTransaction = null;
                handler(this, new StackEventArgs(result, eventType, changedParentTypes, this));
            }

            return result;
        }
        finally
        {
            _popTransaction = null;
            _popCleanedUp = false;
            _poppedChangedParentTypes = null;
            CurrentStackItem = null;
        }
    }

    private static bool IsDeletedByStack(List<StackItem> stack, ID id)
    {
        foreach (var stackItem in stack)
        {
            if (stackItem.Deletions.IsDeleted(id))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Integrates a copy of the deleted <paramref name="item"/> at its old place, and its deleted parents
    /// first when they are redone too; null when it cannot be redone. Yjs 13.6's <c>redoItem</c>.
    /// </summary>
    private ItemRef RedoItem(Transaction transaction, ItemRef item, HashSet<ItemRef> redoItems, DeleteSet itemsToDelete)
    {
        // Recurses once per level of deleted parents: an exception, not a crash, where the stack runs out.
        RuntimeHelpers.EnsureSufficientExecutionStack();
        var store = transaction.Doc.Store;
        var ownClientId = transaction.Doc.ClientId;
        if (item.Redone != null)
        {
            return store.GetItemCleanStart(transaction, item.Redone.Value);
        }

        var parentItem = ((AbstractType)item.Parent)._item;
        ItemRef left = default;
        ItemRef right;

        // The parent must be redone first.
        if (!parentItem.IsNull && parentItem.Deleted)
        {
            // It is redone only if it is redone anyway.
            if (parentItem.Redone == null && (!redoItems.Contains(parentItem) || RedoItem(transaction, parentItem, redoItems, itemsToDelete).IsNull))
            {
                return default;
            }

            while (parentItem.Redone != null)
            {
                parentItem = store.GetItemCleanStart(transaction, parentItem.Redone.Value);
            }
        }

        var parentType = parentItem.IsNull ? (AbstractType)item.Parent : ((ContentType)parentItem.Content).Type;

        if (item.ParentSub == null)
        {
            // A list item goes back to its old position: between the nearest neighbours, or their redone copies, in the parent.
            left = item.Left.AsItem;
            right = item;
            while (!left.IsNull)
            {
                var leftTrace = left;
                while (!leftTrace.IsNull && leftTrace.Parent != parentType)
                {
                    leftTrace = leftTrace.Redone == null ? default : store.GetItemCleanStart(transaction, leftTrace.Redone.Value);
                }

                if (!leftTrace.IsNull)
                {
                    left = leftTrace;
                    break;
                }

                left = left.Left.AsItem;
            }

            while (!right.IsNull)
            {
                var rightTrace = right;
                while (!rightTrace.IsNull && rightTrace.Parent != parentType)
                {
                    rightTrace = rightTrace.Redone == null ? default : store.GetItemCleanStart(transaction, rightTrace.Redone.Value);
                }

                if (!rightTrace.IsNull)
                {
                    right = rightTrace;
                    break;
                }

                right = right.Right.AsItem;
            }
        }
        else
        {
            right = default;
            if (!item.Right.IsNull && !IgnoreRemoteMapChanges)
            {
                // A later value of the key is replaced only when it was redone, or deleted by this undo or a stack
                // item; any other one is a change this UndoManager did not make, and the item is not redone.
                left = item;
                while (left.Right.IsItem &&
                    (left.Right.Redone != null || itemsToDelete.IsDeleted(left.Right.Id) || IsDeletedByStack(_undoStack, left.Right.Id) || IsDeletedByStack(_redoStack, left.Right.Id)))
                {
                    left = left.Right;
                    while (left.Redone != null)
                    {
                        left = store.GetItemCleanStart(transaction, left.Redone.Value);
                    }
                }

                if (!left.Right.IsNull)
                {
                    return default;
                }
            }
            else
            {
                left = parentType._map.GetValueOrDefault(item.ParentSub);
            }

            // A left neighbour in another parent would give the copy an origin that misleads remote peers.
            if (!left.IsNull && left.Parent != parentType)
            {
                left = parentType._map.GetValueOrDefault(item.ParentSub);
            }
        }

        var nextId = new ID(ownClientId, store.GetState(ownClientId));
        var redoneItem = ItemRef.New(nextId, left, right, parentType, item.ParentSub, item.Content.Copy());
        item.Redone = nextId;
        redoneItem.KeepItemAndParents(true);
        redoneItem.Integrate(transaction, 0);
        return redoneItem;
    }

    private static bool IsParentOf(AbstractType parent, ItemRef child)
    {
        for (var type = child.IsNull ? null : child.Parent as AbstractType; type != null; type = type.ParentType)
        {
            if (type == parent)
            {
                return true;
            }
        }

        return false;
    }
}
