// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

using System.Collections;
using System.Runtime.CompilerServices;

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

    /// <summary>The JSON object of this map with each nested type left null and listed in <paramref name="nested"/>.</summary>
    internal override object ShallowJson(List<(AbstractType Type, object Container, string Key, int Index)> nested)
    {
        var result = new Dictionary<string, object>();
        foreach (var kvp in TypeMapEnumerateValues())
        {
            if (kvp.Value is AbstractType at)
            {
                nested.Add((at, result, kvp.Key, 0));
                result[kvp.Key] = null;
            }
            else
            {
                result[kvp.Key] = kvp.Value;
            }
        }

        return result;
    }

    internal override AbstractType InternalCopy()
    {
        return new YMap();
    }

    internal override AbstractType InternalClone()
    {
        // Recurses once per level of nested types: an exception, not a crash, where the stack runs out.
        RuntimeHelpers.EnsureSufficientExecutionStack();
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

    internal override void Integrate(YDoc doc, ItemRef item)
    {
        // Recurses once per level of nested types: an exception, not a crash, where the stack runs out.
        RuntimeHelpers.EnsureSufficientExecutionStack();
        base.Integrate(doc, item);

        foreach (var kvp in _prelimContent)
        {
            Set(kvp.Key, kvp.Value);
        }

        _prelimContent = null;
    }

    internal override void CallObserver(Transaction transaction, ISet<string> parentSubs)
    {
        if (IsObserved(transaction))
        {
            CallTypeObservers(transaction, new YMapEvent(this, transaction, parentSubs));
        }
    }

    internal override void Write(IUpdateEncoder encoder)
    {
        encoder.WriteTypeRef(YMapRefId);
    }

    internal static YMap Read(IUpdateDecoder decoder)
    {
        return new YMap();
    }
}
