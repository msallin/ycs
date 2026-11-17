
using System.Collections;

namespace Ycs;

/// <summary>
/// <see cref="Transaction.Changed"/>: the changed types with their changed keys, enumerated in the
/// order they were added, as a JavaScript Map (observers are called in that order). A removed type
/// (one deleted in the transaction) leaves a hole that enumeration skips, so removing is O(1) and a
/// type added later does not take its slot.
/// </summary>
internal sealed partial class ChangedTypes : IDictionary<YType, ISet<string?>>
{
    private readonly Dictionary<YType, int> _index = new();

    // Holes have a null key.
    private List<KeyValuePair<YType, ISet<string?>>> _entries = new();

    public int Count => _index.Count;

    public bool IsReadOnly => false;

    public ICollection<YType> Keys => Pairs().Select(static kvp => kvp.Key).ToList();

    public ICollection<ISet<string?>> Values => Pairs().Select(static kvp => kvp.Value).ToList();

    // The interface type is the point: ChangedTypes is two IEnumerable<KeyValuePair<,>> and a LINQ call on it is ambiguous.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859:Use concrete types when possible", Justification = "Selects which of the two enumerable interfaces LINQ binds to.")]
    private IEnumerable<KeyValuePair<YType, ISet<string?>>> Pairs() => this;

    public ISet<string?> this[YType key]
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

    public void Add(YType key, ISet<string?> value)
    {
        _index.Add(key, _entries.Count);
        _entries.Add(new(key, value));
    }

    public bool TryGetValue(YType key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out ISet<string?> value)
    {
        if (_index.TryGetValue(key, out var i))
        {
            value = _entries[i].Value;
            return true;
        }

        value = null;
        return false;
    }

    public bool ContainsKey(YType key) => _index.ContainsKey(key);

    public bool Remove(YType key)
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

    IEnumerator<KeyValuePair<YType, ISet<string?>>> IEnumerable<KeyValuePair<YType, ISet<string?>>>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    void ICollection<KeyValuePair<YType, ISet<string?>>>.Add(KeyValuePair<YType, ISet<string?>> item) => Add(item.Key, item.Value);

    bool ICollection<KeyValuePair<YType, ISet<string?>>>.Contains(KeyValuePair<YType, ISet<string?>> item) =>
        TryGetValue(item.Key, out var value) && EqualityComparer<ISet<string?>>.Default.Equals(value, item.Value);

    void ICollection<KeyValuePair<YType, ISet<string?>>>.CopyTo(KeyValuePair<YType, ISet<string?>>[] array, int arrayIndex)
    {
        foreach (var kvp in this)
        {
            array[arrayIndex++] = kvp;
        }
    }

    bool ICollection<KeyValuePair<YType, ISet<string?>>>.Remove(KeyValuePair<YType, ISet<string?>> item) =>
        ((ICollection<KeyValuePair<YType, ISet<string?>>>)this).Contains(item) && Remove(item.Key);

    /// <summary>Visits the entries in order, also those added meanwhile (as a JavaScript Map's forEach).</summary>
    public struct Enumerator : IEnumerator<KeyValuePair<YType, ISet<string?>>>
    {
        private readonly List<KeyValuePair<YType, ISet<string?>>> _entries;
        private int _next;

        internal Enumerator(List<KeyValuePair<YType, ISet<string?>>> entries)
        {
            _entries = entries;
            _next = 0;
            Current = default;
        }

        public KeyValuePair<YType, ISet<string?>> Current { get; private set; }

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
