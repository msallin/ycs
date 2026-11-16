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

public partial class YXmlFragment
{
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

    public YXmlFragment Clone() => InternalClone() as YXmlFragment;

    /// <summary>
    /// Creates a subtree of childNodes, filtered by the given predicate.
    /// </summary>
    public YXmlTreeWalker CreateTreeWalker(Func<YType, bool> filter = null)
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
    public void InsertAfter(YType refItem, ICollection<object> content)
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

    public void ForEach(Action<object, int, YXmlFragment> f) => base.ForEach((c, i, self) => f(c, i, (YXmlFragment)self));
}
