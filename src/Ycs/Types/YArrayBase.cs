// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

using System.Diagnostics;

namespace Ycs;

public abstract partial class YArrayBase : AbstractType, IEnumerable<object>
{
    internal sealed class ArraySearchMarker
    {
        public ArraySearchMarker(Item p, int index, long timestamp)
        {
            P = p;
            Index = index;
            P.Marker = true;
            Timestamp = timestamp;
        }

        public Item P { get; internal set; }
        public int Index { get; internal set; }
        public long Timestamp { get; internal set; }

        public void Update(Item p, int index, long timestamp)
        {
            P.Marker = false;

            P = p;
            P.Marker = true;
            Index = index;
            Timestamp = timestamp;
        }
    }

    internal sealed class ArraySearchMarkerCollection
    {
        internal readonly List<ArraySearchMarker> _searchMarkers = new List<ArraySearchMarker>();

        // Yjs stamps markers from one global counter; only their order within a collection matters
        // (the oldest one is replaced), so a counter per collection gives the same choices.
        private long _clock;

        public int Count => _searchMarkers.Count;

        public void Clear()
        {
            _searchMarkers.Clear();
        }

        public long NextTimestamp() => _clock++;

        public ArraySearchMarker MarkPosition(Item p, int index)
        {
            if (_searchMarkers.Count >= MaxSearchMarkers)
            {
                // Override oldest marker (we don't want to create more objects). As Yjs' reduce, a
                // tie goes to the later one.
                var marker = _searchMarkers[0];
                for (var i = 1; i < _searchMarkers.Count; i++)
                {
                    if (!(marker.Timestamp < _searchMarkers[i].Timestamp))
                    {
                        marker = _searchMarkers[i];
                    }
                }

                marker.Update(p, index, NextTimestamp());
                return marker;
            }
            else
            {
                // Create a new marker.
                var pm = new ArraySearchMarker(p, index, NextTimestamp());
                _searchMarkers.Add(pm);
                return pm;
            }
        }

        /// <summary>
        /// Update markers when a change happened.
        /// This should be called before doing a deletion!
        /// </summary>
        /// <remarks>
        /// An insert moves a marker on a deleted or uncountable item onto the countable item left of it, which in Yjs
        /// is the last of the items that a run extended in place (<see cref="Item.ExtendRun"/>) stands for: the runs
        /// of <paramref name="transaction"/> are split back into Yjs' items first. That can split the item left of the
        /// insert, so the caller takes the item right before its right neighbour again (<see cref="LeftOf"/>).
        /// </remarks>
        public void UpdateMarkerChanges(Transaction transaction, int index, int len)
        {
            if (len > 0 && transaction.HasRunExtensions)
            {
                foreach (var m in _searchMarkers)
                {
                    if (m.P.Deleted || !m.P.Countable)
                    {
                        transaction.MaterializeRunExtensions();
                        break;
                    }
                }
            }

            for (int i = _searchMarkers.Count - 1; i >= 0; i--)
            {
                var m = _searchMarkers[i];

                if (len > 0)
                {
                    var p = m.P;
                    p.Marker = false;

                    // Ideally we just want to do a simple position comparison, but this will only work if
                    // search markers don't point to deleted items for formats.
                    // Iterate marker to prev undeleted countable position so we know what to do when updating a position.
                    while (p != null && (p.Deleted || !p.Countable))
                    {
                        Debug.Assert(p.Left != p);
                        p = p.Left as Item;
                        if (p != null && !p.Deleted && p.Countable)
                        {
                            // Adjust position. The loop should break now.
                            m.Index -= p.Length;
                        }
                    }

                    if (p == null || p.Marker)
                    {
                        // Remove search marker if updated position is null or if position is already marked.
                        _searchMarkers.RemoveAt(i);
                        continue;
                    }

                    m.P = p;
                    p.Marker = true;
                }

                // A simple index <= m.Index check would actually suffice.
                if (index < m.Index || (len > 0 && index == m.Index))
                {
                    m.Index = Math.Max(index, m.Index + len);
                }
            }
        }
    }

    private const int MaxSearchMarkers = 80;

    /// <summary>
    /// The item right before <paramref name="right"/>, from <paramref name="left"/>, which was right before it until
    /// <see cref="ArraySearchMarkerCollection.UpdateMarkerChanges"/> perhaps split it.
    /// </summary>
    private protected static Item LeftOf(Item left, AbstractStruct right)
    {
        while (left != null && left.Right != right)
        {
            left = (Item)left.Right;
        }

        return left;
    }

    /// <summary>
    /// Search markers speed up index lookups, as in Yjs. <c>null</c> means they are disabled:
    /// Yjs only enables them for Y.Array and Y.Text, and disables them for a text as soon as it
    /// holds formatting (see <see cref="ContentFormat"/>).
    /// </summary>
    private protected ArraySearchMarkerCollection _searchMarkers;

    // An item of this list (any: a push walks from it to the end), or null. Items leave a list only by merging into
    // their left neighbour (OnMerge moves the hint there) or when the whole type is collected (ForgetPushHint).
    private Item _pushHint;

    // An empty list for the values of the next insert (see InsertGenericsAfterReturningLast), or null.
    private List<object> _spareValues;

    protected YArrayBase(bool useSearchMarkers = true)
    {
        _searchMarkers = useSearchMarkers ? new ArraySearchMarkerCollection() : null;
    }

    public IEnumerator<object> GetEnumerator()
    {
        return EnumerateContent().GetEnumerator();
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
    {
        return EnumerateContent().GetEnumerator();
    }

    internal void ClearSearchMarkers()
    {
        _searchMarkers?.Clear();
    }

    internal void DisableSearchMarkers()
    {
        _searchMarkers = null;
    }

    /// <summary>
    /// Called when <paramref name="right"/> is merged into <paramref name="left"/>: a marker on
    /// <paramref name="right"/> moves to <paramref name="left"/> (Yjs <c>Item#mergeWith</c>), and so does the push hint.
    /// </summary>
    internal void OnMerge(Item left, Item right)
    {
        if (_pushHint == right)
        {
            _pushHint = left;
        }

        if (_searchMarkers == null)
        {
            return;
        }

        foreach (var marker in _searchMarkers._searchMarkers)
        {
            if (marker.P == right)
            {
                // Right is going to be "forgotten" so we need to update the marker.
                marker.P = left;

                // Adjust marker index.
                if (!left.Deleted && left.Countable)
                {
                    marker.Index -= left.Length;
                }
            }
        }
    }

    /// <summary>The type was garbage collected: its list is gone (<see cref="ContentType"/>).</summary>
    internal void ForgetPushHint() => _pushHint = null;

    /// <summary>The markers in their order, as "client:clock@index" (the tests compare them).</summary>
    internal string DescribeSearchMarkers() =>
        _searchMarkers == null ? "off" : string.Join(" ", _searchMarkers._searchMarkers.Select(m => $"{m.P.Id.Client}:{m.P.Id.Clock}@{m.Index}"));

    /// <summary>
    /// What is wrong with the search markers, or null: each must point at an item of this list (reached from
    /// <see cref="AbstractType._start"/>) whose index (the countable, undeleted length before it) is the marker's.
    /// Walks the whole list: for tests and Debug checks.
    /// </summary>
    internal string SearchMarkerViolations()
    {
        if (_searchMarkers == null || _searchMarkers.Count == 0)
        {
            return null;
        }

        var indexes = new Dictionary<Item, int>(ReferenceEqualityComparer.Instance);
        var index = 0;
        for (var n = _start; n != null; n = n.Right as Item)
        {
            indexes[n] = index;
            if (!n.Deleted && n.Countable)
            {
                index += n.Length;
            }
        }

        foreach (var m in _searchMarkers._searchMarkers)
        {
            if (!indexes.TryGetValue(m.P, out var actual))
            {
                return $"a marker points at {m.P.Id.Client}:{m.P.Id.Clock}, which is not in the list";
            }

            if (actual != m.Index)
            {
                return $"the marker at {m.P.Id.Client}:{m.P.Id.Clock} says index {m.Index}, the item is at {actual}";
            }
        }

        return null;
    }

    /// <summary>
    /// Creates YArrayEvent and calls observers.
    /// </summary>
    internal override void CallObserver(Transaction transaction, ISet<string> parentSubs)
    {
        if (!transaction.Local)
        {
            _searchMarkers?.Clear();
        }
    }

    protected void InsertGenerics(Transaction transaction, int index, ICollection<object> content)
    {
        if (index > Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"Length exceeded: the index is beyond the length ({Length}). Nothing was inserted.");
        }

        if (index == 0)
        {
            if (_searchMarkers?.Count > 0)
            {
                _searchMarkers.UpdateMarkerChanges(transaction, index, content.Count);
            }

            InsertGenericsAfter(transaction, null, content);
            return;
        }

        int startIndex = index;
        var marker = FindMarker(index);
        var n = _start;

        if (marker != null)
        {
            n = marker.P;
            index -= marker.Index;

            // We need to iterate one to the left so that the algorithm works.
            if (index == 0)
            {
                // @todo: refactor this as it actually doesn't consider formats.
                n = n.Prev as Item;
                index += n != null && n.Countable && !n.Deleted ? n.Length : 0;
            }
        }

        var steps = 0;
        for (; n != null; n = n.Right as Item)
        {
            if (++steps == transaction.Doc.LiteralWalkSteps)
            {
                steps = 0;
                n = SeekRight(n, ref index);
            }

            if (!n.Deleted && n.Countable)
            {
                if (index <= n.Length)
                {
                    if (index < n.Length)
                    {
                        // insert in-between
                        transaction.Doc.Store.GetItemCleanStart(transaction, new ID(n.Id.Client, n.Id.Clock + index));
                    }

                    break;
                }

                index -= n.Length;
            }
        }

        if (_searchMarkers?.Count > 0)
        {
            var right = n == null ? null : n.Right;
            _searchMarkers.UpdateMarkerChanges(transaction, startIndex, content.Count);
            n = LeftOf(n, right);
        }

        InsertGenericsAfter(transaction, n, content);
    }

    /// <summary>
    /// Appends after the last item, deleted ones included (Yjs' <c>typeListPushGenerics</c>): the new
    /// item has no right origin, unlike an insert at <see cref="AbstractType.Length"/>.
    /// </summary>
    private protected void PushGenerics(Transaction transaction, ICollection<object> content)
    {
        // Yjs walks to the end from the marker with the highest index (its reduce starts from { index: 0, p: _start }),
        // which is the start when only pushes were made: quadratic in pushes. The walk starts from the item the last
        // push made instead, when there is one; any item of the list reaches the same end, and markers are not touched.
        var n = _pushHint;
        if (n == null)
        {
            n = _start;
            var maxIndex = 0;
            if (_searchMarkers != null)
            {
                foreach (var marker in _searchMarkers._searchMarkers)
                {
                    if (marker.Index > maxIndex)
                    {
                        maxIndex = marker.Index;
                        n = marker.P;
                    }
                }
            }
        }

        while (n?.Right is Item right)
        {
            n = right;
        }

        _pushHint = InsertGenericsAfterReturningLast(transaction, n, content);
    }

    /// <summary>
    /// For the walks from a marker to an index (the callers of <see cref="FindMarker"/>, which pass over an item when
    /// the index lies beyond its end and only count): the item of <paramref name="n"/>'s run they reach by jumping
    /// (<see cref="StructStore.SeekRight"/>), with <paramref name="index"/> lowered by what was passed; or
    /// <paramref name="n"/> with the reference walk.
    /// </summary>
    private protected Item SeekRight(Item n, ref int index) =>
        Doc.RunHeadIndexDisabledForTests ? n : Doc.Store.SeekRight(n, ref index, stopAtFormats: false);

    /// <summary>JavaScript's <c>splice(index, 0, ...content)</c>, on the content of a type not in a document yet.</summary>
    private protected static void PrelimInsert(List<object> list, int index, ICollection<object> content) =>
        list.InsertRange(SpliceStart(list, index), content);

    /// <summary>JavaScript's <c>splice(index, length)</c>, on the content of a type not in a document yet.</summary>
    private protected static void PrelimDelete(List<object> list, int index, int length)
    {
        var start = SpliceStart(list, index);
        list.RemoveRange(start, Math.Clamp(length, 0, list.Count - start));
    }

    private static int SpliceStart(List<object> list, int index) =>
        index < 0 ? Math.Max(list.Count + index, 0) : Math.Min(index, list.Count);

    protected void InsertGenericsAfter(Transaction transaction, Item referenceItem, ICollection<object> content) =>
        InsertGenericsAfterReturningLast(transaction, referenceItem, content);

    /// <returns>The last item inserted, or <paramref name="referenceItem"/> when there was no content.</returns>
    private protected Item InsertGenericsAfterReturningLast(Transaction transaction, Item referenceItem, ICollection<object> content)
    {
        var left = referenceItem;
        var doc = transaction.Doc;
        var ownClientId = doc.ClientId;
        var store = doc.Store;
        var right = referenceItem == null ? _start : referenceItem.Right as Item;

        // Indexed where the content allows it: a foreach over the interface boxes the enumerator.
        var list = content as IList<object>;
        var count = content.Count;
        if (list != null)
        {
            for (var i = 0; i < count; i++)
            {
                if (list[i] is AbstractType)
                {
                    CheckCanNest();
                    break;
                }
            }
        }
        else if (content.Any(static c => c is AbstractType))
        {
            CheckCanNest();
        }

        // The values between the other kinds of content, packed into one item each. The list goes to the item, or,
        // when the run before takes the values (Item.ExtendRun), back to _spareValues for the next insert.
        var jsonContent = _spareValues ?? new List<object>();
        _spareValues = null;

        void packJsonContent()
        {
            if (jsonContent.Count > 0 && left != null && left.CanExtendRun(transaction, this, right, typeof(ContentAny), ownClientId))
            {
                // Appending to this client's run: the run takes the values in place of a new item.
                ((ContentAny)left.Content).Append(jsonContent);
                left.ExtendRun(transaction, jsonContent.Count);
                jsonContent.Clear();
            }
            else if (jsonContent.Count > 0)
            {
                left = new Item(new ID(ownClientId, store.GetState(ownClientId)), left, left?.LastId, right, right?.Id, this, null, ContentAny.Own(jsonContent));
                left.Integrate(transaction, 0);
                jsonContent = new List<object>();
            }
        }

        if (list != null)
        {
            for (var i = 0; i < count; i++)
            {
                Insert(list[i]);
            }
        }
        else
        {
            foreach (var c in content)
            {
                Insert(c);
            }
        }

        packJsonContent();
        if (jsonContent.Capacity <= 16)
        {
            _spareValues = jsonContent;
        }

        return left;

        void Insert(object c)
        {
            switch (c)
            {
                case byte[] arr:
                    packJsonContent();

                    // A copy, as Yjs: the caller's array must not change the document later.
                    left = new Item(new ID(ownClientId, store.GetState(ownClientId)), left, left?.LastId, right, right?.Id, this, null, new ContentBinary((byte[])arr.Clone()));
                    left.Integrate(transaction, 0);
                    break;
                case YDoc d:
                    packJsonContent();
                    left = new Item(new ID(ownClientId, store.GetState(ownClientId)), left, left?.LastId, right, right?.Id, this, null, new ContentDoc(d));
                    left.Integrate(transaction, 0);
                    break;
                case AbstractType at:
                    packJsonContent();
                    left = new Item(new ID(ownClientId, store.GetState(ownClientId)), left, left?.LastId, right, right?.Id, this, null, new ContentType(at));
                    left.Integrate(transaction, 0);
                    break;
                default:
                    AnyEncoding.Validate(c, transaction.Doc.Limits.MaxDepth);
                    jsonContent.Add(c);
                    break;
            }
        }
    }

    protected void Delete(Transaction transaction, int index, int length)
    {
        if (length == 0)
        {
            return;
        }

        int startIndex = index;
        int startLength = length;
        var marker = FindMarker(index);
        var n = _start;

        if (marker != null)
        {
            n = marker.P;
            index -= marker.Index;
        }

        // Compute the first item to be deleted.
        var steps = 0;
        for (; n != null && index > 0; n = n.Right as Item)
        {
            if (++steps == transaction.Doc.LiteralWalkSteps)
            {
                steps = 0;
                n = SeekRight(n, ref index);
            }

            if (!n.Deleted && n.Countable)
            {
                if (index < n.Length)
                {
                    transaction.Doc.Store.GetItemCleanStart(transaction, new ID(n.Id.Client, n.Id.Clock + index));
                }

                index -= n.Length;
            }
        }

        // Delete all items until done.
        while (length > 0 && n != null)
        {
            if (!n.Deleted)
            {
                if (length < n.Length)
                {
                    transaction.Doc.Store.GetItemCleanStart(transaction, new ID(n.Id.Client, n.Id.Clock + length));
                }

                n.Delete(transaction);
                length -= n.Length;
            }

            n = n.Right as Item;
        }

        if (length > 0)
        {
            throw new Exception("Array length exceeded");
        }

        if (_searchMarkers?.Count > 0)
        {
            _searchMarkers.UpdateMarkerChanges(transaction, startIndex, -startLength + length /* in case we remove the above exception */);
        }
    }

    /// <summary>
    /// Yjs' <c>typeListSlice</c>: never throws. A negative index counts from the end; an empty range
    /// gives an empty list; a start that is still negative then yields null for each index before 0,
    /// as JavaScript reads them.
    /// </summary>
    protected IReadOnlyList<object> InternalSlice(int start, int end)
    {
        if (start < 0)
        {
            start += Length;
        }

        if (end < 0)
        {
            end += Length;
        }

        int length = end - start;
        var cs = new List<object>();
        var n = _start;

        while (n != null && length > 0)
        {
            if (n.Countable && !n.Deleted)
            {
                var c = n.Content.GetContent();
                if (c.Count <= start)
                {
                    start -= c.Count;
                }
                else
                {
                    for (int i = start; i < c.Count && length > 0; i++)
                    {
                        cs.Add(i < 0 ? null : c[i]);
                        length--;
                    }

                    start = 0;
                }
            }

            n = n.Right as Item;
        }

        return cs.AsReadOnly();
    }

    protected void ForEach(Action<object, int, YArrayBase> fun)
    {
        int index = 0;
        var n = _start;

        while (n != null)
        {
            if (n.Countable && !n.Deleted)
            {
                var c = n.Content.GetContent();
                foreach (var cItem in c)
                {
                    fun(cItem, index++, this);
                }
            }

            n = n.Right as Item;
        }
    }

    protected void ForEachSnapshot(Action<object, int, YArrayBase> fun, Snapshot snapshot)
    {
        // Visibility is decided per item, by its first clock: Yjs' items.
        Doc?.MaterializeRunExtensions();
        int index = 0;
        var n = _start;

        while (n != null)
        {
            if (n.Countable && n.IsVisible(snapshot))
            {
                var c = n.Content.GetContent();
                foreach (var value in c)
                {
                    fun(value, index++, this);
                }
            }

            n = n.Right as Item;
        }
    }

    protected IEnumerable<object> EnumerateContent()
    {
        var n = _start;
        while (n != null)
        {
            while (n != null && n.Deleted)
            {
                n = n.Right as Item;
            }

            // Check if we reached the end, no need to check currentContent, because it does not exist.
            if (n == null)
            {
                yield break;
            }

            var currentContent = n.Content.GetContent();
            foreach (var c in currentContent)
            {
                yield return c;
            }

            // We used content of n, now iterate to next.
            n = n.Right as Item;
        }
    }

    /// <summary>
    /// Search markers help us to find positions in the associative array faster.
    /// <br/>
    /// They speed up the process of finding a position without much bookkeeping.
    /// <br/>
    /// A maximum of 'MaxSearchMarker' objects are created.
    /// <br/>
    /// This function always returns a refreshed marker (updated timestamp).
    /// </summary>
    private protected ArraySearchMarker FindMarker(int index)
    {
        if (_start == null || index == 0 || _searchMarkers == null || Doc.SearchMarkersDisabledForTests)
        {
            return null;
        }

        // The marker closest to index; as Yjs' reduce, a tie goes to the later one.
        var markers = _searchMarkers._searchMarkers;
        var marker = markers.Count == 0 ? null : markers[0];
        for (var i = 1; i < markers.Count; i++)
        {
            if (!(Math.Abs(index - marker.Index) < Math.Abs(index - markers[i].Index)))
            {
                marker = markers[i];
            }
        }

        var p = _start;
        int pIndex = 0;

        if (marker != null)
        {
            p = marker.P;
            pIndex = marker.Index;

            // We used it, we might need to use it again.
            marker.Timestamp = _searchMarkers.NextTimestamp();
        }

        var indexed = !Doc.RunHeadIndexDisabledForTests;
        var (start, startIndex) = (p, pIndex);
        (p, pIndex) = Walk(p, pIndex, index, indexed ? Doc.Store : null, Doc.LiteralWalkSteps);
        Debug.Assert(!indexed || Walk(start, startIndex, index, null, 0) == (p, pIndex), "The run-head index ends the marker walk where Yjs' loops end.");
        Doc.MarkerLookupsForTests?.Add((p.Id, pIndex));

        if (marker != null && Math.Abs(marker.Index - pIndex) < (p.Parent as AbstractType).Length / (double)MaxSearchMarkers)
        {
            // Adjust existing marker.
            marker.Update(p, pIndex, _searchMarkers.NextTimestamp());
            return marker;
        }
        else
        {
            // Create a new marker.
            return _searchMarkers.MarkPosition(p, pIndex);
        }
    }

    /// <summary>
    /// Yjs' three walks of <c>findMarker</c> from <paramref name="p"/> at <paramref name="pIndex"/> towards
    /// <paramref name="index"/>: right while before it, left while beyond it, then left while the left neighbour has
    /// p's client and ends at p's clock. With <paramref name="store"/>, a walk that goes on for
    /// <paramref name="literalSteps"/> steps (a lookup in the index costs more than a few) jumps within the run it is in
    /// through the run-head index (<see cref="ClientStructs.SeekRight"/>, <see cref="ClientStructs.SeekLeft"/>,
    /// <see cref="ClientStructs.RunHead"/>), over what the loop would pass; without, item by item (the reference).
    /// </summary>
    private static (Item P, int Index) Walk(Item p, int pIndex, int index, StructStore store, int literalSteps)
    {
        // Iterate to right if possible. Within a run the index jumps over what the loop passes (ClientStructs.SeekRight),
        // and the loop takes the last steps: when the visible length reaches index exactly at an item's end, it steps
        // on to that item's right neighbour, whichever it is.
        var steps = 0;
        while (p.Right != null && pIndex < index)
        {
            if (store != null && ++steps == literalSteps)
            {
                steps = 0;
                var remaining = index - pIndex;
                var reached = store.SeekRight(p, ref remaining, stopAtFormats: false);
                if (reached != p)
                {
                    (p, pIndex) = (reached, index - remaining);
                    continue;
                }
            }

            if (!p.Deleted && p.Countable)
            {
                if (index < pIndex + p.Length)
                {
                    break;
                }

                pIndex += p.Length;
            }

            p = p.Right as Item;
        }

        // Iterate to left if necessary (might be that pIndex > index); within a run the index jumps (ClientStructs.SeekLeft).
        steps = 0;
        while (p.Left != null && pIndex > index)
        {
            if (store != null && ++steps == literalSteps && store.TryFindPosition(p, out var run, out var at))
            {
                steps = 0;
                var reached = run.SeekLeft(at, pIndex - index, out var passed);
                if (reached != at)
                {
                    (p, pIndex) = ((Item)run[reached], pIndex - checked((int)passed));
                    continue;
                }
            }

            p = p.Left as Item;
            if (p == null)
            {
                break;
            }
            else if (!p.Deleted && p.Countable)
            {
                pIndex -= p.Length;
            }
        }

        // On to the left while the left neighbour has the same client and the clock right before p (Yjs' findMarker).
        // Which item a marker sits on is observable: where Yjs leaves markers stale (an update applied within a local
        // transaction, an observer editing during a remote transaction's cleanup), later lookups start from it. The
        // walk ends at the same item whether a run extended in place (Item.ExtendRun) is one item or Yjs' several:
        // their boundaries continue each other by clock.
        steps = 0;
        while (p.Left != null && p.Left.Id.Client == p.Id.Client && p.Left.Id.Clock + p.Left.Length == p.Id.Clock)
        {
            if (store != null && ++steps == literalSteps && store.TryFindPosition(p, out var structs, out var position))
            {
                // The rest of the walk: to the run's head, subtracting what it steps onto.
                p = (Item)structs[structs.RunHead(position, out var passed)];
                pIndex -= checked((int)passed);
                break;
            }

            p = p.Left as Item;
            if (p == null)
            {
                break;
            }
            else if (!p.Deleted && p.Countable)
            {
                pIndex -= p.Length;
            }
        }

        return (p, pIndex);
    }
}
