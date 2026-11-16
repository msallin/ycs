using System.Collections;

namespace Ycs;

/// <summary>
/// An object value of a document: read-only to everyone but the code that builds it (<see cref="Set"/>, during a decode or a
/// copy). It enumerates in the order the keys were first set, which is the order the encoding writes. A
/// <c>byte[]</c> member is handed out as a copy, as in <see cref="FrozenList"/>.
/// </summary>
internal sealed class FrozenMap : IDictionary<string, object?>, IReadOnlyDictionary<string, object?>
{
    private readonly Dictionary<string, object?> _entries;

    internal FrozenMap(int capacity)
    {
        _entries = new Dictionary<string, object?>(capacity);
    }

    /// <summary>How many levels of lists and objects this value has, itself included; set by whoever builds it.</summary>
    internal int Depth { get; set; } = 1;

    public int Count => _entries.Count;

    public bool IsReadOnly => true;

    public object? this[string key]
    {
        get => ValueFreezer.OnRead(_entries[key]);
        set => throw ValueFreezer.Frozen();
    }

    public ICollection<string> Keys => _entries.Keys;

    IEnumerable<string> IReadOnlyDictionary<string, object?>.Keys => _entries.Keys;

    public ICollection<object?> Values => Array.AsReadOnly(Snapshot());

    IEnumerable<object?> IReadOnlyDictionary<string, object?>.Values => Values;

    /// <summary>Sets <paramref name="key"/>, as a repeated key of an object in an update does: the last value wins, at the first position.</summary>
    internal void Set(string key, object? value) => _entries[key] = value;

    /// <summary>The entries as stored (see <see cref="FrozenList.RawAt"/>).</summary>
    internal IEnumerator RawEnumerator() => ((IEnumerable<KeyValuePair<string, object?>>)_entries).GetEnumerator();

    internal IEnumerable<KeyValuePair<string, object?>> EnumerateRaw() => _entries;

    internal bool TryGetRaw(string key, out object? value) => _entries.TryGetValue(key, out value);

    public bool TryGetValue(string key, out object? value)
    {
        if (_entries.TryGetValue(key, out var raw))
        {
            value = ValueFreezer.OnRead(raw);
            return true;
        }

        value = null;
        return false;
    }

    public bool ContainsKey(string key) => _entries.ContainsKey(key);

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        foreach (var entry in _entries)
        {
            yield return new KeyValuePair<string, object?>(entry.Key, ValueFreezer.OnRead(entry.Value));
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Contains(KeyValuePair<string, object?> item) =>
        _entries.TryGetValue(item.Key, out var raw) && Equals(raw, item.Value);

    public void CopyTo(KeyValuePair<string, object?>[] array, int arrayIndex)
    {
        foreach (var entry in this)
        {
            array[arrayIndex++] = entry;
        }
    }

    public void Add(string key, object? value) => throw ValueFreezer.Frozen();

    public void Add(KeyValuePair<string, object?> item) => throw ValueFreezer.Frozen();

    public void Clear() => throw ValueFreezer.Frozen();

    public bool Remove(string key) => throw ValueFreezer.Frozen();

    public bool Remove(KeyValuePair<string, object?> item) => throw ValueFreezer.Frozen();

    private object?[] Snapshot()
    {
        var values = new object?[_entries.Count];
        var i = 0;
        foreach (var raw in _entries.Values)
        {
            values[i++] = ValueFreezer.OnRead(raw);
        }

        return values;
    }
}
