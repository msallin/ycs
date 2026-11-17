using System.Collections;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Ycs;

/// <summary>
/// Event that describes changes on a YMap.
/// </summary>
public sealed class YMapEvent : YEvent
{
    public IReadOnlySet<string> KeysChanged { get; }

    internal YMapEvent(YMap map, Transaction transaction, ISet<string?> subs)
        : base(map, transaction)
    {
        KeysChanged = (IReadOnlySet<string>)subs;
    }

    /// <summary>The map that changed.</summary>
    public new YMap Target => (YMap)base.Target;
}

/// <summary>
/// A shared Map implementation.
/// </summary>
public partial class YMap : YType
{
    internal const int YMapRefId = 1;

    private Dictionary<string, object?>? _prelimContent;

    /// <summary>The JSON object of this map with each nested type left null and listed in <paramref name="nested"/>.</summary>
    internal override object ShallowJson(List<(YType Type, object Container, string? Key, int Index)> nested)
    {
        var result = new Dictionary<string, object?>();
        foreach (var kvp in EnumerateEntries())
        {
            if (kvp.Value is YType at)
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

    internal override YType InternalCopy()
    {
        return new YMap();
    }

    internal override YType InternalClone()
    {
        // Recurses once per level of nested types: an exception, not a crash, where the stack runs out.
        RuntimeHelpers.EnsureSufficientExecutionStack();
        var map = new YMap();

        foreach (var kvp in EnumerateEntries())
        {
            // Bug fix vs. the original port: `TypeMapEnumerate()` yields raw `Item`s, not their
            // content values, so cloning used to store the `Item` itself as the value. Use
            // `TypeMapEnumerateValues()` (actual values) and recursively clone nested shared types,
            // matching Yjs' `clone()`: `map.set(key, value instanceof YType ? value.clone() : value)`.
            map.SetRaw(kvp.Key, kvp.Value is YType at ? at.InternalClone() : kvp.Value);
        }

        return map;
    }

    internal override void Integrate(YDoc doc, ItemRef item)
    {
        // Recurses once per level of nested types: an exception, not a crash, where the stack runs out.
        RuntimeHelpers.EnsureSufficientExecutionStack();
        base.Integrate(doc, item);

        var prelimContent = _prelimContent;
        Debug.Assert(prelimContent != null);
        _prelimContent = null;
        foreach (var kvp in prelimContent)
        {
            SetRaw(kvp.Key, kvp.Value);
        }
    }

    internal override void CallObserver(Transaction transaction, ISet<string?> parentSubs)
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
