#nullable enable

using System.Collections;

namespace Ycs;

/// <summary>
/// The attributes of one delta operation: a few entries in two arrays, searched linearly. A dictionary costs several times
/// the bytes for the one to three attributes an operation has, and every operation of a delta has its own.
/// </summary>
internal sealed class AttributeSet : IReadOnlyDictionary<string, YValue>
{
    private readonly string[] _keys;
    private readonly YValue[] _values;
    private int _count;

    public AttributeSet(int capacity)
    {
        _keys = new string[capacity];
        _values = new YValue[capacity];
    }

    public int Count => _count;

    public IEnumerable<string> Keys => new ArraySegment<string>(_keys, 0, _count);

    public IEnumerable<YValue> Values => new ArraySegment<YValue>(_values, 0, _count);

    public YValue this[string key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException($"The operation has no attribute '{key}'.");

    /// <summary>Adds an attribute; the caller adds each key once.</summary>
    public void Add(string key, YValue value)
    {
        _keys[_count] = key;
        _values[_count++] = value;
    }

    public bool ContainsKey(string key) => TryGetValue(key, out _);

    public bool TryGetValue(string key, out YValue value)
    {
        for (var i = 0; i < _count; i++)
        {
            if (string.Equals(_keys[i], key, StringComparison.Ordinal))
            {
                value = _values[i];
                return true;
            }
        }

        value = default;
        return false;
    }

    public IEnumerator<KeyValuePair<string, YValue>> GetEnumerator()
    {
        for (var i = 0; i < _count; i++)
        {
            yield return new KeyValuePair<string, YValue>(_keys[i], _values[i]);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// The items an <see cref="Delta.InsertItems"/> holds: the stored values of the inserted content, as <see cref="YValue"/> when
/// read. A list of <see cref="YValue"/> would be three times the bytes of the stored values.
/// </summary>
internal sealed class StoredItems : IReadOnlyList<YValue>
{
    private object?[] _items = new object?[1];
    private int _count;

    public int Count => _count;

    public YValue this[int index] => index >= 0 && index < _count
        ? YValue.FromStored(_items[index])
        : throw new ArgumentOutOfRangeException(nameof(index), index, $"The operation inserts {_count} items.");

    public void AddRange(IReadOnlyList<object> values)
    {
        if (_count + values.Count > _items.Length)
        {
            Array.Resize(ref _items, Math.Max(_count + values.Count, _items.Length * 2));
        }

        for (var i = 0; i < values.Count; i++)
        {
            _items[_count++] = values[i];
        }
    }

    public IEnumerator<YValue> GetEnumerator()
    {
        for (var i = 0; i < _count; i++)
        {
            yield return YValue.FromStored(_items[i]);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
