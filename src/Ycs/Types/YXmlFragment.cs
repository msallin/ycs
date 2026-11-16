// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

using System.Collections;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Ycs;

/// <summary>
/// Small helpers shared by the Xml node types (<see cref="YXmlElement"/>, <see cref="YXmlText"/>)
/// that need to walk 'Item.Left'/'Item.Right' the way Yjs' 'Item.prototype.next'/'.prev' getters do
/// (i.e. skipping deleted items), and to unwrap the 'AbstractType' a list 'Item' carries.
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

    internal static AbstractType ContentTypeOf(ItemRef item)
    {
        if (item.IsNull)
        {
            return null;
        }

        var content = item.Content.GetContent();
        return content.Count > 0 ? content[0] as AbstractType : null;
    }
}

/// <summary>
/// Represents a subset of the nodes of a YXmlFragment / YXmlElement and a position within them.
/// Created via <see cref="YXmlFragment.CreateTreeWalker"/>.
/// </summary>
public sealed class YXmlTreeWalker : IEnumerator<AbstractType>, IEnumerable<AbstractType>
{
    private readonly Func<AbstractType, bool> _filter;
    private readonly AbstractType _root;
    // The current node, held across the caller's code between two nodes: _currentNode is valid while
    // StructStore.Departures is still _departures, else the node is looked up by _currentId.
    private ItemRef _currentNode;
    private ID _currentId;
    private int _departures;
    private bool _firstCall;

    internal YXmlTreeWalker(AbstractType root, Func<AbstractType, bool> filter = null)
    {
        _root = root;
        _filter = filter ?? (_ => true);
        _firstCall = true;
        Hold(root._start);
    }

    private void Hold(ItemRef node)
    {
        _currentNode = node;
        if (!node.IsNull)
        {
            _currentId = node.Id;
            _departures = _root.Doc.Store.Departures;
        }
    }

    public AbstractType Current { get; private set; }

    object IEnumerator.Current => Current;

    /// <summary>
    /// Advances to the next node. Ported from Yjs' 'YXmlTreeWalker.prototype.next'.
    /// </summary>
    public bool MoveNext()
    {
        var n = _currentNode;
        if (!n.IsNull && _departures != _root.Doc.Store.Departures)
        {
            // Nodes are not merged, but the type of a node can have been collected: its struct is a GC struct then.
            n = _root.Doc.Store.Find(_currentId).AsItem;
        }

        var type = YXmlNodeHelpers.ContentTypeOf(n);

        if (!n.IsNull && (!_firstCall || n.Deleted || !_filter(type)))
        {
            do
            {
                type = YXmlNodeHelpers.ContentTypeOf(n);

                if (!n.Deleted && type is YXmlFragment frag && !frag._start.IsNull)
                {
                    // Walk down into the tree.
                    n = frag._start;
                }
                else
                {
                    // Walk right, or up (and then right).
                    while (!n.IsNull)
                    {
                        var nxt = YXmlNodeHelpers.NextVisible(n);
                        if (!nxt.IsNull)
                        {
                            n = nxt;
                            break;
                        }
                        else if (ReferenceEquals(n.Parent, _root))
                        {
                            n = default;
                        }
                        else
                        {
                            n = (n.Parent as AbstractType)._item;
                        }
                    }
                }
            }
            while (!n.IsNull && (n.Deleted || !_filter(YXmlNodeHelpers.ContentTypeOf(n))));
        }

        _firstCall = false;
        Hold(n);

        if (n.IsNull)
        {
            Current = null;
            return false;
        }

        Current = YXmlNodeHelpers.ContentTypeOf(n);
        return true;
    }

    public void Reset() => throw new NotSupportedException();

    public void Dispose()
    {
        // Do nothing.
    }

    public IEnumerator<AbstractType> GetEnumerator() => this;

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Represents a list of <see cref="YXmlElement"/> and <see cref="YXmlText"/> (and, less commonly,
/// <see cref="YXmlHook"/>) nodes. A YXmlFragment is similar to a YXmlElement, but it has neither a
/// node name nor attributes.
/// </summary>
public partial class YXmlFragment : YArrayBase
{
    internal const int YXmlFragmentRefId = 4;

    private List<object> _prelimContent;

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
        _prelimContent = null;
        Insert(0, prelimContent);
    }

    internal override AbstractType InternalCopy()
    {
        return new YXmlFragment();
    }

    internal override AbstractType InternalClone()
    {
        // Recurses once per level of nested types: an exception, not a crash, where the stack runs out.
        RuntimeHelpers.EnsureSufficientExecutionStack();
        var el = new YXmlFragment();
        var children = new List<object>();
        foreach (var v in ToArray())
        {
            children.Add(v is AbstractType at ? at.InternalClone() : v);
        }

        el.Insert(0, children);
        return el;
    }

    /// <summary>
    /// Creates YXmlEvent and calls observers.
    /// </summary>
    internal override void CallObserver(Transaction transaction, ISet<string> parentSubs)
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

    internal static YXmlFragment Read(IUpdateDecoder decoder)
    {
        return new YXmlFragment();
    }
}
