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
/// break of Yjs' marker walk-left, and the visible length (countable, undeleted) per block of positions.
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
    /// The log2 of the block size of the lists made from now on: 64 positions; the property tests make blocks of 2,
    /// so that their short lists cross block boundaries.
    /// </summary>
    internal static int BlockShiftForNewLists = 6;

    private readonly int _blockShift = BlockShiftForNewLists;

    // The flag values with Break set.
    private static readonly SearchValues<byte> Breaks = SearchValues.Create([Break, Break | Visible, Break | Format]);

    // A rebuild of the sums after a merge, instead of their update, when more structs than this merged at once.
    private const int MaxIncrementalMerge = 32;

    // Per position: Break, Visible, Format; kept with the structs (FlagsAt says what they are).
    private byte[] _flags;

    // Per block of 64 positions, the sum of the visible lengths. long: a block of int lengths can exceed int.
    private long[] _sums;

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

    private void IndexAdded()
    {
        var i = _count - 1;
        _flags[i] = FlagsAt(i);
        _sums[BlockOf(i)] += Value(i);
    }

    /// <summary>
    /// The right part of a split went in at <paramref name="index"/> (the arrays are shifted). The prefix sum at a
    /// position x after it is the old one at x - 1, and at <paramref name="index"/> the old one minus the right
    /// part: so each block from the left part's on changes by E(its end) - E(its start), E(x) = -Value(x) for x at
    /// or after <paramref name="index"/>, else 0.
    /// </summary>
    private void IndexSplit(int index)
    {
        _flags[index] = FlagsAt(index);
        if (index + 1 < _count)
        {
            _flags[index + 1] = FlagsAt(index + 1);
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
    /// visible length of [x, x + count). Returns whether the sums are rebuilt after the removal instead.
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

        var last = BlockOf(_count - 1);
        var previous = 0L;
        for (var b = BlockOf(index - 1); b <= last; b++)
        {
            var end = (b + 1) << _blockShift;
            var next = 0L;
            if (end >= index)
            {
                for (var k = end; k < end + count && k < _count; k++)
                {
                    next += Value(k);
                }
            }

            _sums[b] += next - previous;
            previous = next;
        }

        return false;
    }

    private void IndexMerged(int index, bool rebuild)
    {
        if (index < _count)
        {
            _flags[index] = FlagsAt(index);
        }

        if (rebuild)
        {
            var first = BlockOf(index - 1);
            Array.Clear(_sums, first, _sums.Length - first);
            for (var k = first << _blockShift; k < _count; k++)
            {
                _sums[BlockOf(k)] += Value(k);
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
        _flags[index] = FlagsAt(index);
        _sums[BlockOf(index)] += Value(index) - old;
    }

    /// <summary>
    /// The position where Yjs' walk-left from position <paramref name="index"/> ends (the last break at or before it),
    /// and the visible length of the positions it steps onto (from there up to <paramref name="index"/>, exclusive).
    /// </summary>
    internal int RunHead(int index, out long passed)
    {
        var head = _flags.AsSpan(0, index + 1).LastIndexOfAny(Breaks);
        Debug.Assert(head >= 0, "Position 0 is a break.");
        passed = VisibleBetween(head, index);
        return head;
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
        }

        return null;
    }
}
