// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System.Collections;

namespace Ycs;

/// <summary>
/// Event that describes changes on a YMap.
/// </summary>
public class YMapEvent : YEvent
{
    public readonly ISet<string> KeysChanged;

    internal YMapEvent(YMap map, Transaction transaction, ISet<string> subs)
        : base(map, transaction)
    {
        KeysChanged = subs;
    }
}

/// <summary>
/// A shared Map implementation.
/// </summary>
public partial class YMap : AbstractType, IEnumerable<KeyValuePair<string, object>>
{
    internal const int YMapRefId = 1;

    private Dictionary<string, object> _prelimContent;

    public YMap()
        : this(null)
    {
        // Do nothing.
    }

    public YMap(IDictionary<string, object> entries)
    {
        _prelimContent = entries != null ? new Dictionary<string, object>(entries) : new Dictionary<string, object>();
    }

    public int Count => _prelimContent?.Count ?? TypeMapEnumerate().Count();

    /// <summary>
    /// Returns the size of the YMap (count of key/value pairs). Mirrors Yjs' <c>size</c> getter;
    /// identical to <see cref="Count"/>, provided under both names for parity.
    /// </summary>
    public int Size => Count;

    public object Get(string key)
    {
        if (!TryTypeMapGet(key, out var value))
        {
            throw new KeyNotFoundException();
        }

        return value;
    }

    public void Set(string key, object value)
    {
        if (Doc != null)
        {
            Doc.Transact(tr =>
            {
                TypeMapSet(tr, key, value);
            });
        }
        else
        {
            _prelimContent[key] = value;
        }
    }

    public void Delete(string key)
    {
        if (Doc != null)
        {
            Doc.Transact(tr =>
            {
                TypeMapDelete(tr, key);
            });
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

    public IEnumerable<object> Values() => TypeMapEnumerate().Select(kvp => kvp.Value.Content.GetContent()[kvp.Value.Length - 1]);

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
    public override object ToJson()
    {
        var result = new Dictionary<string, object>();

        foreach (var kvp in TypeMapEnumerateValues())
        {
            result[kvp.Key] = kvp.Value is AbstractType at ? at.ToJson() : kvp.Value;
        }

        return result;
    }

    public YMap Clone() => InternalClone() as YMap;

    internal override AbstractType InternalCopy()
    {
        return new YMap();
    }

    internal override AbstractType InternalClone()
    {
        var map = new YMap();

        foreach (var kvp in TypeMapEnumerateValues())
        {
            // Bug fix vs. the original port: `TypeMapEnumerate()` yields raw `Item`s, not their
            // content values, so cloning used to store the `Item` itself as the value. Use
            // `TypeMapEnumerateValues()` (actual values) and recursively clone nested shared types,
            // matching Yjs' `clone()`: `map.set(key, value instanceof AbstractType ? value.clone() : value)`.
            map.Set(kvp.Key, kvp.Value is AbstractType at ? at.InternalClone() : kvp.Value);
        }

        return map;
    }

    internal override void Integrate(YDoc doc, Item item)
    {
        base.Integrate(doc, item);

        foreach (var kvp in _prelimContent)
        {
            Set(kvp.Key, kvp.Value);
        }

        _prelimContent = null;
    }

    internal override void CallObserver(Transaction transaction, ISet<string> parentSubs)
    {
        CallTypeObservers(transaction, new YMapEvent(this, transaction, parentSubs));
    }

    internal override void Write(IUpdateEncoder encoder)
    {
        encoder.WriteTypeRef(YMapRefId);
    }

    internal static YMap Read(IUpdateDecoder decoder)
    {
        return new YMap();
    }

    public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => TypeMapEnumerateValues().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
