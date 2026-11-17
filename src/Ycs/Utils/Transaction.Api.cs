#nullable enable

using System.Collections.Frozen;

namespace Ycs;

public sealed partial class Transaction
{
    /// <summary>
    /// The Yjs instance.
    /// </summary>
    public YDoc Doc => _ended ? throw EndedException() : _doc;

    /// <summary>The origin the transaction was started with (Yjs' <c>transaction.origin</c>).</summary>
    public object? Origin => _ended ? throw EndedException() : _origin;

    /// <summary>
    /// Holds the state before the transaction started.
    /// </summary>
    public IReadOnlyDictionary<long, long> BeforeState => _ended ? throw EndedException() : _beforeState;

    /// <summary>
    /// Holds the state after the transaction (empty until its cleanup starts).
    /// </summary>
    public IReadOnlyDictionary<long, long> AfterState => _ended ? throw EndedException() : _afterState;

    /// <summary>
    /// All types that were directly modified (property added or child
    /// inserted/deleted). New types are not included in this Set.
    /// Maps from type to parentSubs (<c>null</c> for the list of children of a YArray).
    /// </summary>
    public IReadOnlyDictionary<YType, IReadOnlySet<string?>> Changed => _ended ? throw EndedException() : _changed;

    /// <summary>
    /// Stores the events for the types that observe also child elements.
    /// It is mainly used by 'observeDeep'.
    /// </summary>
    public IReadOnlyDictionary<YType, IReadOnlyList<YEvent>> ChangedParentTypes =>
        _ended ? throw EndedException() : _changedParentTypesView ??= new ChangedParentTypesView(_changedParentTypes);

    /// <summary>
    /// Stores meta information on the transaction (Yjs' <c>transaction.meta</c>, a mutable map for the observers).
    /// </summary>
    public IDictionary<string, object?> Meta => _ended ? throw EndedException() : _meta ??= new Dictionary<string, object?>();

    /// <summary>
    /// Whether this change originates from this doc. Applying an update makes the transaction it
    /// runs in non-local, as in Yjs.
    /// </summary>
    public bool Local
    {
        get => _ended ? throw EndedException() : _local;
        internal set => _local = value;
    }

    public IReadOnlySet<YDoc> SubdocsAdded => _ended ? throw EndedException() : (IReadOnlySet<YDoc>?)_subdocsAdded ?? EmptySubdocs;

    public IReadOnlySet<YDoc> SubdocsRemoved => _ended ? throw EndedException() : (IReadOnlySet<YDoc>?)_subdocsRemoved ?? EmptySubdocs;

    public IReadOnlySet<YDoc> SubdocsLoaded => _ended ? throw EndedException() : (IReadOnlySet<YDoc>?)_subdocsLoaded ?? EmptySubdocs;

    private static readonly FrozenSet<YDoc> EmptySubdocs = FrozenSet<YDoc>.Empty;

    private ChangedParentTypesView? _changedParentTypesView;

    // Changed with its sets mutable, for the library; an ended transaction throws as the public view does.
    internal ChangedTypes LiveChanged => _ended ? throw EndedException() : _changed;

    // The sets the transaction fills; created when first needed.
    internal HashSet<YDoc> AddedSubdocs => _subdocsAdded ??= new HashSet<YDoc>();

    internal HashSet<YDoc> RemovedSubdocs => _subdocsRemoved ??= new HashSet<YDoc>();

    internal HashSet<YDoc> LoadedSubdocs => _subdocsLoaded ??= new HashSet<YDoc>();

    /// <summary>Read-only view of the changed parent types, which a reused transaction keeps for its life.</summary>
    private sealed class ChangedParentTypesView(Dictionary<YType, IList<YEvent>> source) : IReadOnlyDictionary<YType, IReadOnlyList<YEvent>>
    {
        public int Count => source.Count;

        public IReadOnlyList<YEvent> this[YType key] => (IReadOnlyList<YEvent>)source[key];

        public IEnumerable<YType> Keys => source.Keys;

        public IEnumerable<IReadOnlyList<YEvent>> Values
        {
            get
            {
                foreach (var events in source.Values)
                {
                    yield return (IReadOnlyList<YEvent>)events;
                }
            }
        }

        public bool ContainsKey(YType key) => source.ContainsKey(key);

        public bool TryGetValue(YType key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out IReadOnlyList<YEvent> value)
        {
            if (source.TryGetValue(key, out var events))
            {
                value = (IReadOnlyList<YEvent>)events;
                return true;
            }

            value = null;
            return false;
        }

        public IEnumerator<KeyValuePair<YType, IReadOnlyList<YEvent>>> GetEnumerator()
        {
            foreach (var (type, events) in source)
            {
                yield return new(type, (IReadOnlyList<YEvent>)events);
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
