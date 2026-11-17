using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Ycs;

/// <summary>
/// Small helpers shared by the Xml node types (<see cref="YXmlElement"/>, <see cref="YXmlText"/>)
/// that need to walk 'Item.Left'/'Item.Right' the way Yjs' 'Item.prototype.next'/'.prev' getters do
/// (i.e. skipping deleted items), and to unwrap the 'YType' a list 'Item' carries.
/// </summary>
internal static class YXmlNodeHelpers
{
    internal static ItemRef NextVisible(ItemRef item)
    {
        var n = item.IsNull ? default : item.Right.AsItem;
        while (!n.IsNull && n.Deleted)
        {
            n = n.Right.AsItem;
        }

        return n;
    }

    internal static ItemRef PrevVisible(ItemRef item)
    {
        var n = item.IsNull ? default : item.Left.AsItem;
        while (!n.IsNull && n.Deleted)
        {
            n = n.Left.AsItem;
        }

        return n;
    }

    /// <summary>The type a visible (not deleted) node item holds.</summary>
    internal static YType NodeOf(ItemRef item) => (YType)item.TypeOf;

    internal static YType? ContentTypeOf(ItemRef item)
    {
        if (item.IsNull)
        {
            return null;
        }

        return item.Kind == SlotKind.Type ? item.TypeOf as YType : null;
    }
}

/// <summary>
/// Represents a list of <see cref="YXmlElement"/> and <see cref="YXmlText"/> (and, less commonly,
/// <see cref="YXmlHook"/>) nodes. A YXmlFragment is similar to a YXmlElement, but it has neither a
/// node name nor attributes.
/// </summary>
public partial class YXmlFragment : YArrayBase
{
    internal const int YXmlFragmentRefId = 4;

    private List<object?>? _prelimContent;

    internal override void Integrate(YDoc doc, ItemRef item)
    {
        // Recurses once per level of nested types: an exception, not a crash, where the stack runs out.
        RuntimeHelpers.EnsureSufficientExecutionStack();
        base.Integrate(doc, item);

        // Clear '_prelimContent' *before* flushing it: 'Length' falls back to the prelim count
        // while it is non-null, and 'ItemRef.Integrate' bumps 'Parent.Length' through that same
        // virtual getter, so a stale prelim count makes every integrated item count twice.
        // Same fix as YArray.Integrate.
        var prelimContent = _prelimContent;
        Debug.Assert(prelimContent != null);
        _prelimContent = null;
        InsertRaw(0, prelimContent);
    }

    internal override YType InternalCopy()
    {
        return new YXmlFragment();
    }

    internal override YType InternalClone()
    {
        // Recurses once per level of nested types: an exception, not a crash, where the stack runs out.
        RuntimeHelpers.EnsureSufficientExecutionStack();
        var el = new YXmlFragment();
        var children = new List<object?>();
        foreach (var v in EnumerateValues())
        {
            children.Add(v is YType at ? at.InternalClone() : v);
        }

        el.InsertRaw(0, children);
        return el;
    }

    /// <summary>
    /// Creates YXmlEvent and calls observers.
    /// </summary>
    internal override void CallObserver(Transaction transaction, ISet<string?> parentSubs)
    {
        base.CallObserver(transaction, parentSubs);
        if (IsObserved(transaction))
        {
            CallTypeObservers(transaction, new YXmlEvent(this, parentSubs, transaction));
        }
    }

    internal override void Write(IUpdateEncoder encoder)
    {
        encoder.WriteTypeRef(YXmlFragmentRefId);
    }
}
