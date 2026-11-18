using System.Diagnostics;

namespace Ycs;

/// <summary>
/// DeleteSet is a temporary object that is created when needed.
/// - When created in a transaction, it must only be accessed after sorting and merging.
///   - This DeleteSet is sent to other clients.
/// - We do not create a DeleteSet when we send a sync message. The DeleteSet message is created
///   directly from StructStore (<see cref="WriteFromStore"/>).
/// - We read a DeleteSet as a apart of sync/update message. In this case the DeleteSet is already
///   sorted and merged.
/// </summary>
internal class DeleteSet
{
    public struct DeleteItem
    {
        public readonly long Clock;
        public readonly long Length;

        public DeleteItem(long clock, long length)
        {
            Clock = clock;
            Length = length;
        }
    }

    public DeleteSet()
    {
        Clients = new Dictionary<long, List<DeleteItem>>();
    }

    public DeleteSet(IList<DeleteSet> dss)
        : this()
    {
        MergeDeleteSets(dss);
    }

    public DeleteSet(StructStore ss)
        : this()
    {
        CreateDeleteSetFromStructStore(ss);
    }

    public Dictionary<long, List<DeleteItem>> Clients { get; }

    // Lists of a transaction's set, kept by ClearForReuse for the next transaction (nobody else holds them then).
    private Stack<List<DeleteItem>>? _spare;

    public void Add(long client, long clock, long length)
    {
        if (!Clients.TryGetValue(client, out var deletes))
        {
            deletes = _spare is { Count: > 0 } spare ? spare.Pop() : new List<DeleteItem>(2);
            Clients[client] = deletes;
        }

        deletes.Add(new DeleteItem(clock, length));
    }

    /// <summary>Empties the set and keeps its small lists for <see cref="Add"/>; only for a set no one else holds.</summary>
    internal void ClearForReuse()
    {
        foreach (var list in Clients.Values)
        {
            if (list.Capacity <= 16 && (_spare ??= new Stack<List<DeleteItem>>()).Count < 8)
            {
                list.Clear();
                _spare.Push(list);
            }
        }

        Clients.Clear();
    }

    /// <summary>
    /// Adds the ranges of <paramref name="other"/> to this set; both must be sorted and merged (as
    /// <see cref="SortAndMergeDeleteSet"/> leaves them), and this one stays so. Each range is inserted
    /// where it belongs, so the cost is that of <paramref name="other"/>, not of this set.
    /// </summary>
    internal void AddSortedAndMerged(DeleteSet other)
    {
        foreach (var (client, ranges) in other.Clients)
        {
            if (!Clients.TryGetValue(client, out var list))
            {
                Clients[client] = new List<DeleteItem>(ranges);
                continue;
            }

            foreach (var range in ranges)
            {
                Insert(list, range.Clock, range.Clock + range.Length);
            }
        }

        static void Insert(List<DeleteItem> list, long clock, long end)
        {
            // The first range that ends at or after 'clock': it and the ones after it that start at or
            // before 'end' touch the new range and merge with it.
            int lo = 0, hi = list.Count;
            while (lo < hi)
            {
                var mid = (lo + hi) >> 1;
                if (list[mid].Clock + list[mid].Length < clock)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            var last = lo;
            while (last < list.Count && list[last].Clock <= end)
            {
                clock = Math.Min(clock, list[last].Clock);
                end = Math.Max(end, list[last].Clock + list[last].Length);
                last++;
            }

            if (last == lo)
            {
                list.Insert(lo, new DeleteItem(clock, end - clock));
            }
            else
            {
                list[lo] = new DeleteItem(clock, end - clock);
                list.RemoveRange(lo + 1, last - lo - 1);
            }
        }
    }

    /// <summary>
    /// Iterate over all structs that the DeleteSet gc'd.
    /// </summary>
    public void IterateDeletedStructs(Transaction transaction, Predicate<ItemRef> fun)
    {
        // As Yjs: a client the store does not have is skipped, and a client's ranges end at the
        // first one that starts beyond its state (a snapshot can be newer than the document).
        foreach (var kvp in Clients)
        {
            if (!transaction.Doc.Store.Clients.TryGetValue(kvp.Key, out var structs))
            {
                continue;
            }

            var lastStruct = structs[structs.Count - 1];
            var clockState = lastStruct.Id.Clock + lastStruct.Length;
            foreach (var del in kvp.Value)
            {
                if (del.Clock >= clockState)
                {
                    break;
                }

                transaction.Doc.Store.IterateStructs(transaction, structs, del.Clock, del.Length, fun);
            }
        }
    }

    public static int? FindIndexSS(IList<DeleteItem> dis, long clock)
    {
        var left = 0;
        var right = dis.Count - 1;

        while (left <= right)
        {
            var midIndex = (left + right) / 2;
            var mid = dis[midIndex];
            var midClock = mid.Clock;

            if (midClock <= clock)
            {
                if (clock < midClock + mid.Length)
                {
                    return midIndex;
                }

                left = midIndex + 1;
            }
            else
            {
                right = midIndex - 1;
            }
        }

        return null;
    }

    public bool IsDeleted(ItemId id)
    {
        return Clients.TryGetValue(id.Client, out var dis)
               && FindIndexSS(dis, id.Clock) != null;
    }

    public void SortAndMergeDeleteSet()
    {
        foreach (var dels in Clients.Values)
        {
            dels.Sort((a, b) => a.Clock.CompareTo(b.Clock));

            // Merge items without filtering or splicing the array.
            // i is the current pointer.
            // j refers to the current insert position for the pointed item.
            // Try to merge dels[i] into dels[j-1] or set dels[j]=dels[i].
            int i, j;
            for (i = 1, j = 1; i < dels.Count; i++)
            {
                var left = dels[j - 1];
                var right = dels[i];

                // '>=', not '==': ranges that overlap (not just touch) must coalesce too,
                // otherwise a merged delete set keeps duplicate ranges. Matches Yjs'
                // sortAndMergeDeleteSet.
                if (left.Clock + left.Length >= right.Clock)
                {
                    dels[j - 1] = new DeleteItem(
                        left.Clock,
                        Math.Max(left.Length, right.Clock + right.Length - left.Clock));
                }
                else
                {
                    if (j < i)
                    {
                        dels[j] = right;
                    }

                    j++;
                }
            }

            // Trim the collection.
            if (j < dels.Count)
            {
                dels.RemoveRange(j, dels.Count - j);
            }
        }
    }

    public void TryGcDeleteSet(StructStore store, YDoc doc)
    {
        var gcFilter = doc.GcFilter;
        foreach (var kvp in Clients)
        {
            var client = kvp.Key;
            var deleteItems = kvp.Value;
            var structs = store.Clients[client];
            var hint = structs.Count - 1;

            for (int di = deleteItems.Count - 1; di >= 0; di--)
            {
                var deleteItem = deleteItems[di];
                var endDeleteItemClock = deleteItem.Clock + deleteItem.Length;

                for (int si = structs.FindIndex(deleteItem.Clock, ref hint); si < structs.Count; si++)
                {
                    var str = structs[si];
                    if (str.Id.Clock >= endDeleteItemClock)
                    {
                        break;
                    }

                    if (str.IsItem && str.Deleted && !str.Keep && gcFilter(ItemView.Of(doc, str)))
                    {
                        str.Gc(store, parentGCd: false);
                    }
                }
            }
        }
    }

    public void TryMergeDeleteSet(StructStore store)
    {
        // Try to merge deleted / gc'd items.
        // Merge from right to left for better efficiency and so we don't miss any merge targets.
        foreach (var kvp in Clients)
        {
            var client = kvp.Key;
            var deleteItems = kvp.Value;
            var structs = store.Clients[client];
            var hint = structs.Count - 1;

            for (int di = deleteItems.Count - 1; di >= 0; di--)
            {
                var deleteItem = deleteItems[di];

                // Start with merging the item next to the last deleted item.
                var mostRightIndexToCheck = Math.Min(structs.Count - 1, 1 + structs.FindIndex(deleteItem.Clock + deleteItem.Length - 1, ref hint));
                for (int si = mostRightIndexToCheck; si > 0 && structs[si].Id.Clock >= deleteItem.Clock;)
                {
                    si -= 1 + TryToMergeWithLefts(store, structs, si);
                }
            }
        }
    }

    /// <summary>
    /// Yjs' <c>tryToMergeWithLefts</c>: merges <c>structs[pos]</c> into its left neighbour, and that
    /// into its left, as long as they merge. Returns how many structs were removed.
    /// </summary>
    /// <remarks>
    /// Yjs merges right to left, a pair at a time, so each left neighbour copies the content accumulated to its
    /// right: quadratic in the run. Whether a pair merges does not depend on whether either struct merged with its
    /// other neighbour first (an item that took in its right neighbour keeps its start, origins and kind and ends
    /// where that neighbour ended), so the run is found first and then merged into its leftmost struct from left to
    /// right, which appends. The structs, ids, lengths, links, map entries, search markers and the count are the
    /// same as Yjs' order gives (<see cref="TryToMergeWithLeftsPairwise"/>, which the tests compare with).
    /// </remarks>
    public static int TryToMergeWithLefts(ClientStructs structs, int pos)
    {
        var first = pos;
        while (first > 0)
        {
            var l = structs[first - 1];
            var r = structs[first];
            if (l.Deleted != r.Deleted || !l.IsSameKind(r) || !l.CanMergeWith(r))
            {
                break;
            }

            first--;
        }

        var merged = pos - first;
        if (merged == 0)
        {
            return 0;
        }

        var left = structs[first];
        for (var k = first + 1; k <= pos; k++)
        {
            var right = structs[k];
            var map = MapEntryOf(right);
            if (!left.MergeWith(right))
            {
                throw new InvalidOperationException(
                    $"The struct {right.Id.Client}:{right.Id.Clock} did not merge into its left neighbour although it can (a bug of Ycs). The " +
                    "structs before it in the run were merged into the run's first struct and are still in the client's struct list: the " +
                    "document is inconsistent and must not be used further.");
            }

            RetargetMapEntry(map, left, right);
        }

        structs.RemoveMerged(first + 1, merged);
        return merged;
    }

    /// <summary><see cref="TryToMergeWithLefts(ClientStructs, int)"/>, or the reference when the store asks for it.</summary>
    internal static int TryToMergeWithLefts(StructStore store, ClientStructs structs, int pos) =>
        store.PairwiseMergeForTests ? TryToMergeWithLeftsPairwise(structs, pos) : TryToMergeWithLefts(structs, pos);

    /// <summary>Yjs' order of <see cref="TryToMergeWithLefts(ClientStructs, int)"/>, pair by pair from the right: the reference the tests compare with.</summary>
    internal static int TryToMergeWithLeftsPairwise(ClientStructs structs, int pos)
    {
        var right = structs[pos];
        var left = structs[pos - 1];
        var i = pos;
        for (; i > 0; right = left, left = --i > 0 ? structs[i - 1] : default)
        {
            if (left.Deleted != right.Deleted || !left.IsSameKind(right))
            {
                break;
            }

            var map = MapEntryOf(right);
            if (!left.MergeWith(right))
            {
                break;
            }

            RetargetMapEntry(map, left, right);
        }

        var merged = pos - i;
        if (merged > 0)
        {
            structs.RemoveMerged(pos + 1 - merged, merged);
        }

        return merged;
    }

    // Read before the merge: a merged struct is freed and reads nothing.
    private static (YType? Parent, string? Key) MapEntryOf(ItemRef right) =>
        right.IsItem && right.ParentSub is { } key ? (right.Parent, key) : default;

    private static void RetargetMapEntry((YType? Parent, string? Key) map, ItemRef left, ItemRef right)
    {
        if (map is { Parent: { } parent, Key: { } key } && parent.TryGetMapItem(key, out var value) && value == right)
        {
            parent._map[key] = left.Hold;
        }
    }

    private void MergeDeleteSets(IList<DeleteSet> dss)
    {
        for (int dssI = 0; dssI < dss.Count; dssI++)
        {
            foreach (var kvp in dss[dssI].Clients)
            {
                var client = kvp.Key;
                var delsLeft = kvp.Value;

                if (!Clients.ContainsKey(client))
                {
                    // Write all missing keys from current ds and all following.
                    // If merged already contains 'client' current ds has already been added.
                    var dels = new List<DeleteItem>(delsLeft);

                    for (int i = dssI + 1; i < dss.Count; i++)
                    {
                        if (dss[i].Clients.TryGetValue(client, out var appends))
                        {
                            dels.AddRange(appends);
                        }
                    }

                    Clients[client] = dels;
                }
            }
        }

        SortAndMergeDeleteSet();
    }

    private void CreateDeleteSetFromStructStore(StructStore ss)
    {
        foreach (var (client, structs) in ss.Clients)
        {
            List<DeleteItem>? dsItems = null;
            for (var i = 0; NextDeletedRange(structs, ref i, out var clock, out var length);)
            {
                dsItems ??= new List<DeleteItem>();
                dsItems.Add(new DeleteItem(clock, length));
            }

            if (dsItems != null)
            {
                Clients[client] = dsItems;
            }
        }
    }

    /// <summary>
    /// Finds the next range of deleted structs at or after index <paramref name="i"/>, and moves
    /// <paramref name="i"/> past it. Neighbouring deleted structs form one range.
    /// </summary>
    private static bool NextDeletedRange(ClientStructs structs, ref int i, out long clock, out long length)
    {
        for (; i < structs.Count; i++)
        {
            var str = structs[i];
            if (str.Deleted)
            {
                clock = str.Id.Clock;
                length = str.Length;

                while (i + 1 < structs.Count)
                {
                    var next = structs[i + 1];
                    if ((next.Id.Clock == clock + length) && next.Deleted)
                    {
                        length += next.Length;
                        i++;
                    }
                    else
                    {
                        break;
                    }
                }

                i++;
                return true;
            }
        }

        clock = 0;
        length = 0;
        return false;
    }

    public void Write(IDSEncoder encoder)
    {
        var count = Clients.Count;
        long[]? rented = null;
        Span<long> clients = count <= EncodingUtils.StackClients ? stackalloc long[EncodingUtils.StackClients] : (rented = BufferPool<long>.Rent(count));
        try
        {
            clients = clients[..count];
            var k = 0;
            foreach (var client in Clients.Keys)
            {
                clients[k++] = client;
            }

            // Highest client id first: Yjs' writeDeleteSet sorts "to ensure that the delete set is
            // written in a deterministic order".
            clients.Sort();

            encoder.RestWriter.WriteVarUint((uint)count);
            for (var c = count - 1; c >= 0; c--)
            {
                var client = clients[c];
                var dsItems = Clients[client];
                var len = dsItems.Count;

                encoder.ResetDsCurVal();
                encoder.RestWriter.WriteVarUint((ulong)client);
                encoder.RestWriter.WriteVarUint((uint)len);

                for (int i = 0; i < len; i++)
                {
                    var item = dsItems[i];
                    encoder.WriteDsClock(item.Clock);
                    encoder.WriteDsLength(item.Length);
                }
            }
        }
        finally
        {
            if (rented != null)
            {
                BufferPool<long>.Return(rented);
            }
        }
    }

    /// <summary>
    /// Writes the delete set of <paramref name="store"/> - what <c>new DeleteSet(store).Write(encoder)</c>
    /// writes, byte for byte - without building it: the ranges are found in the store twice, once to
    /// count them and once to write them.
    /// </summary>
    public static void WriteFromStore(IDSEncoder encoder, StructStore store)
    {
        var clientStructs = store.Clients;
        var capacity = clientStructs.Count;
        long[]? rentedClients = null;
        long[]? rentedCounts = null;
        Span<long> clients = capacity <= EncodingUtils.StackClients ? stackalloc long[EncodingUtils.StackClients] : (rentedClients = BufferPool<long>.Rent(capacity));
        Span<long> rangeCounts = capacity <= EncodingUtils.StackClients ? stackalloc long[EncodingUtils.StackClients] : (rentedCounts = BufferPool<long>.Rent(capacity));
        try
        {
            var count = 0;
            foreach (var (client, structs) in clientStructs)
            {
                var ranges = 0;
                for (var i = 0; NextDeletedRange(structs, ref i, out _, out _);)
                {
                    ranges++;
                }

                if (ranges > 0)
                {
                    clients[count] = client;
                    rangeCounts[count] = ranges;
                    count++;
                }
            }

            clients = clients[..count];
            clients.Sort(rangeCounts[..count]);

            encoder.RestWriter.WriteVarUint((uint)count);
            for (var c = count - 1; c >= 0; c--)
            {
                var client = clients[c];
                var structs = clientStructs[client];

                encoder.ResetDsCurVal();
                encoder.RestWriter.WriteVarUint((ulong)client);
                encoder.RestWriter.WriteVarUint((uint)rangeCounts[c]);

                for (var i = 0; NextDeletedRange(structs, ref i, out var clock, out var length);)
                {
                    encoder.WriteDsClock(clock);
                    encoder.WriteDsLength(length);
                }
            }
        }
        finally
        {
            if (rentedClients != null)
            {
                BufferPool<long>.Return(rentedClients);
                BufferPool<long>.Return(rentedCounts!); // Rented with rentedClients.
            }
        }
    }

    /// <summary>
    /// Reads a delete set. Every range is checked against the clock range; empty ranges, which
    /// delete nothing, are dropped.
    /// </summary>
    public static DeleteSet Read<TDecoder>(ref TDecoder decoder, ReadOnlySpan<byte> input)
        where TDecoder : struct, IDSDecoder
    {
        var ds = new DeleteSet();
        ReadInto(ref decoder, input, ds, spareLists: null);
        return ds;
    }

    /// <summary>
    /// <see cref="Read"/> into the empty <paramref name="ds"/>, taking the per-client lists from
    /// <paramref name="spareLists"/> while it has any (on an exception, the lists taken are in <paramref name="ds"/>).
    /// </summary>
    public static void ReadInto<TDecoder>(ref TDecoder decoder, ReadOnlySpan<byte> input, DeleteSet ds, Stack<List<DeleteItem>>? spareLists)
        where TDecoder : struct, IDSDecoder
    {
        Debug.Assert(ds.Clients.Count == 0);
        var numClients = decoder.Reader.ReadVarUint(input);

        for (long i = 0; i < numClients; i++)
        {
            decoder.ResetDsCurVal();

            var client = decoder.Reader.ReadVarUint53(input);
            var numberOfDeletes = decoder.Reader.ReadVarUint(input);

            if (numberOfDeletes > 0)
            {
                // Each range takes at least two bytes.
                var capacity = Limits.Capacity(numberOfDeletes, decoder.Reader.Remaining / 2);
                if (!ds.Clients.TryGetValue(client, out var dsField))
                {
                    if (spareLists != null && spareLists.TryPop(out dsField))
                    {
                        dsField.EnsureCapacity(capacity);
                    }
                    else
                    {
                        dsField = new List<DeleteItem>(capacity);
                    }

                    ds.Clients[client] = dsField;
                }

                for (long j = 0; j < numberOfDeletes; j++)
                {
                    var clock = decoder.ReadDsClock(input);
                    var length = decoder.ReadDsLength(input);
                    if (length == 0)
                    {
                        Limits.CheckClock(clock);
                        continue;
                    }

                    Limits.CheckRange(clock, length);
                    dsField.Add(new DeleteItem(clock, length));
                }

                if (dsField.Count == 0)
                {
                    ds.Clients.Remove(client);
                    spareLists?.Push(dsField);
                }
            }
        }
    }
}
