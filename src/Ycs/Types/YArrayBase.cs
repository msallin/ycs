// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System.Diagnostics;

namespace Ycs;

public abstract partial class YArrayBase : YType
{
    internal sealed class ArraySearchMarker
    {
        // The marker's item as a handle into the slab of its item's store; Debug keeps the slot's generation with it.
        private ItemHandle _held;
        private readonly Slab _slab;

        public ArraySearchMarker(ItemRef p, int index, long timestamp)
        {
            _slab = p.Slab;
            P = p;
            Index = index;
            p.Marker = true;
            Timestamp = timestamp;
        }

        internal ItemHandle Held => _held;

        public ItemRef P
        {
            get => _held.In(_slab);
            internal set => _held = value.Hold;
        }

        public int Index { get; internal set; }
        public long Timestamp { get; internal set; }

        public void Update(ItemRef p, int index, long timestamp)
        {
            var old = P;
            old.Marker = false;

            P = p;
            p.Marker = true;
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

        public ArraySearchMarker MarkPosition(ItemRef p, int index)
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
        /// is the last of the items that a run extended in place (<see cref="ItemRef.ExtendRun"/>) stands for: the runs
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
                    while (!p.IsNull && (p.Deleted || !p.Countable))
                    {
                        Debug.Assert(p.Left != p);
                        p = p.Left.AsItem;
                        if (!p.IsNull && !p.Deleted && p.Countable)
                        {
                            // Adjust position. The loop should break now.
                            m.Index -= p.Length;
                        }
                    }

                    if (p.IsNull || p.Marker)
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
    private protected static ItemRef LeftOf(ItemRef left, ItemRef right)
    {
        while (!left.IsNull && left.Right != right)
        {
            left = left.Right;
        }

        return left;
    }

    /// <summary>
    /// Search markers speed up index lookups, as in Yjs. <c>null</c> means they are disabled:
    /// Yjs only enables them for Y.Array and Y.Text, and disables them for a text as soon as it
    /// holds formatting (see <see cref="ContentFormat"/>).
    /// </summary>
    private protected ArraySearchMarkerCollection? _searchMarkers;

    // An item of this list (any: a push walks from it to the end), or null. Items leave a list only by merging into
    // their left neighbour (OnMerge moves the hint there) or when the whole type is collected (ForgetList).
    private ItemHandle _pushHintHeld = ItemHandle.None;

    private ItemRef _pushHint
    {
        get => _pushHintHeld.IsNone ? default : _pushHintHeld.In(_slab);
        set => _pushHintHeld = value.Hold;
    }

    // An empty list for the values of the next insert (see InsertGenericsAfterReturningLast), or null.
    private List<object?>? _spareValues;

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
    internal void OnMerge(ItemRef left, ItemRef right)
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

    /// <summary>
    /// The type was garbage collected: its list is gone (<see cref="ContentType"/>). The markers and the hint name items
    /// of that list, so they go too: a user's reference to the type outlives it.
    /// </summary>
    internal void ForgetList()
    {
        _pushHint = default;
        _searchMarkers?.Clear();
    }

    /// <summary>The markers in their order, as "client:clock@index" (the tests compare them).</summary>
    internal string DescribeSearchMarkers() =>
        _searchMarkers == null ? "off" : string.Join(" ", _searchMarkers._searchMarkers.Select(m => $"{m.P.Id.Client}:{m.P.Id.Clock}@{m.Index}"));

    /// <summary>
    /// What is wrong with the search markers, or null: each must point at an item of this list (reached from
    /// <see cref="YType._start"/>) whose index (the countable, undeleted length before it) is the marker's.
    /// Walks the whole list: for tests and Debug checks.
    /// </summary>
    internal string? SearchMarkerViolations()
    {
        if (_searchMarkers == null || _searchMarkers.Count == 0)
        {
            return null;
        }

        var indexes = new Dictionary<ItemRef, int>();
        var index = 0;
        for (var n = _start; !n.IsNull; n = n.Right.AsItem)
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
    internal override void CallObserver(Transaction transaction, ISet<string?> parentSubs)
    {
        if (!transaction.Local)
        {
            _searchMarkers?.Clear();
        }
    }

    /// <summary>
    /// Appends after the last item, deleted ones included (Yjs' <c>typeListPushGenerics</c>): the new
    /// item has no right origin, unlike an insert at <see cref="YType.Length"/>.
    /// </summary>
    private protected void PushGenerics(Transaction transaction, ICollection<object?> content)
    {
        // Yjs walks to the end from the marker with the highest index (its reduce starts from { index: 0, p: _start }),
        // which is the start when only pushes were made: quadratic in pushes. The walk starts from the item the last
        // push made instead, when there is one; any item of the list reaches the same end, and markers are not touched.
        var n = _pushHint;
        if (n.IsNull)
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

        while (!n.IsNull && n.Right.IsItem)
        {
            n = n.Right;
        }

        _pushHint = InsertGenericsAfterReturningLast(transaction, n, content);
    }

    /// <summary>
    /// For the walks from a marker to an index (the callers of <see cref="FindMarker"/>, which pass over an item when
    /// the index lies beyond its end and only count): the item of <paramref name="n"/>'s run they reach by jumping
    /// (<see cref="StructStore.SeekRight"/>), with <paramref name="index"/> lowered by what was passed; or
    /// <paramref name="n"/> with the reference walk.
    /// </summary>
    private protected ItemRef SeekRight(ItemRef n, ref int index)
    {
        Debug.Assert(Doc != null);
        return Doc.RunHeadIndexDisabledForTests ? n : Doc.Store.SeekRight(n, ref index, stopAtFormats: false);
    }

    /// <summary>JavaScript's <c>splice(index, 0, ...content)</c>, on the content of a type not in a document yet.</summary>
    private protected static void PrelimInsert(List<object?> list, int index, ICollection<object?> content) =>
        list.InsertRange(SpliceStart(list, index), content.Select(ValueFreezer.Detach));

    /// <summary>JavaScript's <c>splice(index, length)</c>, on the content of a type not in a document yet.</summary>
    private protected static void PrelimDelete(List<object?> list, int index, int length)
    {
        var start = SpliceStart(list, index);
        list.RemoveRange(start, Math.Clamp(length, 0, list.Count - start));
    }

    private static int SpliceStart(List<object?> list, int index) =>
        index < 0 ? Math.Max(list.Count + index, 0) : Math.Min(index, list.Count);

    /// <returns>The last item inserted, or <paramref name="referenceItem"/> when there was no content.</returns>
    private protected ItemRef InsertGenericsAfterReturningLast(Transaction transaction, ItemRef referenceItem, ICollection<object?> content)
    {
        var left = referenceItem;
        var doc = transaction.Doc;
        var ownClientId = doc.ClientId;
        var store = doc.Store;
        var right = referenceItem.IsNull ? _start : referenceItem.Right.AsItem;

        // Indexed where the content allows it: a foreach over the interface boxes the enumerator.
        var list = content as IList<object?>;
        var count = content.Count;
        if (list != null)
        {
            for (var i = 0; i < count; i++)
            {
                if (list[i] is YType)
                {
                    CheckCanNest();
                    break;
                }
            }
        }
        else if (content.Any(static c => c is YType))
        {
            CheckCanNest();
        }

        // The values between the other kinds of content, packed into one item each. The list goes to the item, or,
        // when the run before takes the values (ItemRef.ExtendRun), back to _spareValues for the next insert.
        var jsonContent = _spareValues ?? new List<object?>();
        _spareValues = null;

        void packJsonContent()
        {
            if (jsonContent.Count > 0 && !left.IsNull && left.CanExtendRun(transaction, this, right, typeof(ContentAny), ownClientId))
            {
                // Appending to this client's run: the run takes the values in place of a new item.
                ((ContentAny)left.Content).Append(jsonContent);
                left.ExtendRun(transaction, jsonContent.Count);
                jsonContent.Clear();
            }
            else if (jsonContent.Count > 0)
            {
                left = ItemRef.New(store.Slab, new ID(ownClientId, store.GetState(ownClientId)), left, right, this, null, ContentAny.Own(jsonContent));
                left.Integrate(transaction, 0);
                jsonContent = new List<object?>();
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

        void Insert(object? c)
        {
            switch (c)
            {
                case byte[] arr:
                    packJsonContent();

                    // A copy, as Yjs: the caller's array must not change the document later.
                    left = ItemRef.New(store.Slab, new ID(ownClientId, store.GetState(ownClientId)), left, right, this, null, new ContentBinary((byte[])arr.Clone()));
                    left.Integrate(transaction, 0);
                    break;
                case YDoc d:
                    packJsonContent();
                    left = ItemRef.New(store.Slab, new ID(ownClientId, store.GetState(ownClientId)), left, right, this, null, new ContentDoc(d));
                    left.Integrate(transaction, 0);
                    break;
                case YType at:
                    packJsonContent();
                    left = ItemRef.New(store.Slab, new ID(ownClientId, store.GetState(ownClientId)), left, right, this, null, new ContentType(at));
                    left.Integrate(transaction, 0);
                    break;
                default:
                    jsonContent.Add(ValueFreezer.Freeze(c, transaction.Doc.Limits.MaxDepth));
                    break;
            }
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
    private protected ArraySearchMarker? FindMarker(int index)
    {
        Debug.Assert(Doc != null);
        if (_start.IsNull || index == 0 || _searchMarkers == null || Doc.SearchMarkersDisabledForTests)
        {
            return null;
        }

        // The marker closest to index; as Yjs' reduce, a tie goes to the later one.
        var markers = _searchMarkers._searchMarkers;
        ArraySearchMarker? marker = null;
        if (markers.Count > 0)
        {
            marker = markers[0];
            for (var i = 1; i < markers.Count; i++)
            {
                if (!(Math.Abs(index - marker.Index) < Math.Abs(index - markers[i].Index)))
                {
                    marker = markers[i];
                }
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
        (p, pIndex) = Walk(p, pIndex, index, indexed ? Doc.Store : null, Doc.LiteralWalkStepsForTests);
        Debug.Assert(!indexed || Walk(start, startIndex, index, null, 0) == (p, pIndex), "The run-head index ends the marker walk where Yjs' loops end.");
        Doc.MarkerLookupsForTests?.Add((p.Id, pIndex));

        if (marker != null && Math.Abs(marker.Index - pIndex) < ((YType)p.Parent).Length / (double)MaxSearchMarkers)
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
    private static (ItemRef P, int Index) Walk(ItemRef p, int pIndex, int index, StructStore? store, int literalSteps)
    {
        // Iterate to right if possible. Within a run the index jumps over what the loop passes (ClientStructs.SeekRight),
        // and the loop takes the last steps: when the visible length reaches index exactly at an item's end, it steps
        // on to that item's right neighbour, whichever it is.
        // Walk allocates nothing, so a ref Slot taken at a step stays valid through it.
        var steps = 0;
        while (true)
        {
            ref readonly var s = ref p.Peek();
            if (s.Right < 0 || pIndex >= index)
            {
                break;
            }

            if (store != null && ++steps == literalSteps)
            {
                steps = 0;
                var remaining = index - pIndex;
                var reached = store.SeekRight(p, ref remaining, stopAtFormats: false);
                if (reached != p)
                {
                    p = reached;
                    pIndex = index - remaining;
                    continue;
                }
            }

            if ((s.Info & (Slot.DeletedFlag | Slot.CountableFlag)) == Slot.CountableFlag)
            {
                if (index < pIndex + s.Length)
                {
                    break;
                }

                pIndex += s.Length;
            }

            p = p.Right.AsItem;
        }

        // Iterate to left if necessary (might be that pIndex > index); within a run the index jumps (ClientStructs.SeekLeft).
        steps = 0;
        while (pIndex > index && p.Peek().Left >= 0)
        {
            if (store != null && ++steps == literalSteps && store.TryFindPosition(p, out var run, out var at))
            {
                steps = 0;
                var reached = run.SeekLeft(at, pIndex - index, out var passed);
                if (reached != at)
                {
                    p = run[reached];
                    pIndex -= checked((int)passed);
                    continue;
                }
            }

            p = p.Left.AsItem;
            if (p.IsNull)
            {
                break;
            }

            ref readonly var q = ref p.Peek();
            if ((q.Info & (Slot.DeletedFlag | Slot.CountableFlag)) == Slot.CountableFlag)
            {
                pIndex -= q.Length;
            }
        }

        // On to the left while the left neighbour has the same client and the clock right before p (Yjs' findMarker).
        // Which item a marker sits on is observable: where Yjs leaves markers stale (an update applied within a local
        // transaction, an observer editing during a remote transaction's cleanup), later lookups start from it. The
        // walk ends at the same item whether a run extended in place (ItemRef.ExtendRun) is one item or Yjs' several:
        // their boundaries continue each other by clock.
        steps = 0;
        var slab = p.Slab;
        while (true)
        {
            ref readonly var s = ref p.Peek();
            if (s.Left < 0)
            {
                break;
            }

            ref readonly var l = ref slab.Read(s.Left);
            if (l.Client != s.Client || l.Clock + l.Length != s.Clock)
            {
                break;
            }

            if (store != null && ++steps == literalSteps && store.TryFindPosition(p, out var structs, out var position))
            {
                // The rest of the walk: to the run's head, subtracting what it steps onto.
                p = structs[structs.RunHead(position, out var passed)];
                pIndex -= checked((int)passed);
                break;
            }

            p = p.Left.AsItem;
            if (p.IsNull)
            {
                break;
            }

            ref readonly var q = ref p.Peek();
            if ((q.Info & (Slot.DeletedFlag | Slot.CountableFlag)) == Slot.CountableFlag)
            {
                pIndex -= q.Length;
            }
        }

        return (p, pIndex);
    }
}
