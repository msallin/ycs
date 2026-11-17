#nullable enable

using System.Collections;
using System.Runtime.CompilerServices;

namespace Ycs;

/// <summary>
/// The formatting attributes of a text operation: a few entries, immutable, searched linearly (one array; a dictionary
/// costs several times the bytes for the one to three attributes an operation has). Build one with a collection
/// expression, <c>[new("bold", true)]</c>, or take the implicit conversion of one pair, <c>("bold", true)</c>, or of a
/// <see cref="Dictionary{TKey, TValue}"/>. A key appears once; a later entry of the same key wins. On a
/// <see cref="Delta.Retain"/> and on <see cref="YText.Format"/>, a null value removes the attribute from the range.
/// </summary>
[CollectionBuilder(typeof(YAttributes), nameof(Create))]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1710:Identifiers should have correct suffix", Justification = "The name says what it holds.")]
public sealed class YAttributes : IReadOnlyDictionary<string, YValue>
{
    private static readonly KeyValuePair<string, YValue>[] NoEntries = [];

    private KeyValuePair<string, YValue>[] _entries;
    private int _count;

    /// <summary>No attributes.</summary>
    public static YAttributes Empty { get; } = new(0);

    internal YAttributes(int capacity)
    {
        _entries = capacity == 0 ? NoEntries : new KeyValuePair<string, YValue>[capacity];
    }

    public int Count => _count;

    public IEnumerable<string> Keys
    {
        get
        {
            for (var i = 0; i < _count; i++)
            {
                yield return _entries[i].Key;
            }
        }
    }

    public IEnumerable<YValue> Values
    {
        get
        {
            for (var i = 0; i < _count; i++)
            {
                yield return _entries[i].Value;
            }
        }
    }

    public YValue this[string key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException($"The operation has no attribute '{key}'.");

    internal KeyValuePair<string, YValue> EntryAt(int index) => _entries[index];

    /// <summary>The method a collection expression calls.</summary>
    public static YAttributes Create(ReadOnlySpan<KeyValuePair<string, YValue>> entries)
    {
        if (entries.Length == 0)
        {
            return Empty;
        }

        var result = new YAttributes(entries.Length);
        foreach (var entry in entries)
        {
            result.Set(entry.Key, entry.Value);
        }

        return result;
    }

    /// <summary>The attributes of <paramref name="entries"/>; a later entry of a key wins.</summary>
    public static YAttributes From(IEnumerable<KeyValuePair<string, YValue>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries is YAttributes same)
        {
            return same;
        }

        var result = new YAttributes(entries.TryGetNonEnumeratedCount(out var count) ? count : 4);
        foreach (var entry in entries)
        {
            result.Set(entry.Key, entry.Value);
        }

        return result;
    }

    public static implicit operator YAttributes((string Key, YValue Value) pair) => Create([new(pair.Key, pair.Value)]);

    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(entries))]
    public static implicit operator YAttributes?(Dictionary<string, YValue>? entries) => entries == null ? null : From(entries);

    /// <summary>Adds an attribute; the caller adds each key once.</summary>
    internal void Add(string key, YValue value) => _entries[_count++] = new KeyValuePair<string, YValue>(key, value);

    private void Set(string key, YValue value)
    {
        ArgumentNullException.ThrowIfNull(key);
        for (var i = 0; i < _count; i++)
        {
            if (string.Equals(_entries[i].Key, key, StringComparison.Ordinal))
            {
                _entries[i] = new KeyValuePair<string, YValue>(key, value);
                return;
            }
        }

        if (_count == _entries.Length)
        {
            // Only From over a source whose size is not known gets here.
            Array.Resize(ref _entries, Math.Max(4, _count * 2));
        }

        Add(key, value);
    }

    public bool ContainsKey(string key) => TryGetValue(key, out _);

    public bool TryGetValue(string key, out YValue value)
    {
        for (var i = 0; i < _count; i++)
        {
            if (string.Equals(_entries[i].Key, key, StringComparison.Ordinal))
            {
                value = _entries[i].Value;
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
            yield return _entries[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
