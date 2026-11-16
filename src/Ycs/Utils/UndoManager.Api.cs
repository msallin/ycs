// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

public partial class UndoManager
{
    public UndoManager(AbstractType typeScope, UndoManagerOptions options = null)
        : this(new object[] { typeScope ?? throw new ArgumentNullException(nameof(typeScope)) }, options)
    {
    }

    /// <summary>Tracks every change of <paramref name="doc"/>, whichever type it touched.</summary>
    public UndoManager(YDoc doc, UndoManagerOptions options = null)
        : this(new object[] { doc ?? throw new ArgumentNullException(nameof(doc)) }, options)
    {
    }

    /// <summary>
    /// Tracks changes of the given types (and their children) and, for a <see cref="YDoc"/> entry, of the whole document.
    /// </summary>
    public UndoManager(IReadOnlyList<object> typeScope, UndoManagerOptions options = null)
    {
        ArgumentNullException.ThrowIfNull(typeScope);
        _doc = options?.Doc ?? InferDoc(typeScope)
            ?? throw new ArgumentException("The document cannot be determined from an empty scope; set UndoManagerOptions.Doc.", nameof(typeScope));

        AddToScope(typeScope);
        _captureTimeout = options?.CaptureTimeout ?? 500;
        _captureTransaction = options?.CaptureTransaction ?? (_ => true);
        _deleteFilter = options?.DeleteFilter ?? (_ => true);
        _trackedOrigins = options?.TrackedOrigins ?? new HashSet<object> { null };
        _trackedOrigins.Add(this);
        IgnoreRemoteMapChanges = options?.IgnoreRemoteMapChanges ?? false;
        _lastChange = DateTime.MinValue;

        _doc.AfterTransaction += OnAfterTransaction;
        _doc.Destroyed += OnDocDestroyed;
    }

    public event EventHandler<StackEventArgs> StackItemAdded;

    /// <summary>Fires when a captured change was merged into the stack item on top of the undo stack.</summary>
    public event EventHandler<StackEventArgs> StackItemUpdated;

    public event EventHandler<StackEventArgs> StackItemPopped;

    /// <summary>Fires inside the transaction of <see cref="Clear(bool, bool)"/> when it empties a stack.</summary>
    public event EventHandler<StackClearedEventArgs> StackCleared;

    public YDoc Doc => _doc;

    /// <summary>The tracked <see cref="AbstractType"/> and <see cref="YDoc"/> entries.</summary>
    public IReadOnlyList<object> Scope => _scope;

    /// <summary>The undo stack, oldest first; the last item is undone next.</summary>
    public IReadOnlyList<StackItem> UndoStack => _undoStack;

    /// <summary>The redo stack, oldest first; the last item is redone next.</summary>
    public IReadOnlyList<StackItem> RedoStack => _redoStack;

    /// <summary>
    /// The stack item being undone or redone, from the moment it changed the document until its
    /// <see cref="StackItemPopped"/> handlers returned; null otherwise. The handlers of
    /// <see cref="StackItemAdded"/> for the opposite stack can carry its <see cref="StackItem.Meta"/> over.
    /// </summary>
    public StackItem CurrentStackItem { get; private set; }

    /// <summary>See <see cref="UndoManagerOptions.IgnoreRemoteMapChanges"/>.</summary>
    public bool IgnoreRemoteMapChanges { get; set; }

    public void AddToScope(object scopeEntry)
    {
        AddToScope(new[] { scopeEntry });
    }

    /// <summary>
    /// Extends the scope. Each entry must be an <see cref="AbstractType"/> or a <see cref="YDoc"/>; one already
    /// in the scope is skipped.
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

    public void AddTrackedOrigin(object origin) => _trackedOrigins.Add(origin);

    public void RemoveTrackedOrigin(object origin) => _trackedOrigins.Remove(origin);

    public bool CanUndo() => _undoStack.Count > 0;

    public bool CanRedo() => _redoStack.Count > 0;

    public void Clear() => Clear(true, true);

    /// <summary>
    /// Empties the undo and/or the redo stack and lets the deleted items they kept be garbage collected.
    /// </summary>
    public void Clear(bool clearUndoStack, bool clearRedoStack)
    {
        if (!((clearUndoStack && CanUndo()) || (clearRedoStack && CanRedo())))
        {
            return;
        }

        _doc.Transact(tr =>
        {
            if (clearUndoStack)
            {
                foreach (var item in _undoStack)
                {
                    ClearStackItem(tr, item);
                }

                _undoStack = new List<StackItem>();
            }

            if (clearRedoStack)
            {
                foreach (var item in _redoStack)
                {
                    ClearStackItem(tr, item);
                }

                _redoStack = new List<StackItem>();
            }

            StackCleared?.Invoke(this, new StackClearedEventArgs(clearUndoStack, clearRedoStack));
        });
    }

    /// <summary>
    /// Changes are merged into one stack item while they come within the capture timeout of each other;
    /// after this call the next change starts a new one.
    /// </summary>
    public void StopCapturing()
    {
        _lastChange = DateTime.MinValue;
    }

    /// <summary>Undoes the last stack item that changes the document.</summary>
    /// <returns>The stack item, or null when none changed anything.</returns>
    public StackItem Undo()
    {
        _undoing = true;
        try
        {
            return PopStackItem(_undoStack, OperationType.Undo);
        }
        finally
        {
            _undoing = false;
        }
    }

    /// <summary>Redoes the last undone stack item that changes the document.</summary>
    /// <returns>The stack item, or null when none changed anything.</returns>
    public StackItem Redo()
    {
        _redoing = true;
        try
        {
            return PopStackItem(_redoStack, OperationType.Redo);
        }
        finally
        {
            _redoing = false;
        }
    }

    /// <summary>
    /// Unhooks this UndoManager from its document. Safe to call more than once.
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
}
