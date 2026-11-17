using System.Collections;

namespace Ycs;

/// <summary>
/// An object value of a document: read-only to everyone but the code that builds it (<see cref="Set"/> and
/// <see cref="Adopt"/>, during a decode or a copy). It enumerates in the order the keys were first set, which is the order the
/// encoding writes. A <c>byte[]</c> member is handed out as a copy, as in <see cref="FrozenList"/>.
/// </summary>
internal sealed class FrozenMap : IDictionary<string, object?>, IReadOnlyDictionary<string, object?>
{
    // Up to this many keys a lookup compares them one by one; beyond it a dictionary of positions is built on first use.
    private const int LinearLookup = 8;

    private string[] _keys;
    private object?[] _values;
    private int _count;
    private Dictionary<string, int>? _index;

    internal FrozenMap(int capacity)
    {
        _keys = capacity == 0 ? [] : new string[capacity];
        _values = capacity == 0 ? [] : new object?[capacity];
    }

    /// <summary>How many levels of lists and objects this value has, itself included; set by whoever builds it.</summary>
    internal int Depth { get; set; } = 1;

    public int Count => _count;

    public bool IsReadOnly => true;

    public object? this[string key]
    {
        get => TryGetRaw(key, out var raw) ? ValueFreezer.OnRead(raw) : throw new KeyNotFoundException($"The object has no member '{key}'.");
        set => throw ValueFreezer.Frozen();
    }

    public ICollection<string> Keys => Array.AsReadOnly(_keys.AsSpan(0, _count).ToArray());

    IEnumerable<string> IReadOnlyDictionary<string, object?>.Keys => Keys;

    public ICollection<object?> Values => Array.AsReadOnly(Snapshot());

    IEnumerable<object?> IReadOnlyDictionary<string, object?>.Values => Values;

    /// <summary>Sets <paramref name="key"/>, as a repeated key of an object in an update does: the last value wins, at the first position.</summary>
    internal void Set(string key, object? value)
    {
        var at = IndexOf(key);
        if (at >= 0)
        {
            _values[at] = value;
        }
        else
        {
            Grow();
            _keys[_count] = key;
            _values[_count] = value;
            _index?.Add(key, _count);
            _count++;
        }
    }

    /// <summary>Takes <paramref name="keys"/> (distinct) and <paramref name="values"/> as the content, without a copy.</summary>
    internal void Adopt(string[] keys, object?[] values, int count)
    {
        _keys = keys;
        _values = values;
        _count = count;
    }

    internal object?[] RawValues => _values;

    internal void SetRawAt(int index, object? value) => _values[index] = value;

    /// <summary>Enumerates the entries as stored (see <see cref="FrozenList.RawAt"/>).</summary>
    internal IEnumerator RawEnumerator() => EnumerateRaw().GetEnumerator();

    internal IEnumerable<KeyValuePair<string, object?>> EnumerateRaw()
    {
        for (var i = 0; i < _count; i++)
        {
            yield return new KeyValuePair<string, object?>(_keys[i], _values[i]);
        }
    }

    internal bool TryGetRaw(string key, out object? value)
    {
        var at = IndexOf(key);
        value = at >= 0 ? _values[at] : null;
        return at >= 0;
    }

    private int IndexOf(string key)
    {
        if (_count <= LinearLookup)
        {
            for (var i = 0; i < _count; i++)
            {
                if (string.Equals(_keys[i], key, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        if (_index == null)
        {
            _index = new Dictionary<string, int>(_count);
            for (var i = 0; i < _count; i++)
            {
                _index[_keys[i]] = i;
            }
        }

        return _index.TryGetValue(key, out var at) ? at : -1;
    }

    private void Grow()
    {
        if (_count == _keys.Length)
        {
            var size = Math.Max(4, _count * 2);
            Array.Resize(ref _keys, size);
            Array.Resize(ref _values, size);
        }
    }

    public bool TryGetValue(string key, out object? value)
    {
        if (TryGetRaw(key, out var raw))
        {
            value = ValueFreezer.OnRead(raw);
            return true;
        }

        value = null;
        return false;
    }

    public bool ContainsKey(string key) => IndexOf(key) >= 0;

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        for (var i = 0; i < _count; i++)
        {
            yield return new KeyValuePair<string, object?>(_keys[i], ValueFreezer.OnRead(_values[i]));
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Contains(KeyValuePair<string, object?> item) =>
        TryGetRaw(item.Key, out var raw) && Equals(raw, item.Value);

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
        var values = new object?[_count];
        for (var i = 0; i < _count; i++)
        {
            values[i] = ValueFreezer.OnRead(_values[i]);
        }

        return values;
    }
}
