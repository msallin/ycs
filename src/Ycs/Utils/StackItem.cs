#nullable enable

namespace Ycs;

/// <summary>
/// One undo or redo step: what its transactions inserted and what they deleted. Yjs' <c>StackItem</c>.
/// </summary>
public sealed class StackItem
{
    /// <summary>Metadata to save and restore with the step, like a selection range.</summary>
    public IDictionary<string, object> Meta { get; } = new Dictionary<string, object>();

    // The ranges the captured transactions created, and the ranges they deleted.
    internal DeleteSet Insertions;
    internal DeleteSet Deletions;

    internal StackItem(DeleteSet deletions, DeleteSet insertions)
    {
        Deletions = deletions;
        Insertions = insertions;
    }
}
