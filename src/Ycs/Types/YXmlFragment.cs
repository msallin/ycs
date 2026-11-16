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
    // ItemRef lifetime (docs/design-item-layout.md, 3.1): held across the caller's code between two nodes, which can
    // end transactions that merge it away or collect the type; to become an id with a hint.
    private ItemRef _currentNode;
    private bool _firstCall;

    internal YXmlTreeWalker(AbstractType root, Func<AbstractType, bool> filter = null)
    {
        _root = root;
        _filter = filter ?? (_ => true);
        _currentNode = root._start;
        _firstCall = true;
    }

    public AbstractType Current { get; private set; }

    object IEnumerator.Current => Current;

    /// <summary>
    /// Advances to the next node. Ported from Yjs' 'YXmlTreeWalker.prototype.next'.
    /// </summary>
    public bool MoveNext()
    {
        var n = _currentNode;
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
        _currentNode = n;

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
public class YXmlFragment : YArrayBase
{
    internal const int YXmlFragmentRefId = 4;

    private List<object> _prelimContent;

    // Yjs only enables search markers for Y.Array and Y.Text (and so Y.XmlText).
    public YXmlFragment()
        : base(useSearchMarkers: false)
    {
        _prelimContent = new List<object>();
    }

    public override int Length => _prelimContent?.Count ?? base.Length;

    /// <summary>
    /// The first child of this fragment, or null if it has none.
    /// </summary>
    public object FirstChild
    {
        get
        {
            var first = _First();
            return !first.IsNull ? first.Content.GetContent()[0] : null;
        }
    }

    internal override void Integrate(YDoc doc, ItemRef item)
    {
        // Recurses once per level of nested types: an exception, not a crash, where the stack runs out.
        RuntimeHelpers.EnsureSufficientExecutionStack();
        base.Integrate(doc, item);

        // Clear '_prelimContent' *before* flushing it: 'Length' falls back to the prelim count
        // while it is non-null, and 'Item.Integrate' bumps 'Parent.Length' through that same
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

    public YXmlFragment Clone() => InternalClone() as YXmlFragment;

    /// <summary>
    /// Creates a subtree of childNodes, filtered by the given predicate.
    /// </summary>
    public YXmlTreeWalker CreateTreeWalker(Func<AbstractType, bool> filter = null)
    {
        return new YXmlTreeWalker(this, filter);
    }

    /// <summary>
    /// Returns the first YXmlElement whose node name matches the query (currently: a tag name only).
    /// Similar to DOM's 'querySelector'.
    /// </summary>
    public YXmlElement QuerySelector(string query)
    {
        var upper = query.ToUpperInvariant();
        var walker = new YXmlTreeWalker(this, t => t is YXmlElement el && string.Equals(el.NodeName.ToUpperInvariant(), upper, StringComparison.Ordinal));
        return walker.MoveNext() ? (YXmlElement)walker.Current : null;
    }

    /// <summary>
    /// Returns all YXmlElements whose node name matches the query. Similar to DOM's 'querySelectorAll'.
    /// </summary>
    public IEnumerable<YXmlElement> QuerySelectorAll(string query)
    {
        var upper = query.ToUpperInvariant();
        var walker = new YXmlTreeWalker(this, t => t is YXmlElement el && string.Equals(el.NodeName.ToUpperInvariant(), upper, StringComparison.Ordinal));
        foreach (var node in walker)
        {
            yield return (YXmlElement)node;
        }
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

    /// <summary>
    /// The XML serialization of all the children of this fragment.
    /// </summary>
    public override string ToString()
    {
        // Recurses once per level of nested types: an exception, not a crash, where the stack runs out.
        RuntimeHelpers.EnsureSufficientExecutionStack();
        var sb = new StringBuilder();
        foreach (var child in EnumerateContent())
        {
            sb.Append(JsValue.ToJsString(child));
        }

        return sb.ToString();
    }

    public override object ToJson() => ToString();

    /// <summary>
    /// Inserts new content at an index.
    /// </summary>
    public void Insert(int index, ICollection<object> content)
    {
        if (Doc != null)
        {
            Doc.Transact((self: this, index, content), static (s, tr) => s.self.InsertGenerics(tr, s.index, s.content));
        }
        else
        {
            PrelimInsert(_prelimContent, index, content);
        }
    }

    /// <summary>
    /// Inserts new content right after 'refItem' (or at the start, if 'refItem' is null).
    /// </summary>
    public void InsertAfter(AbstractType refItem, ICollection<object> content)
    {
        if (Doc != null)
        {
            Doc.Transact((self: this, item: refItem == null ? default : refItem._item, content), static (s, tr) => s.self.InsertGenericsAfterReturningLast(tr, s.item, s.content));
        }
        else
        {
            int index;
            if (refItem == null)
            {
                index = 0;
            }
            else
            {
                index = _prelimContent.FindIndex(el => ReferenceEquals(el, refItem)) + 1;
                if (index == 0)
                {
                    throw new InvalidOperationException("Reference item not found");
                }
            }

            _prelimContent.InsertRange(index, content);
        }
    }

    /// <summary>
    /// Deletes elements starting from an index.
    /// </summary>
    public void Delete(int index, int length = 1)
    {
        if (Doc != null)
        {
            Doc.Transact((self: this, index, length), static (s, tr) => s.self.Delete(tr, s.index, s.length));
        }
        else
        {
            PrelimDelete(_prelimContent, index, length);
        }
    }

    public IList<object> ToArray()
    {
        var list = new List<object>();
        list.AddRange(EnumerateContent());
        return list;
    }

    public void Push(ICollection<object> content) => Insert(Length, content);

    public void Unshift(ICollection<object> content) => Insert(0, content);

    public object Get(int index)
    {
        var marker = FindMarker(index);
        var n = _start;

        if (marker != null)
        {
            n = marker.P;
            index -= marker.Index;
        }

        if (index < 0)
        {
            // A negative index: Yjs' typeListGet reads the first visible item's content at it, which is undefined (an XML
            // fragment has no search markers).
            return default;
        }

        var steps = 0;
        for (; !n.IsNull; n = n.Right.AsItem)
        {
            if (++steps == Doc.LiteralWalkStepsForTests)
            {
                steps = 0;
                n = SeekRight(n, ref index);
            }

            if (!n.Deleted && n.Countable)
            {
                if (index < n.Length)
                {
                    return n.Content.GetContent()[index];
                }

                index -= n.Length;
            }
        }

        return default;
    }

    public IReadOnlyList<object> Slice(int start = 0) => InternalSlice(start, Length);

    public IReadOnlyList<object> Slice(int start, int end) => InternalSlice(start, end);

    public void ForEach(Action<object, int, YXmlFragment> f) => ForEach((c, i, self) => f(c, i, (YXmlFragment)self));

    internal override void Write(IUpdateEncoder encoder)
    {
        encoder.WriteTypeRef(YXmlFragmentRefId);
    }

    internal static YXmlFragment Read(IUpdateDecoder decoder)
    {
        return new YXmlFragment();
    }
}
