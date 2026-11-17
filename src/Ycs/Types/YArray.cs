using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Ycs;

public sealed class YArrayEvent : YEvent
{
    internal YArrayEvent(YArray arr, Transaction transaction)
        : base(arr, transaction)
    {
        // Do nothing.
    }

    /// <summary>The array that changed.</summary>
    public new YArray Target => (YArray)base.Target;
}

public sealed partial class YArray : YArrayBase
{
    internal const byte YArrayRefId = 0;

    private List<object?>? _prelimContent;

    internal override void Integrate(YDoc doc, ItemRef item)
    {
        // Recurses once per level of nested types: an exception, not a crash, where the stack runs out.
        RuntimeHelpers.EnsureSufficientExecutionStack();
        base.Integrate(doc, item);

        // Bug fix: `_prelimContent` must be cleared *before* flushing it via Insert(), not after.
        // `Length` is overridden as `_prelimContent?.Count ?? base.Length` so it's readable before
        // attach; but ItemRef.Integrate() (Structs/ItemRef.Operations.cs) does `parent.Length += item.Length` for
        // every item it integrates -- and that `+=` reads `Parent.Length` through the very same
        // virtual getter. While `_prelimContent` was still non-null during the Insert() below, that
        // read returned the *stale prelim count* instead of the real, incrementally-accumulated
        // `base.Length`, so every integrated item's length was counted once for real and once again
        // via the stale prelim-count fallback -- silently doubling `Length` (content itself was
        // unaffected; only the cached counter was wrong). Nulling first makes the getter fall
        // through to `base.Length` for the whole duration of the flush, exactly as intended.
        var prelimContent = _prelimContent;
        Debug.Assert(prelimContent != null);
        _prelimContent = null;
        InsertRaw(0, prelimContent);
    }

    internal override YType InternalCopy()
    {
        return new YArray();
    }

    internal override YType InternalClone()
    {
        // Recurses once per level of nested types: an exception, not a crash, where the stack runs out.
        RuntimeHelpers.EnsureSufficientExecutionStack();
        var arr = new YArray();

        foreach (var item in EnumerateValues())
        {
            arr.AddRaw(new[] { item is YType at ? at.InternalClone() : item });
        }

        return arr;
    }

    internal override void Write(IUpdateEncoder encoder)
    {
        encoder.WriteTypeRef(YArrayRefId);
    }


    /// <summary>
    /// Creates YArrayEvent and calls observers.
    /// </summary>
    internal override void CallObserver(Transaction transaction, ISet<string?> parentSubs)
    {
        base.CallObserver(transaction, parentSubs);
        if (IsObserved(transaction))
        {
            CallTypeObservers(transaction, new YArrayEvent(this, transaction));
        }
    }

    /// <summary>The JSON list of this array with each nested type left null and listed in <paramref name="nested"/>.</summary>
    internal override object ShallowJson(List<(YType Type, object Container, string? Key, int Index)> nested)
    {
        var result = new List<object?>();
        foreach (var c in EnumerateValues())
        {
            if (c is YType at)
            {
                nested.Add((at, result, null, result.Count));
                result.Add(null);
            }
            else
            {
                result.Add(c);
            }
        }

        return result;
    }

    /// <summary>The values in order, as stored; those of an array not in a document yet too.</summary>
    private RawEnumerator EnumerateValues() => new(this);
}
