// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

using System.Collections;
using System.Diagnostics;

namespace Ycs;

/// <summary>
/// The structs of one client in clock order (Yjs' <c>store.clients.get(client)</c>), with their start clocks beside
/// them, so that finding the struct that holds a clock reads the starts, not the structs; in leaves
/// (ClientStructs.Leaves.cs), so that a split or a merge shifts one leaf, not the rest of the list.
/// </summary>
/// <remarks>
/// The structs are contiguous by clock: the end of position i is the start of i + 1, and <see cref="End"/> is the end
/// of the last one. Every change of the list goes through this class, which keeps the starts, the leaves and the
/// run-head index (ClientStructs.RunHeads.cs) in step. Three changes of items made elsewhere change what the index
/// holds and call <see cref="StructStore.Refresh"/>: an item deleted (<see cref="ItemRef.MarkDeleted()"/>), a deleted
/// content integrated (<see cref="ContentDeleted"/>), an item integrated between a struct and its predecessor
/// (<see cref="ItemRef.Left"/>).
/// </remarks>
internal sealed partial class ClientStructs : IReadOnlyList<ItemRef>
{
    private int _count;
    private long _end;

    // The slab the handles of the leaves name; the structs of one client list are in one store.
    private readonly Slab _slab;

    public ClientStructs(Slab slab, int capacity = 4)
    {
        _slab = slab;
        _single = new Leaf(Math.Clamp(capacity, 1, _leafCapacity));
    }

    public int Count => _count;

    /// <summary>The clock after the last struct (the client's state).</summary>
    public long End => _end;

    public ItemRef this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
            {
                ThrowOutOfRange(index, _count);
            }

            if (_leaves == null)
            {
                return _single.Structs[index].In(_slab);
            }

            var offset = index - _cursorFirst;
            if (unchecked((uint)offset) < (uint)_cursorCount)
            {
                return _cursorStructs[offset].In(_slab);
            }

            var k = LeafOf(index);
            return _leaves[k].Structs[index - _firstPositions[k]].In(_slab);
        }
    }

    public Enumerator GetEnumerator() => new(this);

    IEnumerator<ItemRef> IEnumerable<ItemRef>.GetEnumerator()
    {
        for (var k = 0; k < _leafCount; k++)
        {
            var leaf = LeafAt(k);
            for (var i = 0; i < leaf.Count; i++)
            {
                yield return leaf.Structs[i].In(_slab);
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<ItemRef>)this).GetEnumerator();

    public void EnsureCapacity(int capacity)
    {
        if (_leaves == null)
        {
            if (capacity > _single.Capacity && _single.Capacity < _leafCapacity)
            {
                GrowSingle(capacity);
            }
        }
        else
        {
            ReserveLeaves((capacity / _leafCapacity) + 1);
        }
    }

    /// <summary>Appends <paramref name="str"/>, which starts where the last struct ends (the caller checked).</summary>
    public void Add(ItemRef str)
    {
        var k = _leafCount - 1;
        var leaf = LeafAt(k);
        if (leaf.Count == leaf.Capacity)
        {
            if (leaf.Capacity == _leafCapacity)
            {
                AppendLeaf(str);
                k++;
                SetFlags(k, 0, FlagsAt(k, 0));
                AddSum(k, ValueAt(k, 0));
                return;
            }

            GrowSingle(leaf.Count + 1);
            leaf = _single;
        }

        // The last struct's length is unchanged: the new one starts where it ended.
        var offset = leaf.Count;
        leaf.Structs[offset] = str.Hold;
        leaf.Starts[offset] = str.Id.Clock;
        leaf.Flags[offset] = 0;
        leaf.Count++;
        _count++;
        _end = str.Id.Clock + str.Length;
        SetFlags(k, offset, FlagsAt(k, offset));
        AddSum(k, ValueAt(k, offset));
    }

    /// <summary>Inserts the right part of a split at <paramref name="index"/>, right after its left part.</summary>
    public void InsertSplit(int index, ItemRef right)
    {
        Debug.Assert(index > 0 && index <= _count && this[index - 1].Id.Clock + this[index - 1].Length == right.Id.Clock);

        // The right part goes into its left part's leaf, right after it.
        var k = Locate(index - 1, out var offset);
        offset++;
        var leaf = LeafAt(k);
        if (leaf.Count == leaf.Capacity)
        {
            if (leaf.Capacity < _leafCapacity)
            {
                GrowSingle(leaf.Count + 1);
            }
            else
            {
                SplitLeaf(k);
                var half = LeafAt(k).Count;
                if (offset > half)
                {
                    k++;
                    offset -= half;
                }
            }

            leaf = LeafAt(k);
        }

        var tail = leaf.Count - offset;
        ForgetCursor();
        Array.Copy(leaf.Structs, offset, leaf.Structs, offset + 1, tail);
        Array.Copy(leaf.Starts, offset, leaf.Starts, offset + 1, tail);
        Array.Copy(leaf.Flags, offset, leaf.Flags, offset + 1, tail);
        leaf.Structs[offset] = right.Hold;
        leaf.Starts[offset] = right.Id.Clock;
        leaf.Flags[offset] = 0;
        leaf.Count++;
        _count++;
        for (var j = k + 1; j < _leafCount; j++)
        {
            _firstPositions[j]++;
        }

        // The right part takes the left part's flags but Break, and the rest of its visible length: both are in one
        // leaf, so the leaf's sum stays.
        SetFlags(k, offset, FlagsAt(k, offset));
        Debug.Assert((leaf.Flags[offset] & ~Break) == (leaf.Flags[offset - 1] & ~Break));
        RefreshSuccessor(k, offset);
    }

    /// <summary>Removes the <paramref name="count"/> structs from <paramref name="index"/>, which merged into the one before.</summary>
    public void RemoveMerged(int index, int count)
    {
        Debug.Assert(index > 0 && count > 0 && index + count <= _count);
        var k0 = Locate(index - 1, out var left);
        var k0Flags = LeafAt(k0).Flags[left];

        // The removed positions leave the counts and sums (their visible length goes to the struct before, whose old
        // length leaves its leaf's sum here and whose new one comes back below).
        var leftDelta = -ValueAt(k0, left);
        var k = k0;
        var offset = left + 1;
        for (var remaining = count; remaining > 0; k++, offset = 0)
        {
            var leaf = LeafAt(k);
            var n = Math.Min(remaining, leaf.Count - offset);
            var removed = 0L;
            for (var i = offset; i < offset + n; i++)
            {
                Debug.Assert((leaf.Flags[i] & (Visible | Format)) == (k0Flags & (Visible | Format)));
                removed += ValueAt(k, i);
                SetFlags(k, i, 0);
            }

            if (k == k0)
            {
                leftDelta -= removed;
            }
            else
            {
                AddSum(k, -removed);
            }

            remaining -= n;
        }

        ForgetCursor();
        var lastLeaf = k0;
        var emptied = false;
        k = k0;
        offset = left + 1;
        for (var remaining = count; remaining > 0; k++, offset = 0)
        {
            var leaf = LeafAt(k);
            var n = Math.Min(remaining, leaf.Count - offset);
            if (n == 0)
            {
                continue;
            }

            var tail = leaf.Count - offset - n;
            Array.Copy(leaf.Structs, offset + n, leaf.Structs, offset, tail);
            Array.Copy(leaf.Starts, offset + n, leaf.Starts, offset, tail);
            Array.Copy(leaf.Flags, offset + n, leaf.Flags, offset, tail);
            leaf.Count -= n;
            emptied |= leaf.Count == 0;
            remaining -= n;
            lastLeaf = k;
        }

        _count -= count;
        if (lastLeaf > k0)
        {
            // Across leaves: the emptied ones go (after k0, which keeps the struct before), the others that lost
            // their first positions start at the next struct.
            for (var j = lastLeaf; j > k0; j--)
            {
                if (emptied && _leaves[j].Count == 0)
                {
                    lastLeaf--;
                    RemoveLeaf(j);
                    if (_leaves == null)
                    {
                        break;
                    }
                }
                else
                {
                    _firstStarts[j] = _leaves[j].Starts[0];
                }
            }
        }

        if (_leaves != null)
        {
            for (var j = k0 + 1; j <= lastLeaf; j++)
            {
                _firstPositions[j] = _firstPositions[j - 1] + _leaves[j - 1].Count;
            }

            for (var j = lastLeaf + 1; j < _leafCount; j++)
            {
                _firstPositions[j] -= count;
            }
        }

        AddSum(k0, leftDelta + ValueAt(k0, left));
        RefreshSuccessor(k0, left);
        MergeIfSparse(k0 + 1);
        MergeIfSparse(k0);
    }

    /// <summary>Puts <paramref name="str"/> (a GC struct of the same clocks) in place of the struct at <paramref name="index"/>.</summary>
    public void Replace(int index, ItemRef str)
    {
        var k = Locate(index, out var offset);
        var leaf = LeafAt(k);
        Debug.Assert(leaf.Structs[offset].In(_slab).Id.Equals(str.Id) && leaf.Structs[offset].In(_slab).Length == str.Length && ValueAt(k, offset) == 0);
        leaf.Structs[offset] = str.Hold;
        SetFlags(k, offset, FlagsAt(k, offset));
        RefreshSuccessor(k, offset);
    }

    /// <summary>The last struct grew in place by <paramref name="length"/> (<see cref="ItemRef.ExtendRun"/>).</summary>
    public void LastExtended(int length)
    {
        _end += length;
        var k = _leafCount - 1;
        var leaf = LeafAt(k);
        Debug.Assert(_end == leaf.Structs[leaf.Count - 1].In(_slab).Id.Clock + leaf.Structs[leaf.Count - 1].In(_slab).Length);
        if ((leaf.Flags[leaf.Count - 1] & Visible) != 0)
        {
            AddSum(k, length);
        }
    }

    /// <summary>
    /// The index of the struct that holds <paramref name="clock"/>: the leaf by its first start, then a binary search
    /// on the leaf's starts, after a look at the last one (integrating in clock order mostly asks for it), with Yjs'
    /// pivot.
    /// </summary>
    public int FindIndex(long clock)
    {
        if (_leaves == null)
        {
            return FindIndex(new Starts(_single.Starts, _single.Count, _end), clock);
        }

        CheckInList(clock);
        var k = LeafOfClock(clock);
        var leaf = _leaves[k];
        return _firstPositions[k] + FindIndex(new Starts(leaf.Starts, leaf.Count, NextFirstStart(k)), clock);
    }

    /// <inheritdoc cref="FindIndex{T}(T, long, ref int)"/>
    public int FindIndex(long clock, ref int hint)
    {
        if (_leaves == null)
        {
            return FindIndex(new Starts(_single.Starts, _single.Count, _end), clock, ref hint);
        }

        CheckInList(clock);
        var k = LeafOfClock(clock);
        var leaf = _leaves[k];
        var offset = hint - _firstPositions[k];
        FindIndex(new Starts(leaf.Starts, leaf.Count, NextFirstStart(k)), clock, ref offset);
        return hint = _firstPositions[k] + offset;
    }

    /// <summary>The search of <see cref="FindIndex(long)"/> on any list of structs in clock order.</summary>
    internal static int FindIndex<T>(T order, long clock)
        where T : struct, IClockOrder
    {
        Debug.Assert(order.Count > 0);

        var left = 0;
        var right = order.Count - 1;
        var midClock = order.StartAt(right);
        var end = order.End;
        if (clock < 0 || clock >= end)
        {
            throw new InvalidOperationException($"Clock {clock} is not in the store (it ends at {end}).");
        }

        if (midClock <= clock)
        {
            return right;
        }

        // Yjs' pivot, from the order's first start (a leaf's starts do not begin at 0): first <= clock < end and
        // clock != midClock, so end - 1 > first: no division by zero, and the pivot is at most right. Every probe is
        // below the last position (the last starts after clock), so position mid + 1 exists.
        var first = order.StartAt(0);
        if (clock < first)
        {
            throw new InvalidOperationException($"Clock {clock} is not in the store.");
        }

        int midIndex = (int)(((clock - first) * right) / (end - 1 - first));
        while (left <= right)
        {
            midClock = order.StartAt(midIndex);

            if (midClock <= clock)
            {
                if (clock < order.StartAt(midIndex + 1))
                {
                    return midIndex;
                }

                left = midIndex + 1;
            }
            else
            {
                right = midIndex - 1;
            }

            midIndex = (left + right) / 2;
        }

        // Always check state before looking for a struct in StructStore
        // Therefore the case of not finding a struct is unexpected.
        throw new InvalidOperationException($"Clock {clock} is not in the store.");
    }

    /// <summary>
    /// <see cref="FindIndex{T}(T, long)"/> for lookups that move through the list in order (a delete set's ranges): an
    /// exponential search from <paramref name="hint"/>, the index the last lookup found, so a lookup costs the
    /// logarithm of how far it moved, not of the list. The hint is set to the index found; any value is valid.
    /// </summary>
    internal static int FindIndex<T>(T order, long clock, ref int hint)
        where T : struct, IClockOrder
    {
        var count = order.Count;
        if (clock < 0 || clock >= order.End)
        {
            return hint = FindIndex(order, clock);
        }

        var i = Math.Clamp(hint, 0, count - 1);
        int low, high;
        if (order.StartAt(i) <= clock)
        {
            // Gallop right: the struct at low starts at or before clock.
            low = i;
            var step = 1;
            while (true)
            {
                high = low + step;
                if (high >= count || order.StartAt(high) > clock)
                {
                    high = Math.Min(high, count) - 1;
                    break;
                }

                low = high;
                step *= 2;
            }
        }
        else
        {
            // Gallop left: the struct at high starts after clock... until one starts at or before it (the first does).
            high = i - 1;
            var step = 1;
            while (true)
            {
                low = high - step + 1;
                if (low <= 0)
                {
                    low = 0;
                    break;
                }

                if (order.StartAt(low) <= clock)
                {
                    break;
                }

                high = low - 1;
                step *= 2;
            }
        }

        // The last struct in [low, high] that starts at or before clock holds it.
        while (low < high)
        {
            var mid = low + ((high - low + 1) / 2);
            if (order.StartAt(mid) <= clock)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        if (clock < order.StartAt(low))
        {
            throw new InvalidOperationException($"Clock {clock} is not in the store.");
        }

        return hint = low;
    }

    /// <summary>What is wrong with the starts or the run-head index (against a recount), or null (for tests and Debug checks).</summary>
    internal string Violations()
    {
        if (LeafViolations() is { } violation)
        {
            return violation;
        }

        var position = 0;
        for (var k = 0; k < _leafCount; k++)
        {
            var leaf = LeafAt(k);
            for (var i = 0; i < leaf.Count; i++, position++)
            {
                var s = leaf.Structs[i].In(_slab);
                if (leaf.Starts[i] != s.Id.Clock)
                {
                    return $"position {position} starts at {leaf.Starts[i]}, its struct at {s.Id.Clock}";
                }

                var end = i + 1 < leaf.Count ? leaf.Starts[i + 1] : NextFirstStart(k);
                if (s.Id.Clock + s.Length != end)
                {
                    return $"position {position} ends at {s.Id.Clock + s.Length}, the next starts at {end}";
                }
            }
        }

        return IndexViolations();
    }

    /// <summary>A list of structs in clock order, contiguous by clock, as the searches read it.</summary>
    internal interface IClockOrder
    {
        int Count { get; }

        long End { get; }

        long StartAt(int index);
    }

    private readonly struct Starts(long[] starts, int count, long end) : IClockOrder
    {
        public int Count => count;

        public long End => end;

        public long StartAt(int index) => starts[index];
    }

    /// <summary>The structs an update was decoded into (one client's, contiguous by clock).</summary>
    internal readonly struct Decoded(List<AbstractStruct> structs) : IClockOrder
    {
        public int Count => structs.Count;

        public long End => structs[^1].Id.Clock + structs[^1].Length;

        public long StartAt(int index) => structs[index].Id.Clock;
    }

    /// <summary>The structs in order, without a copy (a <c>foreach</c> over the list).</summary>
    internal struct Enumerator(ClientStructs list)
    {
        private int _leaf;
        private int _offset = -1;

        public readonly ItemRef Current => list.LeafAt(_leaf).Structs[_offset].In(list._slab);

        public bool MoveNext()
        {
            if (_offset + 1 < list.LeafAt(_leaf).Count)
            {
                _offset++;
                return true;
            }

            if (_leaf + 1 < list._leafCount)
            {
                _leaf++;
                _offset = 0;
                return true;
            }

            return false;
        }
    }

    private void CheckInList(long clock)
    {
        if (clock < 0 || clock >= _end)
        {
            throw new InvalidOperationException($"Clock {clock} is not in the store (it ends at {_end}).");
        }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowOutOfRange(int index, int count) =>
        throw new ArgumentOutOfRangeException(nameof(index), index, $"The client has {count} structs.");
}
