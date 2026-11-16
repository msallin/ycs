
using System.Collections;

namespace Ycs;

/// <summary>
/// <see cref="Transaction.Changed"/>: the changed types with their changed keys, enumerated in the
/// order they were added, as a JavaScript Map (observers are called in that order). A removed type
/// (one deleted in the transaction) leaves a hole that enumeration skips, so removing is O(1) and a
/// type added later does not take its slot.
/// </summary>
internal sealed partial class ChangedTypes : IDictionary<AbstractType, ISet<string?>>
{
    private readonly Dictionary<AbstractType, int> _index = new();

    // Holes have a null key.
    private List<KeyValuePair<AbstractType, ISet<string?>>> _entries = new();

    public int Count => _index.Count;

    public bool IsReadOnly => false;

    public ICollection<AbstractType> Keys => Pairs().Select(static kvp => kvp.Key).ToList();

    public ICollection<ISet<string?>> Values => Pairs().Select(static kvp => kvp.Value).ToList();

    // The interface type is the point: ChangedTypes is two IEnumerable<KeyValuePair<,>> and a LINQ call on it is ambiguous.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859:Use concrete types when possible", Justification = "Selects which of the two enumerable interfaces LINQ binds to.")]
    private IEnumerable<KeyValuePair<AbstractType, ISet<string?>>> Pairs() => this;

    public ISet<string?> this[AbstractType key]
    {
        get => _index.TryGetValue(key, out var i) ? _entries[i].Value : throw new KeyNotFoundException();
        set
        {
            if (_index.TryGetValue(key, out var i))
            {
                _entries[i] = new(key, value);
            }
            else
            {
                Add(key, value);
            }
        }
    }

    public void Add(AbstractType key, ISet<string?> value)
    {
        _index.Add(key, _entries.Count);
        _entries.Add(new(key, value));
    }

    public bool TryGetValue(AbstractType key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out ISet<string?> value)
    {
        if (_index.TryGetValue(key, out var i))
        {
            value = _entries[i].Value;
            return true;
        }

        value = null;
        return false;
    }

    public bool ContainsKey(AbstractType key) => _index.ContainsKey(key);

    public bool Remove(AbstractType key)
    {
        if (!_index.Remove(key, out var i))
        {
            return false;
        }

        _entries[i] = default;
        return true;
    }

    public void Clear()
    {
        _index.Clear();
        _entries.Clear();
    }

    /// <summary>Drops the storage after a large transaction.</summary>
    public void TrimExcess()
    {
        _index.TrimExcess();
        _entries = new();
    }

    public Enumerator GetEnumerator() => new(_entries);

    IEnumerator<KeyValuePair<AbstractType, ISet<string?>>> IEnumerable<KeyValuePair<AbstractType, ISet<string?>>>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    void ICollection<KeyValuePair<AbstractType, ISet<string?>>>.Add(KeyValuePair<AbstractType, ISet<string?>> item) => Add(item.Key, item.Value);

    bool ICollection<KeyValuePair<AbstractType, ISet<string?>>>.Contains(KeyValuePair<AbstractType, ISet<string?>> item) =>
        TryGetValue(item.Key, out var value) && EqualityComparer<ISet<string?>>.Default.Equals(value, item.Value);

    void ICollection<KeyValuePair<AbstractType, ISet<string?>>>.CopyTo(KeyValuePair<AbstractType, ISet<string?>>[] array, int arrayIndex)
    {
        foreach (var kvp in this)
        {
            array[arrayIndex++] = kvp;
        }
    }

    bool ICollection<KeyValuePair<AbstractType, ISet<string?>>>.Remove(KeyValuePair<AbstractType, ISet<string?>> item) =>
        ((ICollection<KeyValuePair<AbstractType, ISet<string?>>>)this).Contains(item) && Remove(item.Key);

    /// <summary>Visits the entries in order, also those added meanwhile (as a JavaScript Map's forEach).</summary>
    public struct Enumerator : IEnumerator<KeyValuePair<AbstractType, ISet<string?>>>
    {
        private readonly List<KeyValuePair<AbstractType, ISet<string?>>> _entries;
        private int _next;

        internal Enumerator(List<KeyValuePair<AbstractType, ISet<string?>>> entries)
        {
            _entries = entries;
            _next = 0;
            Current = default;
        }

        public KeyValuePair<AbstractType, ISet<string?>> Current { get; private set; }

        object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            while (_next < _entries.Count)
            {
                Current = _entries[_next++];
                if (Current.Key != null)
                {
                    return true;
                }
            }

            return false;
        }

        public void Reset() => _next = 0;

        public void Dispose()
        {
        }
    }
}
