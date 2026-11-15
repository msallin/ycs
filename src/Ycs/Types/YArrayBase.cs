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
        public void UpdateMarkerChanges(int index, int len)
        {
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
    /// Search markers speed up index lookups, as in Yjs. <c>null</c> means they are disabled:
    /// Yjs only enables them for Y.Array and Y.Text, and disables them for a text as soon as it
    /// holds formatting (see <see cref="ContentFormat"/>).
    /// </summary>
    private protected ArraySearchMarkerCollection _searchMarkers;

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
    /// <paramref name="right"/> moves to <paramref name="left"/> (Yjs <c>Item#mergeWith</c>).
    /// </summary>
    internal void MoveMarkersOnMerge(Item left, Item right)
    {
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
                _searchMarkers.UpdateMarkerChanges(index, content.Count);
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

        for (; n != null; n = n.Right as Item)
        {
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
            _searchMarkers.UpdateMarkerChanges(startIndex, content.Count);
        }

        InsertGenericsAfter(transaction, n, content);
    }

    /// <summary>
    /// Appends after the last item, deleted ones included (Yjs' <c>typeListPushGenerics</c>): the new
    /// item has no right origin, unlike an insert at <see cref="AbstractType.Length"/>.
    /// </summary>
    private protected void PushGenerics(Transaction transaction, ICollection<object> content)
    {
        // From the marker with the highest index, as Yjs: its reduce starts from { index: 0, p: _start }.
        var n = _start;
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

        while (n?.Right is Item right)
        {
            n = right;
        }

        InsertGenericsAfter(transaction, n, content);
    }

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

    protected void InsertGenericsAfter(Transaction transaction, Item referenceItem, ICollection<object> content)
    {
        var left = referenceItem;
        var doc = transaction.Doc;
        var ownClientId = doc.ClientId;
        var store = doc.Store;
        var right = referenceItem == null ? _start : referenceItem.Right as Item;

        foreach (var c in content)
        {
            if (c is AbstractType)
            {
                CheckCanNest();
                break;
            }
        }

        var jsonContent = new List<object>();

        void packJsonContent()
        {
            if (jsonContent.Count > 0)
            {
                left = new Item(new ID(ownClientId, store.GetState(ownClientId)), left, left?.LastId, right, right?.Id, this, null, ContentAny.Own(jsonContent));
                left.Integrate(transaction, 0);
                jsonContent = new List<object>();
            }
        }

        foreach (var c in content)
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
                    AnyEncoding.Validate(c);
                    jsonContent.Add(c);
                    break;
            }
        }

        packJsonContent();
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
        for (; n != null && index > 0; n = n.Right as Item)
        {
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
            _searchMarkers.UpdateMarkerChanges(startIndex, -startLength + length /* in case we remove the above exception */);
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
        if (_start == null || index == 0 || _searchMarkers == null)
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

        // Iterate to right if possible.
        while (p.Right != null && pIndex < index)
        {
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

        // Iterate to left if necessary (might be that pIndex > index).
        while (p.Left != null && pIndex > index)
        {
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

        // We want to make sure that p can't be merged with left, because that would screw up everything.
        // In that case just return what we have (it is most likely the best marker anyway).
        // Iterate to left until p can't be merged with left.
        while (p.Left != null && p.Left.Id.Client == p.Id.Client && p.Left.Id.Clock + p.Left.Length == p.Id.Clock)
        {
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
}
