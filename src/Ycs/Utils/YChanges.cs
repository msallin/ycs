namespace Ycs;

/// <summary>
/// What an event changed (Yjs' <c>event.changes</c>): the added and deleted items, the list delta and the keys.
/// </summary>
public sealed class YChanges
{
    internal YChanges(
        IReadOnlySet<ItemView> added,
        IReadOnlySet<ItemView> deleted,
        IReadOnlyList<Delta> delta,
        IReadOnlyDictionary<string, KeyChange> keys)
    {
        Added = added;
        Deleted = deleted;
        Delta = delta;
        Keys = keys;
    }

    public IReadOnlySet<ItemView> Added { get; }

    public IReadOnlySet<ItemView> Deleted { get; }

    public IReadOnlyList<Delta> Delta { get; }

    public IReadOnlyDictionary<string, KeyChange> Keys { get; }
}
