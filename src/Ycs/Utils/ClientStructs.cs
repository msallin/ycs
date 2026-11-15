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
/// them in an array of their own, so that finding the struct that holds a clock reads the array, not the structs.
/// </summary>
/// <remarks>
/// The structs are contiguous by clock: the end of position i is the start of i + 1, and <see cref="End"/> is the end
/// of the last one. Every change of the list goes through this class, which keeps the starts and the run-head index
/// (ClientStructs.RunHeads.cs) in step.
/// </remarks>
internal sealed partial class ClientStructs : IReadOnlyList<AbstractStruct>
{
    private AbstractStruct[] _structs;
    private long[] _starts;
    private int _count;
    private long _end;

    public ClientStructs(int capacity = 4)
    {
        capacity = Math.Max(capacity, 1);
        _structs = new AbstractStruct[capacity];
        _starts = new long[capacity];
        _flags = new byte[capacity];
        _sums = new long[BlockOf(capacity - 1) + 1];
        _breaks = new int[_sums.Length];
    }

    /// <summary>A copy of <paramref name="count"/> structs of <paramref name="source"/> from <paramref name="start"/>.</summary>
    public ClientStructs(List<AbstractStruct> source, int start, int count)
        : this(count)
    {
        for (var i = 0; i < count; i++)
        {
            Add(source[start + i]);
        }
    }

    public int Count => _count;

    /// <summary>The clock after the last struct (the client's state).</summary>
    public long End => _end;

    public AbstractStruct this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count)
            {
                ThrowOutOfRange(index, _count);
            }

            return _structs[index];
        }
    }

    public long StartAt(int index) => _starts[index];

    public ReadOnlySpan<AbstractStruct> AsSpan() => _structs.AsSpan(0, _count);

    public ReadOnlySpan<AbstractStruct>.Enumerator GetEnumerator() => AsSpan().GetEnumerator();

    IEnumerator<AbstractStruct> IEnumerable<AbstractStruct>.GetEnumerator()
    {
        for (var i = 0; i < _count; i++)
        {
            yield return _structs[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<AbstractStruct>)this).GetEnumerator();

    public void EnsureCapacity(int capacity)
    {
        if (capacity > _structs.Length)
        {
            Grow(capacity);
        }
    }

    /// <summary>Appends <paramref name="str"/>, which starts where the last struct ends (the caller checked).</summary>
    public void Add(AbstractStruct str)
    {
        if (_count == _structs.Length)
        {
            Grow(_count + 1);
        }

        _structs[_count] = str;
        _starts[_count] = str.Id.Clock;
        _count++;
        _end = str.Id.Clock + str.Length;
        IndexAdded();
    }

    /// <summary>Inserts the right part of a split at <paramref name="index"/>, right after its left part.</summary>
    public void InsertSplit(int index, AbstractStruct right)
    {
        Debug.Assert(index > 0 && index <= _count && _structs[index - 1].Id.Clock + _structs[index - 1].Length == right.Id.Clock);
        if (_count == _structs.Length)
        {
            Grow(_count + 1);
        }

        Array.Copy(_structs, index, _structs, index + 1, _count - index);
        Array.Copy(_starts, index, _starts, index + 1, _count - index);
        Array.Copy(_flags, index, _flags, index + 1, _count - index);
        _structs[index] = right;
        _starts[index] = right.Id.Clock;
        _count++;
        IndexSplit(index);
    }

    /// <summary>Removes the <paramref name="count"/> structs from <paramref name="index"/>, which merged into the one before.</summary>
    public void RemoveMerged(int index, int count)
    {
        Debug.Assert(index > 0 && count > 0 && index + count <= _count);
        var tail = _count - index - count;
        var rebuild = IndexMerging(index, count);
        Array.Copy(_structs, index + count, _structs, index, tail);
        Array.Copy(_starts, index + count, _starts, index, tail);
        Array.Copy(_flags, index + count, _flags, index, tail);
        Array.Clear(_structs, _count - count, count);
        _count -= count;
        IndexMerged(index, rebuild);
    }

    /// <summary>Puts <paramref name="str"/> (a GC struct of the same clocks) in place of the struct at <paramref name="index"/>.</summary>
    public void Replace(int index, AbstractStruct str)
    {
        Debug.Assert(_structs[index].Id.Equals(str.Id) && _structs[index].Length == str.Length && Value(index) == 0);
        _structs[index] = str;
        SetFlags(index, FlagsAt(index));
        if (index + 1 < _count)
        {
            SetFlags(index + 1, FlagsAt(index + 1));
        }
    }

    /// <summary>The last struct grew in place by <paramref name="length"/> (<see cref="Item.ExtendRun"/>).</summary>
    public void LastExtended(int length)
    {
        _end += length;
        Debug.Assert(_end == _structs[_count - 1].Id.Clock + _structs[_count - 1].Length);
        if ((_flags[_count - 1] & Visible) != 0)
        {
            _sums[BlockOf(_count - 1)] += length;
        }
    }

    /// <summary>
    /// The index of the struct that holds <paramref name="clock"/>: a binary search on the starts, after a look at the
    /// last one (integrating in clock order mostly asks for it), with Yjs' pivot.
    /// </summary>
    public int FindIndex(long clock) => FindIndex(new Starts(_starts, _count, _end), clock);

    /// <inheritdoc cref="FindIndex{T}(T, long, ref int)"/>
    public int FindIndex(long clock, ref int hint) => FindIndex(new Starts(_starts, _count, _end), clock, ref hint);

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

        // clock < end and clock != midClock, so end > 1: no division by zero, and the pivot is at most right. Every
        // probe is below the last position (the last starts after clock), so position mid + 1 exists.
        int midIndex = (int)((clock * right) / (end - 1));
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
        for (var i = 0; i < _count; i++)
        {
            var s = _structs[i];
            if (_starts[i] != s.Id.Clock)
            {
                return $"position {i} starts at {_starts[i]}, its struct at {s.Id.Clock}";
            }

            var end = i + 1 < _count ? _starts[i + 1] : _end;
            if (s.Id.Clock + s.Length != end)
            {
                return $"position {i} ends at {s.Id.Clock + s.Length}, the next starts at {end}";
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

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ThrowOutOfRange(int index, int count) =>
        throw new ArgumentOutOfRangeException(nameof(index), index, $"The client has {count} structs.");

    private void Grow(int min)
    {
        var capacity = Math.Max(min, _structs.Length * 2);
        var blocks = BlockOf(capacity - 1) + 1;

        // All allocated before any is assigned: an OutOfMemoryException leaves the list as it was.
        var structs = new AbstractStruct[capacity];
        var starts = new long[capacity];
        var flags = new byte[capacity];
        var sums = new long[blocks];
        var breaks = new int[blocks];
        Array.Copy(_structs, structs, _count);
        Array.Copy(_starts, starts, _count);
        Array.Copy(_flags, flags, _count);
        Array.Copy(_sums, sums, _sums.Length);
        Array.Copy(_breaks, breaks, _breaks.Length);
        _structs = structs;
        _starts = starts;
        _flags = flags;
        _sums = sums;
        _breaks = breaks;
    }
}
