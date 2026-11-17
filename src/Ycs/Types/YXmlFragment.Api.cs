using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;

namespace Ycs;

/// <summary>
/// The children of a fragment or an element: <see cref="YXmlElement"/>, <see cref="YXmlText"/> and <see cref="YXmlHook"/>
/// nodes, read as <see cref="YType"/>. A child of another kind, which only a foreign client can have put there, counts in
/// <see cref="Count"/> but is not a node: the indexer throws for it, <see cref="TryGetValue"/> returns false, and enumeration,
/// <see cref="Slice"/> and <see cref="ToArray()"/> leave it out.
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
        _prelimContent = new List<object?>();
    }

    public int Count => Length;

    internal override int Length => _prelimContent?.Count ?? base.Length;

    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not in 0 to <see cref="Count"/> - 1.</exception>
    /// <exception cref="InvalidOperationException">The child at <paramref name="index"/> is not an XML node (see <see cref="TryGetValue"/>).</exception>
    public YType this[int index]
    {
        get
        {
            CheckIndex(index);
            return RawAt(index) as YType
                ?? throw new InvalidOperationException($"The child at index {index} is not an XML node (a foreign client put another kind of value there). Test it with TryGetValue.");
        }
    }

    /// <summary>The child at <paramref name="index"/>; false when there is none or it is not an XML node.</summary>
    public bool TryGetValue(int index, [NotNullWhen(true)] out YType? value)
    {
        value = unchecked((uint)index >= (uint)Count) ? null : RawAt(index) as YType;
        return value != null;
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

    internal override object ToJsonTree() => ToString();

    /// <summary>
    /// Inserts <paramref name="children"/> at <paramref name="index"/>, which is 0 to <see cref="Count"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is beyond <see cref="Count"/>.</exception>
    /// <exception cref="ArgumentException">A child is not a <see cref="YXmlFragment"/> (an element is one), a <see cref="YXmlText"/> or a <see cref="YXmlHook"/>.</exception>
    public void Insert(int index, params ReadOnlySpan<YType> children)
    {
        if (unchecked((uint)index > (uint)Count))
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"The index is beyond the count ({Count}). Nothing was inserted.");
        }

        InsertRaw(index, Nodes(children));
    }

    /// <summary>
    /// Inserts <paramref name="children"/> right after <paramref name="reference"/> (or at the start, if it is null).
    /// </summary>
    /// <exception cref="ArgumentException">A child is not a <see cref="YXmlFragment"/> (an element is one), a <see cref="YXmlText"/> or a <see cref="YXmlHook"/>;
    /// or <paramref name="reference"/> is not one of its children (Yjs inserts anyway and corrupts the list); or <paramref name="reference"/> was collected together with its parent, so it is in no list.</exception>
    public void InsertAfter(YType? reference, params ReadOnlySpan<YType> children)
    {
        var nodes = Nodes(children);
        if (Doc != null)
        {
            if (reference != null && !ReferenceEquals(reference.ParentType, this))
            {
                throw new ArgumentException("The reference is not a child of this fragment. Nothing was inserted.", nameof(reference));
            }

            if (reference is { IsCollected: true } && !reference.CurrentItem().IsItem)
            {
                throw new ArgumentException("The reference was collected with its parent: it is in no list any more. Nothing was inserted.", nameof(reference));
            }

            Doc.Transact((self: this, reference, nodes), static (s, tr) => s.self.InsertGenericsAfterReturningLast(tr, s.reference == null ? default : s.reference.ItemToInsertAfter(tr), s.nodes));
        }
        else
        {
            Debug.Assert(_prelimContent != null);
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
        if (length == 0)
        {
            return [];
        }

        return Nodes(_prelimContent != null ? _prelimContent.GetRange(start, length) : InternalSlice(start, start + length));
    }

    public YType[] ToArray()
    {
        return Nodes(EnumerateValues());
    }

    /// <summary>The children visible in <paramref name="snapshot"/>. It only reads: an item merged across the snapshot's boundary is seen whole, as in Yjs.</summary>
    public YType[] ToArray(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var raw = ContentAtSnapshot(snapshot);
        return Nodes(raw);
    }

    public IEnumerator<YType> GetEnumerator()
    {
        foreach (var raw in EnumerateValues())
        {
            if (raw is YType node)
            {
                yield return node;
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>The values in order, as stored; those of a fragment not in a document yet too.</summary>
    internal IEnumerable<object?> EnumerateValues() => _prelimContent ?? EnumerateRaw();

    internal void InsertRaw(int index, ICollection<object?> content)
    {
        if (Doc != null)
        {
            Doc.Transact((self: this, index, content), static (s, tr) => s.self.InsertGenerics(tr, s.index, s.content));
        }
        else
        {
            Debug.Assert(_prelimContent != null);
            PrelimInsert(_prelimContent, index, content);
        }
    }

    private static YType[] Nodes(IEnumerable<object?> raw)
    {
        var nodes = new List<YType>();
        foreach (var value in raw)
        {
            if (value is YType node)
            {
                nodes.Add(node);
            }
        }

        return [.. nodes];
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
            Debug.Assert(_prelimContent != null);
            PrelimDelete(_prelimContent, index, count);
        }
    }

    private object? RawAt(int index) =>
        _prelimContent != null ? _prelimContent[index] : TryGetRawAt(index, out var value) ? value : null;

    private void CheckIndex(int index)
    {
        if (unchecked((uint)index >= (uint)Count))
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
        var doc = root.Doc;
        Debug.Assert(doc != null);
        var n = root._start;
        var id = n.IsNull ? default : n.Id;
        var departures = n.IsNull ? 0 : doc.Store.Departures;
        var first = true;

        while (true)
        {
            if (!n.IsNull && departures != doc.Store.Departures)
            {
                n = doc.Store.Find(id).AsItem;
            }

            if (!n.IsNull && (!first || n.Deleted || !filter(YXmlNodeHelpers.NodeOf(n))))
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
                                n = ((YType)n.Parent).CurrentItem();
                            }
                        }
                    }
                }
                while (!n.IsNull && (n.Deleted || !filter(YXmlNodeHelpers.NodeOf(n))));
            }

            first = false;
            if (n.IsNull)
            {
                yield break;
            }

            id = n.Id;
            departures = doc.Store.Departures;
            yield return YXmlNodeHelpers.NodeOf(n);
        }
    }
}
