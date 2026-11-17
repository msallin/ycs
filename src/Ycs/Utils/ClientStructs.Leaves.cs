
using System.Diagnostics;
using System.Numerics;

namespace Ycs;

/// <summary>
/// The leaf order of a client's structs (docs/design-item-layout.md, section 1.3): the structs in leaves of at most
/// <see cref="LeafCapacity"/> positions, and from two leaves on a directory of them.
/// </summary>
/// <remarks>
/// Positions are global (0 to <see cref="Count"/> - 1); leaf k holds the positions from its first position on. The
/// leaves in order hold the structs contiguous by clock; every leaf holds 1 to the capacity structs (the one leaf of a
/// list without a directory: 0 before its first <see cref="Add"/>). A split shifts within its leaf and moves the later
/// leaves' first positions; a full leaf splits in two; a leaf at or below a quarter merges with a neighbour after a
/// removal when the two fit in one.
/// </remarks>
internal sealed partial class ClientStructs
{
    /// <summary>
    /// The capacity of a leaf: 128 positions (bench/Ycs.Compare/README.md, "The leaf order", measured 64, 128 and 256).
    /// </summary>
    internal const int LeafCapacity = 128;

    /// <summary>
    /// When not 0, the leaf capacity of the lists made from now on instead of <see cref="LeafCapacity"/> (at least 2).
    /// The property and corpus suites make leaves of 2 to 4, so that their short lists split and merge leaves.
    /// </summary>
    internal static int LeafCapacityForTests;

    // Per list, so that lists of different leaf capacities live side by side (the differential tests compare them in
    // one process) and a change of LeafCapacityForTests leaves the lists already made intact. At least 2: a full leaf
    // splits into two that are not empty.
    private readonly int _leafCapacity = LeafCapacityForTests == 0 ? LeafCapacity : Math.Max(2, LeafCapacityForTests);

    // The one leaf while there is no directory; null while there is one (the directory's leaf 0 then).
    private Leaf _single = null!;

    // The directory, null up to one leaf: the leaves in clock order (entries from _leafCount on are null), per leaf the
    // start of its first struct and its first position, a bit per leaf "has a break" and "has a live format" in 64-bit
    // words (bits from _leafCount on are 0), and a Fenwick tree of the leaves' visible sums (1-based: _sumTree[i]
    // holds the sums of the leaves [i - (i & -i), i)). All are kept with the leaves (a recount gives the same) but the
    // tree, which a change of the leaves marks stale and the next read rebuilds (_sumTreeStale).
    // The six are null together, exactly while there is no directory (_leaves == null tells); they are typed non-null so
    // the code that runs with a directory needs no annotation. _single is null exactly while there is one.
    private Leaf[] _leaves = null!;
    private long[] _firstStarts = null!;
    private int[] _firstPositions = null!;
    private ulong[] _hasBreak = null!;
    private ulong[] _hasFormat = null!;
    private long[] _sumTree = null!;
    private bool _sumTreeStale;

    // 1 without a directory.
    private int _leafCount = 1;

    // The leaf the last lookup by position or clock found, where the next lookup looks first; below _leafCount.
    private int _cursor;

    // The cursor's leaf's structs, first position and count, for the lookup by position (the indexer): valid while
    // _cursorCount is not 0. A change of the leaves' positions sets it to 0 (ForgetCursor); one that only appends
    // leaves it valid, the count it holds then being at most the leaf's.
    private ItemHandle[] _cursorStructs = [];
    private int _cursorFirst;
    private int _cursorCount;

    private void ForgetCursor() => _cursorCount = 0;

    private Leaf LeafAt(int k) => _leaves == null ? _single : _leaves[k];

    private int FirstPosition(int k) => _leaves == null ? 0 : _firstPositions[k];

    /// <summary>The start of the struct after leaf <paramref name="k"/>'s last one (the end of the list after the last leaf).</summary>
    private long NextFirstStart(int k) => k + 1 < _leafCount ? _firstStarts[k + 1] : _end;

    /// <summary>The leaf that holds position <paramref name="position"/> (0 to Count - 1), and the offset in it.</summary>
    private int Locate(int position, out int offset)
    {
        if (_leaves == null)
        {
            offset = position;
            return 0;
        }

        var k = LeafOf(position);
        offset = position - _firstPositions[k];
        return k;
    }

    private int LeafOf(int position)
    {
        if (unchecked((uint)(position - _cursorFirst)) < (uint)_cursorCount)
        {
            return _cursor;
        }

        var k = _cursor;
        if (k + 1 < _leafCount && unchecked((uint)(position - _firstPositions[k + 1])) < (uint)_leaves[k + 1].Count)
        {
            return SetCursor(k + 1);
        }

        if (unchecked((uint)(position - _firstPositions[k])) < (uint)_leaves[k].Count)
        {
            return SetCursor(k);
        }

        var low = 0;
        var high = _leafCount - 1;
        while (low < high)
        {
            var mid = (low + high + 1) >> 1;
            if (_firstPositions[mid] <= position)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return SetCursor(low);
    }

    private int SetCursor(int k)
    {
        var leaf = _leaves[k];
        _cursor = k;
        _cursorStructs = leaf.Structs;
        _cursorFirst = _firstPositions[k];
        _cursorCount = leaf.Count;
        return k;
    }

    /// <summary>The leaf that holds <paramref name="clock"/> (in the list), with a directory.</summary>
    private int LeafOfClock(long clock)
    {
        // Integrating in clock order mostly asks for the last leaf, an ordered lookup for the one of the last lookup
        // or the next.
        var starts = _firstStarts;
        var last = _leafCount - 1;
        if (starts[last] <= clock)
        {
            return SetCursor(last);
        }

        var k = _cursor;
        if (starts[k] <= clock)
        {
            // k < last, and k + 1 < last when clock is at or after k + 1's start (it is before the last's).
            if (clock < starts[k + 1])
            {
                return k;
            }

            if (clock < starts[k + 2])
            {
                return SetCursor(k + 1);
            }
        }

        // The leaves before the last: the last one that starts at or before clock (leaf 0 for a clock below the list's
        // first start, whose own search then throws as a list without a directory does).
        var low = 0;
        var high = last - 1;
        while (low < high)
        {
            var mid = (low + high + 1) >> 1;
            if (starts[mid] <= clock)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return SetCursor(low);
    }

    /// <summary>The one leaf grown to hold at least <paramref name="min"/> structs (at most a leaf's capacity).</summary>
    private void GrowSingle(int min)
    {
        var old = _single;
        var grown = new Leaf(Math.Min(Math.Max(min, old.Capacity * 2), _leafCapacity));
        Array.Copy(old.Structs, grown.Structs, old.Count);
        Array.Copy(old.Starts, grown.Starts, old.Count);
        Array.Copy(old.Flags, grown.Flags, old.Count);
        grown.Count = old.Count;
        grown.Sum = old.Sum;
        grown.Breaks = old.Breaks;
        grown.Formats = old.Formats;
        _single = grown;
    }

    /// <summary>
    /// Makes room in the directory for <paramref name="leaves"/> leaves, making the directory if there is none (its
    /// first leaf the one leaf). Allocates everything before it assigns anything, so an OutOfMemoryException leaves
    /// the list as it was.
    /// </summary>
    private void ReserveLeaves(int leaves)
    {
        if (_leaves != null && leaves <= _leaves.Length)
        {
            return;
        }

        var capacity = Math.Max(leaves, _leaves == null ? 4 : _leaves.Length * 2);
        var words = (capacity + 63) >> 6;
        var all = new Leaf[capacity];
        var firstStarts = new long[capacity];
        var firstPositions = new int[capacity];
        var hasBreak = new ulong[words];
        var hasFormat = new ulong[words];
        var sumTree = new long[capacity + 1];
        if (_leaves == null)
        {
            all[0] = _single;
            firstStarts[0] = _single.Starts[0];
            hasBreak[0] = _single.Breaks != 0 ? 1UL : 0;
            hasFormat[0] = _single.Formats != 0 ? 1UL : 0;
        }
        else
        {
            Array.Copy(_leaves, all, _leafCount);
            Array.Copy(_firstStarts, firstStarts, _leafCount);
            Array.Copy(_firstPositions, firstPositions, _leafCount);
            Array.Copy(_hasBreak, hasBreak, _hasBreak.Length);
            Array.Copy(_hasFormat, hasFormat, _hasFormat.Length);
            Array.Copy(_sumTree, sumTree, _sumTree.Length);
        }

        _leaves = all;
        _firstStarts = firstStarts;
        _firstPositions = firstPositions;
        _hasBreak = hasBreak;
        _hasFormat = hasFormat;
        _sumTree = sumTree;
        _sumTreeStale = true;
        _single = null!;
    }

    /// <summary>
    /// Puts <paramref name="leaf"/> (not empty) into the directory at <paramref name="k"/>, after
    /// <see cref="ReserveLeaves"/>, with its bits clear: the positions of the later leaves stay, its own follows
    /// from leaf <paramref name="k"/> - 1's.
    /// </summary>
    private void InsertLeaf(int k, Leaf leaf)
    {
        Array.Copy(_leaves, k, _leaves, k + 1, _leafCount - k);
        Array.Copy(_firstStarts, k, _firstStarts, k + 1, _leafCount - k);
        Array.Copy(_firstPositions, k, _firstPositions, k + 1, _leafCount - k);
        _leaves[k] = leaf;
        _firstStarts[k] = leaf.Starts[0];
        _firstPositions[k] = k == 0 ? 0 : _firstPositions[k - 1] + _leaves[k - 1].Count;
        _leafCount++;
        InsertBit(_hasBreak, k, _leafCount);
        InsertBit(_hasFormat, k, _leafCount);
        _sumTreeStale = true;
    }

    /// <summary>Takes leaf <paramref name="k"/> out of the directory; with one leaf left, the directory goes.</summary>
    private void RemoveLeaf(int k)
    {
        RemoveBit(_hasBreak, k, _leafCount);
        RemoveBit(_hasFormat, k, _leafCount);
        _sumTreeStale = true;
        _leafCount--;
        Array.Copy(_leaves, k + 1, _leaves, k, _leafCount - k);
        Array.Copy(_firstStarts, k + 1, _firstStarts, k, _leafCount - k);
        Array.Copy(_firstPositions, k + 1, _firstPositions, k, _leafCount - k);
        _leaves[_leafCount] = null!;
        _cursor = 0;
        ForgetCursor();
        if (_leafCount == 1)
        {
            _single = _leaves[0];
            _leaves = null!;
            _firstStarts = null!;
            _firstPositions = null!;
            _hasBreak = null!;
            _hasFormat = null!;
            _sumTree = null!;
        }
    }

    /// <summary>The tree of the leaves' sums, from the leaves.</summary>
    private void RebuildSumTree()
    {
        for (var k = 0; k < _leafCount; k++)
        {
            _sumTree[k + 1] = _leaves[k].Sum;
        }

        Array.Clear(_sumTree, _leafCount + 1, _sumTree.Length - _leafCount - 1);
        for (var i = 1; i <= _leafCount; i++)
        {
            var parent = i + (i & -i);
            if (parent <= _leafCount)
            {
                _sumTree[parent] += _sumTree[i];
            }
        }

        _sumTreeStale = false;
    }

    /// <summary>Splits the full leaf <paramref name="k"/> into two halves.</summary>
    private void SplitLeaf(int k)
    {
        var leaf = LeafAt(k);
        Debug.Assert(leaf.Count == _leafCapacity);
        var right = new Leaf(_leafCapacity);
        ReserveLeaves(_leafCount + 1);
        var half = leaf.Count / 2;
        var moved = leaf.Count - half;
        Array.Copy(leaf.Structs, half, right.Structs, 0, moved);
        Array.Copy(leaf.Starts, half, right.Starts, 0, moved);
        Array.Copy(leaf.Flags, half, right.Flags, 0, moved);
        leaf.Count = half;
        right.Count = moved;
        InsertLeaf(k + 1, right);

        // The halves' sums read the starts after them, which the directory now holds.
        RecountLeaf(k);
        RecountLeaf(k + 1);
        SetLeafBits(k);
        SetLeafBits(k + 1);
    }

    /// <summary>Appends <paramref name="str"/> in a new leaf after the last one, which is full.</summary>
    private void AppendLeaf(ItemRef str)
    {
        var leaf = new Leaf(_leafCapacity);
        ReserveLeaves(_leafCount + 1);
        leaf.Structs[0] = str.Hold;
        leaf.Starts[0] = str.Clock;
        leaf.Count = 1;
        InsertLeaf(_leafCount, leaf);
        _count++;
        _end = str.Clock + str.Length;
    }

    /// <summary>Merges leaf <paramref name="k"/> with a neighbour when it holds at most a quarter of a leaf and they fit in one.</summary>
    private void MergeIfSparse(int k)
    {
        if (_leaves == null || k >= _leafCount || _leaves[k].Count > Math.Max(1, _leafCapacity / 4))
        {
            return;
        }

        if (k + 1 < _leafCount && _leaves[k].Count + _leaves[k + 1].Count <= _leafCapacity)
        {
            MergeLeaves(k);
        }
        else if (k > 0 && _leaves[k - 1].Count + _leaves[k].Count <= _leafCapacity)
        {
            MergeLeaves(k - 1);
        }
    }

    /// <summary>Appends leaf <paramref name="k"/> + 1 to leaf <paramref name="k"/> (no position's visible length changes).</summary>
    private void MergeLeaves(int k)
    {
        var leaf = _leaves[k];
        var next = _leaves[k + 1];
        Array.Copy(next.Structs, 0, leaf.Structs, leaf.Count, next.Count);
        Array.Copy(next.Starts, 0, leaf.Starts, leaf.Count, next.Count);
        Array.Copy(next.Flags, 0, leaf.Flags, leaf.Count, next.Count);
        leaf.Count += next.Count;
        leaf.Sum += next.Sum;
        leaf.Breaks += next.Breaks;
        leaf.Formats += next.Formats;
        RemoveLeaf(k + 1);
        if (_leaves != null)
        {
            SetLeafBits(k);
        }
    }

    /// <summary>The sum, break count and format count of leaf <paramref name="k"/> from its positions' flags and starts.</summary>
    private void RecountLeaf(int k)
    {
        var leaf = LeafAt(k);
        leaf.Sum = LeafVisible(k, 0, leaf.Count);
        leaf.Breaks = 0;
        leaf.Formats = 0;
        for (var i = 0; i < leaf.Count; i++)
        {
            leaf.Breaks += leaf.Flags[i] & Break;
            leaf.Formats += (leaf.Flags[i] & Format) >> 2;
        }
    }

    private void SetLeafBits(int k)
    {
        SetBit(_hasBreak, k, _leaves[k].Breaks != 0);
        SetBit(_hasFormat, k, _leaves[k].Formats != 0);
    }

    /// <summary>Adds <paramref name="delta"/> to leaf <paramref name="k"/>'s visible sum, and to the tree.</summary>
    private void AddSum(int k, long delta)
    {
        LeafAt(k).Sum += delta;
        if (_leaves != null && !_sumTreeStale)
        {
            for (var i = k + 1; i <= _leafCount; i += i & -i)
            {
                _sumTree[i] += delta;
            }
        }
    }

    /// <summary>The visible sum of the leaves before leaf <paramref name="m"/> (with a directory).</summary>
    private long SumBefore(int m)
    {
        if (_sumTreeStale)
        {
            RebuildSumTree();
        }

        var sum = 0L;
        for (var i = m; i > 0; i -= i & -i)
        {
            sum += _sumTree[i];
        }

        return sum;
    }

    /// <summary>The largest m (0 to the leaf count) such that the leaves before leaf m sum to at most <paramref name="y"/>, or -1 when <paramref name="y"/> is negative.</summary>
    private int LeavesUpTo(long y)
    {
        if (y < 0)
        {
            return -1;
        }

        if (_sumTreeStale)
        {
            RebuildSumTree();
        }

        var m = 0;
        for (var step = 1 << (31 - BitOperations.LeadingZeroCount((uint)_leafCount)); step > 0; step >>= 1)
        {
            var next = m + step;
            if (next <= _leafCount && _sumTree[next] <= y)
            {
                m = next;
                y -= _sumTree[next];
            }
        }

        return m;
    }

    /// <summary>Moves the bits from <paramref name="k"/> on one up and clears bit <paramref name="k"/>; <paramref name="count"/> bits are in use after.</summary>
    private static void InsertBit(ulong[] words, int k, int count)
    {
        var w = k >> 6;
        for (var i = ((count + 63) >> 6) - 1; i > w; i--)
        {
            words[i] = (words[i] << 1) | (words[i - 1] >> 63);
        }

        var below = (1UL << k) - 1;
        if ((k & 63) == 0)
        {
            below = 0;
        }

        words[w] = (words[w] & below) | ((words[w] & ~below) << 1);
    }

    /// <summary>Takes bit <paramref name="k"/> out, moving the bits above it one down; <paramref name="count"/> bits were in use before.</summary>
    private static void RemoveBit(ulong[] words, int k, int count)
    {
        var w = k >> 6;
        var below = (k & 63) == 0 ? 0 : (1UL << k) - 1;
        words[w] = (words[w] & below) | ((words[w] >> 1) & ~below);
        for (var i = w; i + 1 < (count + 63) >> 6; i++)
        {
            words[i] |= words[i + 1] << 63;
            words[i + 1] >>= 1;
        }
    }

    private static void SetBit(ulong[] words, int k, bool value)
    {
        if (value)
        {
            words[k >> 6] |= 1UL << k;
        }
        else
        {
            words[k >> 6] &= ~(1UL << k);
        }
    }

    /// <summary>The last leaf before leaf <paramref name="k"/> whose bit is set in <paramref name="words"/> (one is).</summary>
    private static int PreviousWithBit(ulong[] words, int k)
    {
        var j = k - 1;
        var w = j >> 6;
        var word = words[w] & (ulong.MaxValue >> (63 - (j & 63)));
        while (word == 0)
        {
            word = words[--w];
        }

        return (w << 6) + 63 - BitOperations.LeadingZeroCount(word);
    }

    /// <summary>The first leaf after leaf <paramref name="k"/> whose bit is set in <paramref name="words"/>, or -1.</summary>
    private int NextWithBit(ulong[] words, int k)
    {
        var j = k + 1;
        if (j >= _leafCount)
        {
            return -1;
        }

        var w = j >> 6;
        var used = (_leafCount + 63) >> 6;
        var word = words[w] & (ulong.MaxValue << (j & 63));
        while (word == 0)
        {
            if (++w >= used)
            {
                return -1;
            }

            word = words[w];
        }

        return (w << 6) + BitOperations.TrailingZeroCount(word);
    }

    /// <summary>Whether a leaf other than the first starts at <paramref name="position"/> (for tests).</summary>
    internal bool IsLeafStartForTests(int position) => _leaves != null && Array.BinarySearch(_firstPositions, 1, _leafCount - 1, position) >= 0;

    /// <summary>What is wrong with the leaves and the directory against a recount, or null.</summary>
    private string? LeafViolations()
    {
        if (_leaves == null)
        {
            if (_leafCount != 1 || _single.Count != _count || _single.Capacity > _leafCapacity)
            {
                return $"one leaf of {_single.Count} structs (capacity {_single.Capacity}) holds a list of {_count} in {_leafCount} leaves";
            }

            return LeafCountViolations(0);
        }

        if (_leafCount < 2 || _leafCount > _leaves.Length || _single != null || _cursor >= _leafCount)
        {
            return $"a directory of {_leafCount} leaves (room for {_leaves.Length}, cursor {_cursor})";
        }

        if (_cursorCount != 0 && (_cursorStructs != _leaves[_cursor].Structs || _cursorFirst != _firstPositions[_cursor] || _cursorCount > _leaves[_cursor].Count))
        {
            return $"the cursor's leaf {_cursor} is cached at position {_cursorFirst} with {_cursorCount} structs, it is at {_firstPositions[_cursor]} with {_leaves[_cursor].Count}";
        }

        var position = 0;
        var tree = new long[_leafCount + 1];
        for (var k = 0; k < _leaves.Length; k++)
        {
            var leaf = _leaves[k];
            if (k >= _leafCount)
            {
                if (leaf != null || (k < _hasBreak.Length << 6 && ((_hasBreak[k >> 6] | _hasFormat[k >> 6]) & (1UL << k)) != 0))
                {
                    return $"the directory holds a leaf or a bit at {k}, beyond its {_leafCount} leaves";
                }

                continue;
            }

            if (leaf.Count < 1 || leaf.Count > _leafCapacity || leaf.Capacity != _leafCapacity)
            {
                return $"leaf {k} holds {leaf.Count} structs (capacity {leaf.Capacity}, leaves of {_leafCapacity})";
            }

            if (_firstPositions[k] != position || _firstStarts[k] != leaf.Starts[0])
            {
                return $"leaf {k} is at position {_firstPositions[k]} from clock {_firstStarts[k]}, a recount gives {position} from {leaf.Starts[0]}";
            }

            if (LeafCountViolations(k) is { } violation)
            {
                return violation;
            }

            var bits = (((_hasBreak[k >> 6] >> k) & 1) != 0, ((_hasFormat[k >> 6] >> k) & 1) != 0);
            if (bits != (leaf.Breaks != 0, leaf.Formats != 0))
            {
                return $"leaf {k} has the bits (break, format) {bits}, its counts {leaf.Breaks} and {leaf.Formats}";
            }

            position += leaf.Count;
            tree[k + 1] = leaf.Sum;
        }

        if (position != _count)
        {
            return $"the leaves hold {position} structs, the list {_count}";
        }

        for (var i = 1; i <= _leafCount; i++)
        {
            if (i + (i & -i) <= _leafCount)
            {
                tree[i + (i & -i)] += tree[i];
            }

            if (!_sumTreeStale && _sumTree[i] != tree[i])
            {
                return $"the sum tree holds {_sumTree[i]} at {i}, a recount gives {tree[i]}";
            }
        }

        return null;
    }

    private string? LeafCountViolations(int k)
    {
        var leaf = LeafAt(k);
        var sum = LeafVisible(k, 0, leaf.Count);
        var breaks = 0;
        var formats = 0;
        for (var i = 0; i < leaf.Capacity; i++)
        {
            if (i >= leaf.Count)
            {
                continue;
            }

            breaks += leaf.Flags[i] & Break;
            formats += (leaf.Flags[i] & Format) >> 2;
        }

        if ((leaf.Sum, leaf.Breaks, leaf.Formats) != (sum, breaks, formats))
        {
            return $"leaf {k} has (sum, breaks, formats) {(leaf.Sum, leaf.Breaks, leaf.Formats)}, a recount gives {(sum, breaks, formats)}";
        }

        return null;
    }

    /// <summary>A run of positions of one client's list, and what the run-head index holds of them.</summary>
    private sealed class Leaf(int capacity)
    {
        // The entries from Count on are unspecified.
        public readonly ItemHandle[] Structs = new ItemHandle[capacity];

        public readonly long[] Starts = new long[capacity];

        // Per position: Break, Visible, Format (FlagsAt says what they are).
        public readonly byte[] Flags = new byte[capacity];

        public int Count;

        // The visible length of its positions. long: a leaf of int lengths can exceed int.
        public long Sum;

        // How many of its positions are breaks, and live formats.
        public int Breaks;

        public int Formats;

        public int Capacity => Structs.Length;
    }
}
