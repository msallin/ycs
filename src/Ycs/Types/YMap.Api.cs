// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System.Collections;
using System.Diagnostics;

namespace Ycs;

/// <summary>
/// A shared map from string keys to <see cref="YValue"/>s. Setting a key inserts a copy of the value; a collection
/// initializer (<c>new YMap { ["a"] = 1 }</c>) builds a map that is not in a document yet.
/// </summary>
#pragma warning disable CA1710 // Named after Yjs' type, as YArray and YText.
public partial class YMap : IReadOnlyDictionary<string, YValue>
{
    /// <summary>
    /// Observes the events of this map (Yjs' <c>observe(f)</c>). Disposing the result unsubscribes.
    /// </summary>
    public IDisposable Observe(Action<YMapEvent> handler) => AddObserver(handler);

    public YMap()
    {
        _prelimContent = new Dictionary<string, object?>();
    }

    public YMap(IEnumerable<KeyValuePair<string, YValue>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var prelimContent = new Dictionary<string, object?>();
        foreach (var entry in entries)
        {
            prelimContent[entry.Key] = entry.Value.Stored();
        }

        _prelimContent = prelimContent;
    }

    public int Count => _prelimContent?.Count ?? _mapCount;

    /// <summary>The value of <paramref name="key"/>; Yjs' <c>set(key, value)</c> sets it.</summary>
    /// <exception cref="KeyNotFoundException">Getting: the map has no <paramref name="key"/>.</exception>
    public YValue this[string key]
    {
        get => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException($"The map has no key '{key}'.");
        set
        {
            ArgumentNullException.ThrowIfNull(key);
            SetRaw(key, value.Stored());
        }
    }

    /// <summary>Whether the map has <paramref name="key"/>, and its value (which may be null).</summary>
    public bool TryGetValue(string key, out YValue value)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_prelimContent != null)
        {
            var found = _prelimContent.TryGetValue(key, out var stored);
            value = YValue.FromStored(stored);
            return found;
        }

        if (TryTypeMapGet(key, out var raw))
        {
            value = YValue.FromStored(raw);
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// The value of <paramref name="key"/> as of <paramref name="snapshot"/> (Yjs' <c>typeMapGetSnapshot</c>); false when
    /// the key was not visible then. It only reads: an item merged across the snapshot's boundary is seen whole, as in Yjs.
    /// </summary>
    public bool TryGetValue(string key, Snapshot snapshot, out YValue value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (TryMapGetSnapshot(key, snapshot, out var raw))
        {
            value = YValue.FromStored(raw);
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Every key and value as of <paramref name="snapshot"/> (Yjs' <c>typeMapGetAllSnapshot</c>).</summary>
    public IReadOnlyDictionary<string, YValue> ToDictionary(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return MapAtSnapshot(snapshot);
    }

    public bool ContainsKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _prelimContent != null ? _prelimContent.ContainsKey(key) : _map.TryGetValue(key, out var val) && !val.Deleted;
    }

    /// <summary>Adds <paramref name="key"/> for a collection initializer.</summary>
    /// <exception cref="ArgumentException">The map has <paramref name="key"/> already.</exception>
    public void Add(string key, YValue value)
    {
        if (ContainsKey(key))
        {
            throw new ArgumentException($"The map has the key '{key}' already.", nameof(key));
        }

        this[key] = value;
    }

    /// <summary>Removes <paramref name="key"/>; false when the map had none. In a document it is a transaction either way, as Yjs' <c>delete</c>.</summary>
    public bool Remove(string key)
    {
        var existed = ContainsKey(key);
        if (Doc != null)
        {
            Doc.Transact((self: this, key), static (s, tr) => s.self.TypeMapDelete(tr, s.key));
        }
        else
        {
            Debug.Assert(_prelimContent != null);
            _prelimContent.Remove(key);
        }

        return existed;
    }

    /// <summary>
    /// Removes all elements from this YMap. Mirrors Yjs' <c>clear()</c>.
    /// </summary>
    public void Clear()
    {
        if (Doc != null)
        {
            Doc.Transact(tr =>
            {
                foreach (var key in TypeMapEnumerate().Select(kvp => kvp.Key).ToList())
                {
                    TypeMapDelete(tr, key);
                }
            });
        }
        else
        {
            Debug.Assert(_prelimContent != null);
            _prelimContent.Clear();
        }
    }

    public IEnumerable<string> Keys => EnumerateEntries().Select(static kvp => kvp.Key);

    public IEnumerable<YValue> Values => EnumerateEntries().Select(static kvp => YValue.FromStored(kvp.Value));

    /// <summary>
    /// Transforms this Shared Type to a JSON-compatible <see cref="IDictionary{TKey,TValue}"/>. Mirrors Yjs' <c>toJSON()</c>.
    /// </summary>
    public override object ToJson() => JsonTree(this);

    public YMap Clone() => (YMap)InternalClone();

    public IEnumerator<KeyValuePair<string, YValue>> GetEnumerator()
    {
        if (_prelimContent != null)
        {
            foreach (var kvp in _prelimContent)
            {
                yield return new KeyValuePair<string, YValue>(kvp.Key, YValue.FromStored(kvp.Value));
            }

            yield break;
        }

        foreach (var kvp in _map)
        {
            if (!kvp.Value.Deleted)
            {
                yield return new KeyValuePair<string, YValue>(kvp.Key, YValue.FromStored(ValueAt(kvp.Value.Content, kvp.Value.Length - 1)));
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Sets <paramref name="key"/> to a value as stored, which a document may keep without a copy.</summary>
    internal void SetRaw(string key, object? stored)
    {
        if (Doc != null)
        {
            Doc.Transact((self: this, key, stored), static (s, tr) => s.self.TypeMapSet(tr, s.key, s.stored));
        }
        else
        {
            Debug.Assert(_prelimContent != null);
            _prelimContent[key] = stored;
        }
    }

    private protected IEnumerable<KeyValuePair<string, object?>> EnumerateEntries() => _prelimContent ?? TypeMapEnumerateValues();
}
#pragma warning restore CA1710
