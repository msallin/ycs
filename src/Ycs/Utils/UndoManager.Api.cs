#nullable enable

namespace Ycs;

/// <summary>
/// Tracks the changes of a document, or of some of its types, so that they can be undone and redone;
/// Yjs 13.6's <c>UndoManager</c>. Fires <see cref="StackItemAdded"/> when a stack item was added to either the undo or
/// the redo stack, <see cref="StackItemUpdated"/> when a change was merged into the last one,
/// <see cref="StackItemPopped"/> when one was popped (restore what was saved in <see cref="StackItem.Meta"/> there), and
/// <see cref="StackCleared"/> when <see cref="Clear"/> emptied a stack. Disposing it unhooks it from the document.
/// </summary>
public sealed partial class UndoManager : IDisposable
{
    /// <summary>Tracks the changes of <paramref name="scope"/> and of the types inside it.</summary>
    /// <exception cref="ArgumentException">The type is not part of a document.</exception>
    public UndoManager(YType scope, UndoManagerOptions? options = null)
        : this(DocOf(scope), false, options)
    {
        AddToScope(scope);
    }

    /// <summary>Tracks the changes of the given types and of the types inside them.</summary>
    /// <exception cref="ArgumentException">The first type is not part of a document, or there is none.</exception>
    public UndoManager(IEnumerable<YType> scope, UndoManagerOptions? options = null)
        : this(DocOf(scope), false, options)
    {
        AddToScope(scope);
    }

    /// <summary>Tracks every change of <paramref name="doc"/>, whichever type it touches.</summary>
    public UndoManager(YDoc doc, UndoManagerOptions? options = null)
        : this(doc, true, options)
    {
    }

    private UndoManager(YDoc doc, bool wholeDocument, UndoManagerOptions? options)
    {
        ArgumentNullException.ThrowIfNull(doc);
        _doc = doc;
        _wholeDocument = wholeDocument;
        _captureTimeout = options?.CaptureTimeout ?? UndoManagerOptions.DefaultCaptureTimeout;
        _timeProvider = options?.TimeProvider ?? TimeProvider.System;
        _captureTransaction = options?.CaptureTransaction ?? (static _ => true);
        _deleteFilter = options?.DeleteFilter ?? (static _ => true);
        _trackedOrigins = options?.TrackedOrigins is { } origins ? new HashSet<object?>(origins) : new HashSet<object?> { null };
        _trackedOrigins.Add(this);
        IgnoreRemoteMapChanges = options?.IgnoreRemoteMapChanges ?? false;

        _doc.AfterTransaction += OnAfterTransaction;
        _doc.Destroyed += OnDocDestroyed;
    }

    public event EventHandler<UndoStackEventArgs>? StackItemAdded;

    /// <summary>Fires when a captured change was merged into the stack item on top of the undo stack.</summary>
    public event EventHandler<UndoStackEventArgs>? StackItemUpdated;

    public event EventHandler<UndoStackEventArgs>? StackItemPopped;

    /// <summary>Fires inside the transaction of <see cref="Clear"/> when it empties a stack.</summary>
    public event EventHandler<UndoStackClearedEventArgs>? StackCleared;

    public YDoc Doc => _doc;

    /// <summary>
    /// The tracked types. A manager made for a document (or extended with one: <see cref="AddToScope(YDoc)"/>)
    /// tracks all of it, whatever this lists; Yjs lists the document itself here.
    /// </summary>
    public IReadOnlyList<YType> Scope => _scope;

    /// <summary>Whether every change of <see cref="Doc"/> is tracked, not only those of <see cref="Scope"/>.</summary>
    public bool TracksWholeDocument => _wholeDocument;

    /// <summary>The undo stack, oldest first; the last item is undone next.</summary>
    public IReadOnlyList<StackItem> UndoStack => _undoStack;

    /// <summary>The redo stack, oldest first; the last item is redone next.</summary>
    public IReadOnlyList<StackItem> RedoStack => _redoStack;

    /// <summary>
    /// The stack item being undone or redone, from the moment it changed the document until its
    /// <see cref="StackItemPopped"/> handlers returned; null otherwise. The handlers of
    /// <see cref="StackItemAdded"/> for the opposite stack can carry its <see cref="StackItem.Meta"/> over.
    /// </summary>
    public StackItem? CurrentStackItem { get; private set; }

    public bool CanUndo => _undoStack.Count > 0;

    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>See <see cref="UndoManagerOptions.IgnoreRemoteMapChanges"/>.</summary>
    public bool IgnoreRemoteMapChanges { get; set; }

    /// <summary>Extends the scope by a type (and the types inside it); one already in the scope is skipped.</summary>
    public void AddToScope(YType type)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(type);
        if (!_scope.Contains(type))
        {
            _scope.Add(type);
        }
    }

    public void AddToScope(IEnumerable<YType> types)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(types);
        foreach (var type in types)
        {
            AddToScope(type);
        }
    }

    /// <summary>Extends the scope by the whole document (<see cref="TracksWholeDocument"/>).</summary>
    /// <exception cref="ArgumentException">It is not the document of this manager.</exception>
    public void AddToScope(YDoc doc)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(doc);
        if (doc != _doc)
        {
            throw new ArgumentException("An undo manager tracks one document.", nameof(doc));
        }

        _wholeDocument = true;
    }

    /// <summary>
    /// Captures the transactions of an origin as well (by equality, or by type: a transaction whose origin is of
    /// the given <see cref="Type"/>). Null is the origin of a transaction made without one.
    /// </summary>
    public void AddTrackedOrigin(object? origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _trackedOrigins.Add(origin);
    }

    public void RemoveTrackedOrigin(object? origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _trackedOrigins.Remove(origin);
    }

    /// <summary>
    /// Empties the undo and/or the redo stack and lets the deleted items they kept be garbage collected.
    /// </summary>
    public void Clear(bool undoStack = true, bool redoStack = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!((undoStack && CanUndo) || (redoStack && CanRedo)))
        {
            return;
        }

        _doc.Transact(tr =>
        {
            if (undoStack)
            {
                foreach (var item in _undoStack)
                {
                    ClearStackItem(tr, item);
                }

                _undoStack = new List<StackItem>();
            }

            if (redoStack)
            {
                foreach (var item in _redoStack)
                {
                    ClearStackItem(tr, item);
                }

                _redoStack = new List<StackItem>();
            }

            StackCleared?.Invoke(this, new UndoStackClearedEventArgs(undoStack, redoStack));
        });
    }

    /// <summary>
    /// Changes are merged into one stack item while they come within the capture timeout of each other;
    /// after this call the next change starts a new one.
    /// </summary>
    public void StopCapturing()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _lastChange = null;
    }

    /// <summary>Undoes the last stack item that changes the document.</summary>
    /// <returns>The stack item, or null when none changed anything.</returns>
    public StackItem? Undo()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _undoing = true;
        try
        {
            return PopStackItem(_undoStack, UndoOperation.Undo);
        }
        finally
        {
            _undoing = false;
        }
    }

    /// <summary>Redoes the last undone stack item that changes the document.</summary>
    /// <returns>The stack item, or null when none changed anything.</returns>
    public StackItem? Redo()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _redoing = true;
        try
        {
            return PopStackItem(_redoStack, UndoOperation.Redo);
        }
        finally
        {
            _redoing = false;
        }
    }

    /// <summary>Unhooks this manager from its document; the stacks and <see cref="CanUndo"/> stay readable, every member that changes it throws <see cref="ObjectDisposedException"/>. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _trackedOrigins.Remove(this);
        _doc.AfterTransaction -= OnAfterTransaction;
        _doc.Destroyed -= OnDocDestroyed;
    }

    private static YDoc DocOf(YType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.Doc ?? throw new ArgumentException("The type is not part of a document.", nameof(type));
    }

    private static YDoc DocOf(IEnumerable<YType> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        foreach (var type in types)
        {
            return DocOf(type);
        }

        throw new ArgumentException("The document cannot be determined from an empty scope.", nameof(types));
    }
}
