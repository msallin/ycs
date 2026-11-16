// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System.Collections;
using System.Runtime.CompilerServices;
using System.Text;

namespace Ycs;

/// <summary>
/// The children of a fragment or an element: <see cref="YXmlElement"/>, <see cref="YXmlText"/> and <see cref="YXmlHook"/>
/// nodes, read as <see cref="YType"/>. A value that is none of them, which only a foreign client can have put there, reads
/// as null.
/// </summary>
public partial class YXmlFragment : IReadOnlyList<YType>
{
    /// <summary>
    /// Observes the events of this fragment or element (Yjs' <c>observe(f)</c>). Disposing the result unsubscribes.
    /// </summary>
    public IDisposable Observe(Action<YXmlEvent> handler) => AddObserver(handler);

    // Yjs only enables search markers for Y.Array and Y.Text (and so Y.XmlText).
    public YXmlFragment()
        : base(useSearchMarkers: false)
    {
        _prelimContent = new List<object>();
    }

    public int Count => Length;

    internal override int Length => _prelimContent?.Count ?? base.Length;

    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not in 0 to <see cref="Count"/> - 1.</exception>
    public YType this[int index]
    {
        get
        {
            CheckIndex(index);
            return (RawAt(index) as YType)!;
        }
    }

    /// <summary>The child at <paramref name="index"/>; false when there is none.</summary>
    public bool TryGetValue(int index, out YType? value)
    {
        if ((uint)index >= (uint)Count)
        {
            value = null;
            return false;
        }

        value = RawAt(index) as YType;
        return true;
    }

    /// <summary>
    /// The first child of this fragment, or null if it has none.
    /// </summary>
    public YType? FirstChild => Count > 0 ? RawAt(0) as YType : null;

    public YXmlFragment Clone() => (YXmlFragment)InternalClone();

    /// <summary>
    /// The descendants (children, their children, and so on, in document order) that <paramref name="filter"/> accepts, all
    /// when it is null. The walk is lazy; a node removed or collected while it runs is not visited.
    /// </summary>
    public IEnumerable<YType> CreateTreeWalker(Func<YType, bool>? filter = null) => Walk(this, filter ?? (static _ => true));

    /// <summary>
    /// Returns the first YXmlElement whose node name matches the query (currently: a tag name only).
    /// Similar to DOM's 'querySelector'.
    /// </summary>
    public YXmlElement? QuerySelector(string query)
    {
        foreach (var element in QuerySelectorAll(query))
        {
            return element;
        }

        return null;
    }

    /// <summary>
    /// Returns all YXmlElements whose node name matches the query. Similar to DOM's 'querySelectorAll'.
    /// </summary>
    public IEnumerable<YXmlElement> QuerySelectorAll(string query)
    {
        var upper = query.ToUpperInvariant();
        foreach (var node in Walk(this, t => t is YXmlElement el && string.Equals(el.NodeName.ToUpperInvariant(), upper, StringComparison.Ordinal)))
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
        foreach (var child in EnumerateValues())
        {
            sb.Append(JsValue.ToJsString(child));
        }

        return sb.ToString();
    }

    public override object ToJson() => ToString();

    /// <summary>
    /// Inserts <paramref name="children"/> at <paramref name="index"/>, which is 0 to <see cref="Count"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is beyond <see cref="Count"/>.</exception>
    /// <exception cref="ArgumentException">A child is not a <see cref="YXmlFragment"/> (an element is one), a <see cref="YXmlText"/> or a <see cref="YXmlHook"/>.</exception>
    public void Insert(int index, params ReadOnlySpan<YType> children)
    {
        if ((uint)index > (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"The index is beyond the count ({Count}). Nothing was inserted.");
        }

        InsertRaw(index, Nodes(children));
    }

    /// <summary>
    /// Inserts <paramref name="children"/> right after <paramref name="reference"/> (or at the start, if it is null).
    /// </summary>
    /// <exception cref="ArgumentException">A child is not a <see cref="YXmlFragment"/> (an element is one), a <see cref="YXmlText"/> or a <see cref="YXmlHook"/>;
    /// or, for a fragment that is not in a document, <paramref name="reference"/> is not one of its children.</exception>
    public void InsertAfter(YType? reference, params ReadOnlySpan<YType> children)
    {
        var nodes = Nodes(children);
        if (Doc != null)
        {
            Doc.Transact((self: this, item: reference == null ? default : reference._item, nodes), static (s, tr) => s.self.InsertGenericsAfterReturningLast(tr, s.item, s.nodes));
        }
        else
        {
            var index = 0;
            if (reference != null)
            {
                index = _prelimContent.FindIndex(el => ReferenceEquals(el, reference)) + 1;
                if (index == 0)
                {
                    throw new ArgumentException("The reference is not a child of this fragment.", nameof(reference));
                }
            }

            _prelimContent.InsertRange(index, nodes);
        }
    }

    /// <summary>Appends <paramref name="children"/>.</summary>
    /// <exception cref="ArgumentException">A child is not a <see cref="YXmlFragment"/> (an element is one), a <see cref="YXmlText"/> or a <see cref="YXmlHook"/>.</exception>
    public void Add(params ReadOnlySpan<YType> children) => Insert(Count, children);

    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not in 0 to <see cref="Count"/> - 1.</exception>
    public void RemoveAt(int index)
    {
        CheckIndex(index);
        RemoveRangeCore(index, 1);
    }

    /// <exception cref="ArgumentOutOfRangeException">The range is not within the children; nothing is removed.</exception>
    public void RemoveRange(int index, int count)
    {
        CheckRange(index, count, nameof(index));
        RemoveRangeCore(index, count);
    }

    /// <summary>The <paramref name="length"/> children from <paramref name="start"/>; <c>node[1..3]</c> calls it.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The range is not within the children.</exception>
    public YType[] Slice(int start, int length)
    {
        CheckRange(start, length, nameof(start));
        var result = new YType[length];
        if (length == 0)
        {
            return result;
        }

        if (_prelimContent != null)
        {
            for (var i = 0; i < length; i++)
            {
                result[i] = (_prelimContent[start + i] as YType)!;
            }

            return result;
        }

        var slice = InternalSlice(start, start + length);
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = (slice[i] as YType)!;
        }

        return result;
    }

    public YType[] ToArray()
    {
        var result = new YType[Count];
        var i = 0;
        foreach (var raw in EnumerateValues())
        {
            result[i++] = (raw as YType)!;
        }

        return result;
    }

    /// <summary>The children visible in <paramref name="snapshot"/>. It only reads: an item merged across the snapshot's boundary is seen whole, as in Yjs.</summary>
    public YType[] ToArray(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var raw = ContentAtSnapshot(snapshot);
        var result = new YType[raw.Count];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = (raw[i] as YType)!;
        }

        return result;
    }

    public IEnumerator<YType> GetEnumerator()
    {
        foreach (var raw in EnumerateValues())
        {
            yield return (raw as YType)!;
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>The values in order, as stored; those of a fragment not in a document yet too.</summary>
    internal IEnumerable<object> EnumerateValues() => _prelimContent ?? EnumerateRaw();

    internal void InsertRaw(int index, ICollection<object?> content)
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

    private static object[] Nodes(ReadOnlySpan<YType> children)
    {
        var nodes = new object[children.Length];
        for (var i = 0; i < nodes.Length; i++)
        {
            if (children[i] is not (YXmlFragment or YXmlText or YXmlHook))
            {
                throw new ArgumentException(
                    $"A child of an XML fragment is a YXmlElement, a YXmlFragment, a YXmlText or a YXmlHook, not {(children[i] == null ? "null" : children[i].GetType().Name)}. Nothing was inserted.",
                    nameof(children));
            }

            nodes[i] = children[i];
        }

        return nodes;
    }

    private void RemoveRangeCore(int index, int count)
    {
        if (Doc != null)
        {
            Doc.Transact((self: this, index, count), static (s, tr) => s.self.Delete(tr, s.index, s.count));
        }
        else
        {
            PrelimDelete(_prelimContent, index, count);
        }
    }

    private object? RawAt(int index) =>
        _prelimContent != null ? _prelimContent[index] : TryGetRawAt(index, out var value) ? value : null;

    private void CheckIndex(int index)
    {
        if ((uint)index >= (uint)Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"The index is not within the children (count {Count}).");
        }
    }

    private void CheckRange(int start, int length, string paramName)
    {
        if (start < 0 || length < 0 || start > Count || length > Count - start)
        {
            throw new ArgumentOutOfRangeException(paramName, start, $"The range of {length} children from {start} is not within the children (count {Count}).");
        }
    }

    // Yjs' YXmlTreeWalker: a node is held across the caller's code between two nodes, valid while StructStore.Departures is
    // still the one it was found at, else looked up by its id (the type of a node can have been collected: its struct is a
    // GC struct then).
    private static IEnumerable<YType> Walk(YType root, Func<YType, bool> filter)
    {
        var n = root._start;
        var id = n.IsNull ? default : n.Id;
        var departures = n.IsNull ? 0 : root.Doc.Store.Departures;
        var first = true;

        while (true)
        {
            if (!n.IsNull && departures != root.Doc.Store.Departures)
            {
                n = root.Doc.Store.Find(id).AsItem;
            }

            if (!n.IsNull && (!first || n.Deleted || !filter(YXmlNodeHelpers.ContentTypeOf(n))))
            {
                do
                {
                    var type = YXmlNodeHelpers.ContentTypeOf(n);

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
                            var next = YXmlNodeHelpers.NextVisible(n);
                            if (!next.IsNull)
                            {
                                n = next;
                                break;
                            }
                            else if (ReferenceEquals(n.Parent, root))
                            {
                                n = default;
                            }
                            else
                            {
                                n = (n.Parent as YType)!._item;
                            }
                        }
                    }
                }
                while (!n.IsNull && (n.Deleted || !filter(YXmlNodeHelpers.ContentTypeOf(n))));
            }

            first = false;
            if (n.IsNull)
            {
                yield break;
            }

            id = n.Id;
            departures = root.Doc.Store.Departures;
            yield return YXmlNodeHelpers.ContentTypeOf(n);
        }
    }
}
