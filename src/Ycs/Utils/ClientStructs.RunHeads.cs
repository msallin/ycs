// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

using System.Buffers;
using System.Diagnostics;

namespace Ycs;

/// <summary>
/// The run-head index of a client's structs (docs/design-item-layout.md, section 3.3): per position whether it is a
/// break of Yjs' marker walk-left, and per block of positions the visible length (countable, undeleted) and the number
/// of breaks.
/// </summary>
/// <remarks>
/// Yjs' walk-left steps from p to p.left while p.left has p's client and ends at p's clock. The struct that ends at a
/// struct's clock is unique: its predecessor in its client's clock order. So the walk steps from position i to i - 1
/// exactly when the item at i has the struct at i - 1 as its <see cref="Item.Left"/>, by reference ("linked"); every
/// other position is a break (position 0, a GC struct, a struct after a GC struct, an item whose left is another
/// client's or not its predecessor). A walk from i ends at the last break at or before i. The visible lengths give the
/// index it subtracts on the way. A position's visible length is its clock length when the flag says visible.
/// </remarks>
internal sealed partial class ClientStructs
{
    private const byte Break = 1;
    private const byte Visible = 2;

    // A live non-countable item (a format): the walks of a text read it, so they do not jump over it.
    private const byte Format = 4;

    /// <summary>
    /// The log2 of the block size of the lists made from now on: 512 positions (a split or a merge updates the sums of
    /// the blocks after it, a lookup scans at most a block on each side and the block sums between); the property
    /// tests make blocks of 2, so that their short lists cross block boundaries.
    /// </summary>
    internal static int BlockShiftForNewLists = 9;

    private readonly int _blockShift = BlockShiftForNewLists;

    // The flag values with Break set, and with Format set.
    private static readonly SearchValues<byte> Breaks = SearchValues.Create([Break, Break | Visible, Break | Format]);
    private static readonly SearchValues<byte> Formats = SearchValues.Create([Format, Break | Format]);

    // A rebuild of the sums after a merge, instead of their update, when more structs than this merged at once.
    private const int MaxIncrementalMerge = 32;

    // Per position: Break, Visible, Format; kept with the structs (FlagsAt says what they are).
    private byte[] _flags;

    // Per block of positions, the sum of the visible lengths. long: a block of int lengths can exceed int.
    private long[] _sums;

    // Per block of positions, how many are breaks: the searches for a run's head and end skip the blocks without one.
    private int[] _breaks;

    private int BlockOf(int position) => position >> _blockShift;

    /// <summary>The flags of the struct at <paramref name="i"/>, computed from it and its predecessor.</summary>
    private byte FlagsAt(int i)
    {
        if (_structs[i] is not Item item)
        {
            return Break;
        }

        var flags = i > 0 && _structs[i - 1] is Item before && ReferenceEquals(item.Left, before) ? (byte)0 : Break;
        if (!item.Deleted)
        {
            flags |= item.Countable ? Visible : Format;
        }

        return flags;
    }

    private long LengthAt(int i) => (i + 1 < _count ? _starts[i + 1] : _end) - _starts[i];

    /// <summary>The visible length of position <paramref name="i"/> as the index holds it (0 beyond the list).</summary>
    private long Value(int i) => i < _count && (_flags[i] & Visible) != 0 ? LengthAt(i) : 0;

    /// <summary>Sets the flags of position <paramref name="i"/>, whose current flags its block's break count holds.</summary>
    private void SetFlags(int i, byte flags)
    {
        _breaks[BlockOf(i)] += (flags & Break) - (_flags[i] & Break);
        _flags[i] = flags;
    }

    private void IndexAdded()
    {
        var i = _count - 1;
        _flags[i] = 0;
        SetFlags(i, FlagsAt(i));
        _sums[BlockOf(i)] += Value(i);
    }

    /// <summary>
    /// The right part of a split went in at <paramref name="index"/> (the arrays are shifted). The prefix sum at a
    /// position x after it is the old one at x - 1, and at <paramref name="index"/> the old one minus the right
    /// part: so each block from the left part's on changes by E(its end) - E(its start), E(x) = -Value(x) for x at
    /// or after <paramref name="index"/>, else 0. Every block after the one of <paramref name="index"/> gains, as a
    /// break, the position that moved in at its start and loses the one that moved out at its end.
    /// </summary>
    private void IndexSplit(int index)
    {
        // The new position is in no block's break count until its flags are set.
        _flags[index] = 0;
        var lastBlock = BlockOf(_count - 1);
        var carried = 0;
        for (var b = BlockOf(index); b <= lastBlock; b++)
        {
            var end = (b + 1) << _blockShift;
            var leaving = end < _count ? _flags[end] & Break : 0;
            _breaks[b] += carried - leaving;
            carried = leaving;
        }

        SetFlags(index, FlagsAt(index));
        if (index + 1 < _count)
        {
            SetFlags(index + 1, FlagsAt(index + 1));
        }

        Debug.Assert((_flags[index] & ~Break) == (_flags[index - 1] & ~Break));
        var last = BlockOf(_count - 1);
        var previous = 0L;
        for (var b = BlockOf(index - 1); b <= last; b++)
        {
            var end = (b + 1) << _blockShift;
            var next = end >= index ? -Value(end) : 0;
            _sums[b] += next - previous;
            previous = next;
        }
    }

    /// <summary>
    /// Before <paramref name="count"/> structs from <paramref name="index"/> are removed (they merged into the one
    /// before, which takes their visible length): the prefix sum at x at or after <paramref name="index"/> becomes
    /// the old one at x + <paramref name="count"/>, so each block changes by E(its end) - E(its start), E(x) the old
    /// visible length of [x, x + count). The break counts change the same way, with the breaks of [x, x + count) for
    /// E(x), once the removed positions are no breaks. Returns whether the sums and counts are rebuilt after the removal
    /// instead.
    /// </summary>
    private bool IndexMerging(int index, int count)
    {
        for (var k = index; k < index + count; k++)
        {
            Debug.Assert((_flags[k] & (Visible | Format)) == (_flags[index - 1] & (Visible | Format)));
        }

        if (count > MaxIncrementalMerge)
        {
            return true;
        }

        for (var k = index; k < index + count; k++)
        {
            SetFlags(k, (byte)(_flags[k] & ~Break));
        }

        var last = BlockOf(_count - 1);
        var previous = 0L;
        var previousBreaks = 0;
        for (var b = BlockOf(index - 1); b <= last; b++)
        {
            var end = (b + 1) << _blockShift;
            var next = 0L;
            var nextBreaks = 0;
            if (end >= index)
            {
                for (var k = end; k < end + count && k < _count; k++)
                {
                    next += Value(k);
                    nextBreaks += _flags[k] & Break;
                }
            }

            _sums[b] += next - previous;
            _breaks[b] += nextBreaks - previousBreaks;
            previous = next;
            previousBreaks = nextBreaks;
        }

        return false;
    }

    private void IndexMerged(int index, bool rebuild)
    {
        if (index < _count)
        {
            SetFlags(index, FlagsAt(index));
        }

        if (rebuild)
        {
            var first = BlockOf(index - 1);
            Array.Clear(_sums, first, _sums.Length - first);
            Array.Clear(_breaks, first, _breaks.Length - first);
            for (var k = first << _blockShift; k < _count; k++)
            {
                _sums[BlockOf(k)] += Value(k);
                _breaks[BlockOf(k)] += _flags[k] & Break;
            }
        }
    }

    /// <summary>
    /// The struct at <paramref name="index"/> changed what the index holds of it: it was deleted, or its left
    /// neighbour changed (an item integrated between it and its predecessor).
    /// </summary>
    internal void Refresh(int index)
    {
        var old = Value(index);
        SetFlags(index, FlagsAt(index));
        _sums[BlockOf(index)] += Value(index) - old;
    }

    /// <summary>The last break at or before position <paramref name="index"/> (position 0 is one).</summary>
    private int LastBreak(int index)
    {
        var block = BlockOf(index);
        var start = block << _blockShift;
        var at = _flags.AsSpan(start, index + 1 - start).LastIndexOfAny(Breaks);
        if (at >= 0)
        {
            return start + at;
        }

        block = _breaks.AsSpan(0, block).LastIndexOfAnyExcept(0);
        Debug.Assert(block >= 0, "Position 0 is a break.");
        start = block << _blockShift;
        return start + _flags.AsSpan(start, 1 << _blockShift).LastIndexOfAny(Breaks);
    }

    /// <summary>The first break at or after position <paramref name="from"/>, or -1.</summary>
    private int NextBreak(int from)
    {
        if (from >= _count)
        {
            return -1;
        }

        var block = BlockOf(from);
        var end = Math.Min((block + 1) << _blockShift, _count);
        var at = _flags.AsSpan(from, end - from).IndexOfAny(Breaks);
        if (at >= 0)
        {
            return from + at;
        }

        var next = _breaks.AsSpan(block + 1, BlockOf(_count - 1) - block).IndexOfAnyExcept(0);
        if (next < 0)
        {
            return -1;
        }

        var start = (block + 1 + next) << _blockShift;
        return start + _flags.AsSpan(start, Math.Min(1 << _blockShift, _count - start)).IndexOfAny(Breaks);
    }

    /// <summary>
    /// The position where Yjs' walk-left from position <paramref name="index"/> ends (the last break at or before it),
    /// and the visible length of the positions it steps onto (from there up to <paramref name="index"/>, exclusive).
    /// </summary>
    internal int RunHead(int index, out long passed)
    {
        var head = LastBreak(index);
        passed = VisibleBetween(head, index);
        return head;
    }

    /// <summary>
    /// How far a walk to the right that has <paramref name="target"/> visible elements to go can jump from position
    /// <paramref name="index"/> within its run (the positions after it up to the next break, which the walk reaches
    /// by <see cref="Item.Right"/> links: a linked position is its predecessor's right): the furthest position k such
    /// that the positions passed, [index, k), are shorter than <paramref name="target"/> (so every walk that stops at
    /// the item holding the target, or at the end of one, passes over them), and, with
    /// <paramref name="stopAtFormats"/>, hold no live format (a text's walk reads those). <paramref name="passed"/>
    /// is their visible length.
    /// </summary>
    internal int SeekRight(int index, long target, bool stopAtFormats, out long passed)
    {
        var next = NextBreak(index + 1);
        var last = next < 0 ? _count - 1 : next - 1;
        var block = 1 << _blockShift;
        var sum = 0L;
        var k = index;
        while (k < last)
        {
            if ((k & (block - 1)) == 0 && k + block <= last && sum + _sums[BlockOf(k)] < target
                && (!stopAtFormats || !_flags.AsSpan(k, block).ContainsAny(Formats)))
            {
                sum += _sums[BlockOf(k)];
                k += block;
                continue;
            }

            var value = Value(k);
            if (sum + value >= target || (stopAtFormats && (_flags[k] & Format) != 0))
            {
                break;
            }

            sum += value;
            k++;
        }

        passed = sum;
        return k;
    }

    /// <summary>
    /// How far Yjs' walk to the left while the index is beyond the target (the middle loop of <c>findMarker</c>) can
    /// jump from position <paramref name="index"/> within its run, <paramref name="target"/> being how far beyond it
    /// is: the leftmost position j at or after the run's head such that the positions it steps onto, [j, index), are
    /// shorter than <paramref name="target"/>. <paramref name="passed"/> is their visible length.
    /// </summary>
    internal int SeekLeft(int index, long target, out long passed)
    {
        var head = LastBreak(index);
        var block = 1 << _blockShift;
        var sum = 0L;
        var j = index;
        while (j > head)
        {
            if ((j & (block - 1)) == 0 && j - block >= head && sum + _sums[BlockOf(j - 1)] < target)
            {
                sum += _sums[BlockOf(j - 1)];
                j -= block;
                continue;
            }

            var value = Value(j - 1);
            if (sum + value >= target)
            {
                break;
            }

            sum += value;
            j--;
        }

        passed = sum;
        return j;
    }

    /// <summary>The visible length of the positions from <paramref name="from"/> up to <paramref name="to"/> (exclusive).</summary>
    private long VisibleBetween(int from, int to)
    {
        var sum = 0L;
        var k = from;
        for (; k < to && (k & ((1 << _blockShift) - 1)) != 0; k++)
        {
            sum += Value(k);
        }

        for (; k + (1 << _blockShift) <= to; k += 1 << _blockShift)
        {
            sum += _sums[BlockOf(k)];
        }

        for (; k < to; k++)
        {
            sum += Value(k);
        }

        return sum;
    }

    /// <summary>What is wrong with the run-head index against a recount, or null.</summary>
    private string IndexViolations()
    {
        for (var i = 0; i < _count; i++)
        {
            if (_flags[i] != FlagsAt(i))
            {
                return $"position {i} ({_structs[i].Id.Client}:{_structs[i].Id.Clock}) has flags {_flags[i]}, a recount gives {FlagsAt(i)}";
            }

            if (i > 0 && (_flags[i] & Break) == 0 && ((Item)_structs[i - 1]).Right != _structs[i])
            {
                return $"position {i} is linked to its left, which does not have it on its right";
            }
        }

        for (var b = 0; b < _sums.Length; b++)
        {
            var sum = 0L;
            for (var k = b << _blockShift; k < Math.Min((b + 1) << _blockShift, _count); k++)
            {
                sum += Value(k);
            }

            if (_sums[b] != sum)
            {
                return $"block {b} sums to {_sums[b]}, a recount gives {sum}";
            }

            var breaks = 0;
            for (var k = b << _blockShift; k < Math.Min((b + 1) << _blockShift, _count); k++)
            {
                breaks += _flags[k] & Break;
            }

            if (_breaks[b] != breaks)
            {
                return $"block {b} counts {_breaks[b]} breaks, a recount gives {breaks}";
            }
        }

        return null;
    }
}
