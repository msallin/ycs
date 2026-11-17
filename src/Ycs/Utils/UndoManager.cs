#nullable enable

using System.Runtime.CompilerServices;

namespace Ycs;

public sealed partial class UndoManager
{
    // The tracked types (each with its subtree). Yjs lists the document itself in its scope; here it is _wholeDocument.
    private readonly List<YType> _scope = new List<YType>();
    private bool _wholeDocument;
    private readonly Func<ItemView, bool> _deleteFilter;
    private readonly Func<Transaction, bool> _captureTransaction;
    private readonly HashSet<object?> _trackedOrigins;
    private readonly YDoc _doc;
    private readonly TimeSpan _captureTimeout;
    private readonly TimeProvider _timeProvider;

    // The top of a stack is its last element, as in Yjs.
    private List<StackItem> _undoStack = new List<StackItem>();
    private List<StackItem> _redoStack = new List<StackItem>();

    private bool _undoing;
    private bool _redoing;
    // The time of the last change that was captured on its own, null after StopCapturing and before the first.
    private long? _lastChange;
    private bool _disposed;

    // The transaction of the running Undo or Redo, whether it was cleaned up, and its changed parent types
    // as they were at its 'AfterTransaction' (the transaction object is reused once it has ended).
    private Transaction? _popTransaction;
    private bool _popCleanedUp;
    private IReadOnlyDictionary<YType, IReadOnlyList<YEvent>>? _poppedChangedParentTypes;

    // An Undo or Redo inside an outer transaction: the popped events got this dictionary, which is filled at
    // that transaction's 'AfterTransaction', as Yjs hands out the transaction's live map.
    private Transaction? _outerTransaction;
    private Dictionary<YType, IReadOnlyList<YEvent>>? _outerChangedParentTypes;

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

    private void OnDocDestroyed(object? sender, EventArgs e) => Dispose();

    private void OnAfterTransaction(object? sender, Transaction transaction)
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
                _outerChangedParentTypes![type] = ReadOnly(events);
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

        var now = _timeProvider.GetTimestamp();
        var didAdd = false;
        if (_lastChange is { } last && _timeProvider.GetElapsedTime(last, now) < _captureTimeout && stack.Count > 0 && !undoing && !redoing)
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
        handler?.Invoke(this, new UndoStackEventArgs(stack[^1], undoing ? UndoOperation.Redo : UndoOperation.Undo, CopyChangedParentTypes(transaction, handler)!, transaction.Origin));
    }

    private bool ScopeChanged(Transaction transaction)
    {
        if (_wholeDocument)
        {
            return true;
        }

        foreach (var type in _scope)
        {
            if (transaction._changedParentTypes.ContainsKey(type))
            {
                return true;
            }
        }

        return false;
    }

    private bool InScope(ItemRef item)
    {
        if (_wholeDocument)
        {
            return true;
        }

        foreach (var type in _scope)
        {
            if (IsParentOf(type, item))
            {
                return true;
            }
        }

        return false;
    }

    // A stack event outlives the transaction; a copy is made only for a subscribed event.
    private static Dictionary<YType, IReadOnlyList<YEvent>>? CopyChangedParentTypes(Transaction transaction, Delegate? handler)
    {
        if (handler == null)
        {
            return null;
        }

        var copy = new Dictionary<YType, IReadOnlyList<YEvent>>(transaction._changedParentTypes.Count);
        foreach (var (type, events) in transaction._changedParentTypes)
        {
            copy[type] = ReadOnly(events);
        }

        return copy;
    }

    private static IReadOnlyList<YEvent> ReadOnly(IList<YEvent> events) => events as IReadOnlyList<YEvent> ?? events.ToArray();

    private StackItem? PopStackItem(List<StackItem> stack, UndoOperation eventType)
    {
        Transaction? tr = null;

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
                                    redone = store.GetItemCleanStart(transaction, new ItemId(redone.Id.Client, redone.Id.Clock + diff));
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
                        if (_deleteFilter(ItemView.Of(transaction.Doc, item)))
                        {
                            item.Delete(transaction);
                            performedChange = true;
                        }
                    }

                    CurrentStackItem = performedChange ? stackItem : null;
                }

                foreach (var (type, subProps) in transaction.LiveChanged)
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
                        _outerChangedParentTypes = new Dictionary<YType, IReadOnlyList<YEvent>>();
                    }

                    changedParentTypes = _outerChangedParentTypes;
                }

                _popTransaction = null;
                handler(this, new UndoStackEventArgs(result, eventType, changedParentTypes!, this));
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

    private static bool IsDeletedByStack(List<StackItem> stack, ItemId id)
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

        var parentItem = ((YType)item.Parent)._item;
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

        var parentType = parentItem.IsNull ? (YType)item.Parent : parentItem.TypeOf;

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
                parentType.TryGetMapItem(item.ParentSub, out left);
            }

            // A left neighbour in another parent would give the copy an origin that misleads remote peers.
            if (!left.IsNull && left.Parent != parentType)
            {
                parentType.TryGetMapItem(item.ParentSub, out left);
            }
        }

        var nextId = new ItemId(ownClientId, store.GetState(ownClientId));
        var redoneItem = ItemRef.NewCopyOf(store.Slab, nextId, left, right, parentType, item.ParentSub, item);
        item.Redone = nextId;
        redoneItem.KeepItemAndParents(true);
        redoneItem.Integrate(transaction, 0);
        return redoneItem;
    }

    private static bool IsParentOf(YType parent, ItemRef child)
    {
        for (var type = child.IsNull ? null : child.Parent as YType; type != null; type = type.ParentType)
        {
            if (type == parent)
            {
                return true;
            }
        }

        return false;
    }
}
