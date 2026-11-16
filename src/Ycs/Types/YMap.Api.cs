// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

using System.Collections;

namespace Ycs;

public partial class YMap
{
    public YMap()
        : this(null)
    {
        // Do nothing.
    }

    public YMap(IDictionary<string, object> entries)
    {
        _prelimContent = new Dictionary<string, object>();
        foreach (var entry in entries ?? Enumerable.Empty<KeyValuePair<string, object>>())
        {
            _prelimContent[entry.Key] = ValueFreezer.Detach(entry.Value);
        }
    }

    public int Count => _prelimContent?.Count ?? _mapCount;

    /// <summary>
    /// Returns the size of the YMap (count of key/value pairs). Mirrors Yjs' <c>size</c> getter;
    /// identical to <see cref="Count"/>, provided under both names for parity.
    /// </summary>
    public int Size => Count;

    /// <summary>The value of <paramref name="key"/>, or null when the map has none (Yjs: <c>undefined</c>); <see cref="TryGet"/> tells the two apart.</summary>
    public object Get(string key) => TryTypeMapGet(key, out var value) ? value : null;

    /// <summary>Whether the map has <paramref name="key"/>, and its value (which may be null).</summary>
    public bool TryGet(string key, out object value) => TryTypeMapGet(key, out value);

    public void Set(string key, object value)
    {
        if (Doc != null)
        {
            Doc.Transact((self: this, key, value), static (s, tr) => s.self.TypeMapSet(tr, s.key, s.value));
        }
        else
        {
            _prelimContent[key] = ValueFreezer.Detach(value);
        }
    }

    public void Delete(string key)
    {
        if (Doc != null)
        {
            Doc.Transact((self: this, key), static (s, tr) => s.self.TypeMapDelete(tr, s.key));
        }
        else
        {
            _prelimContent.Remove(key);
        }
    }

    public bool ContainsKey(string key)
    {
        return _map.TryGetValue(key, out var val) && !val.Deleted;
    }

    /// <summary>
    /// Returns a boolean indicating whether the specified key exists or not. Mirrors Yjs' <c>has(key)</c>;
    /// identical to <see cref="ContainsKey"/>, provided under both names for parity.
    /// </summary>
    public bool Has(string key) => ContainsKey(key);

    public IEnumerable<string> Keys() => TypeMapEnumerate().Select(kvp => kvp.Key);

    public IEnumerable<object> Values() => TypeMapEnumerate().Select(kvp => ValueAt(kvp.Value.Content, kvp.Value.Length - 1));

    /// <summary>
    /// Returns an enumerable of (key, value) pairs. Mirrors Yjs' <c>entries()</c>; C# idiom: since
    /// <see cref="YMap"/> already implements <c>IEnumerable&lt;KeyValuePair&lt;string,object&gt;&gt;</c>
    /// (and is therefore directly usable in a <c>foreach</c>, matching Yjs' <c>[Symbol.iterator]</c>
    /// which delegates to <c>entries()</c>), this is simply that same sequence under the Yjs-familiar name.
    /// </summary>
    public IEnumerable<KeyValuePair<string, object>> Entries() => this;

    /// <summary>
    /// Executes a provided function once on every key-value pair. Mirrors Yjs' <c>forEach(f)</c>.
    /// </summary>
    public void ForEach(System.Action<object, string, YMap> f)
    {
        foreach (var kvp in TypeMapEnumerateValues())
        {
            f(kvp.Value, kvp.Key, this);
        }
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
                foreach (var key in Keys().ToList())
                {
                    TypeMapDelete(tr, key);
                }
            });
        }
        else
        {
            _prelimContent.Clear();
        }
    }

    /// <summary>
    /// Transforms this Shared Type to a JSON-compatible <see cref="IDictionary{TKey,TValue}"/>. Mirrors Yjs' <c>toJSON()</c>.
    /// </summary>
    public override object ToJson() => JsonTree(this);

    public YMap Clone() => InternalClone() as YMap;

    public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => TypeMapEnumerateValues().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
