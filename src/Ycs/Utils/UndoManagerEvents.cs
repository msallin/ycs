#nullable enable

using System.Diagnostics.CodeAnalysis;

namespace Ycs;

/// <summary>Whether a stack item was undone (it came from the undo stack) or redone.</summary>
public enum UndoOperation
{
    Undo,
    Redo,
}

/// <summary>
/// What <see cref="UndoManager.StackItemAdded"/>, <see cref="UndoManager.StackItemUpdated"/> and
/// <see cref="UndoManager.StackItemPopped"/> report. <paramref name="Operation"/> is the stack the item belongs
/// to (an item added by an undo goes to the redo stack: <see cref="UndoOperation.Redo"/>), or for a popped item what
/// was done to it. <paramref name="ChangedParentTypes"/> holds each changed type with its events.
/// </summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "The payload of an event, not an EventArgs subclass.")]
public readonly record struct UndoStackEventArgs(
    StackItem StackItem,
    UndoOperation Operation,
    IReadOnlyDictionary<YType, IReadOnlyList<YEvent>> ChangedParentTypes,
    object? Origin);

/// <summary>What <see cref="UndoManager.StackCleared"/> reports: which of the two stacks were emptied.</summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "The payload of an event, not an EventArgs subclass.")]
public readonly record struct UndoStackClearedEventArgs(bool UndoStackCleared, bool RedoStackCleared);
