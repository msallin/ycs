#nullable enable

namespace Ycs;

/// <summary>Options of an <see cref="UndoManager"/>; Yjs' <c>UndoManagerOptions</c>.</summary>
public sealed record UndoManagerOptions
{
    internal static readonly TimeSpan DefaultCaptureTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>Changes made within this long of the last one are merged into one stack item.</summary>
    public TimeSpan CaptureTimeout { get; init; } = DefaultCaptureTimeout;

    /// <summary>A transaction for which this returns false is not captured. Default: every transaction.</summary>
    public Func<Transaction, bool>? CaptureTransaction { get; init; }

    /// <summary>An item for which this returns false is not deleted by an undo or redo. Default: every item.</summary>
    public Func<ItemView, bool>? DeleteFilter { get; init; }

    /// <summary>
    /// The origins whose transactions are captured, compared by equality or, for a transaction whose origin is not
    /// null, by its type: a <see cref="Type"/> in the collection tracks every origin of that type. A null element is
    /// the origin of a transaction made without one. Default: <c>{ null }</c>. The manager adds itself, and takes a
    /// copy: <see cref="UndoManager.AddTrackedOrigin"/> changes the tracked ones later.
    /// </summary>
    public IReadOnlyCollection<object?>? TrackedOrigins { get; init; }

    /// <summary>
    /// When true, an undo or redo overwrites a key of a map (or an attribute) that another change set
    /// since; by default it leaves such a key alone.
    /// </summary>
    public bool IgnoreRemoteMapChanges { get; init; }

    /// <summary>What the capture timeout reads the time from; default <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider? TimeProvider { get; init; }
}
