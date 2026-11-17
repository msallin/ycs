#nullable disable // Not annotated for nullable reference types yet.

using System.Buffers;
using System.Diagnostics;

namespace Ycs;

/// <summary>
/// The run-head index of a client's structs (docs/design-item-layout.md, section 1.2): per position whether it is a
/// break of Yjs' marker walk-left, and per leaf the visible length (countable, undeleted), the number of breaks and of
/// live formats.
/// </summary>
/// <remarks>
/// Yjs' walk-left steps from p to p.left while p.left has p's client and ends at p's clock. The struct that ends at a
/// struct's clock is unique: its predecessor in its client's clock order. So the walk steps from position i to i - 1
/// exactly when the item at i has the struct at i - 1 as its <see cref="ItemRef.Left"/>, by reference ("linked"); every
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

    // The flag values with Break set, and with Format set.
    private static readonly SearchValues<byte> Breaks = SearchValues.Create([Break, Break | Visible, Break | Format]);

    /// <summary>The flags of the struct at offset <paramref name="i"/> of leaf <paramref name="k"/>, computed from it and its predecessor.</summary>
    private byte FlagsAt(int k, int i)
    {
        var leaf = LeafAt(k);
        var item = leaf.Structs[i].In(_slab);
        if (!item.IsItem)
        {
            return Break;
        }

        var before = i > 0 ? leaf.Structs[i - 1].In(_slab) : k > 0 ? _leaves[k - 1].Structs[_leaves[k - 1].Count - 1].In(_slab) : default;
        var flags = before.IsItem && item.Left == before ? (byte)0 : Break;
        if (!item.Deleted)
        {
            flags |= item.Countable ? Visible : Format;
        }

        return flags;
    }

    /// <summary>The visible length of offset <paramref name="i"/> of leaf <paramref name="k"/> as the index holds it: its clock length when the flag says visible.</summary>
    private long ValueAt(int k, int i)
    {
        var leaf = LeafAt(k);
        if ((leaf.Flags[i] & Visible) == 0)
        {
            return 0;
        }

        return (i + 1 < leaf.Count ? leaf.Starts[i + 1] : NextFirstStart(k)) - leaf.Starts[i];
    }

    /// <summary>The visible length of the offsets [<paramref name="from"/>, <paramref name="to"/>) of leaf <paramref name="k"/>.</summary>
    private long LeafVisible(int k, int from, int to)
    {
        var leaf = LeafAt(k);
        var flags = leaf.Flags;
        var starts = leaf.Starts;
        var sum = 0L;
        var inner = Math.Min(to, leaf.Count - 1);
        for (var i = from; i < inner; i++)
        {
            if ((flags[i] & Visible) != 0)
            {
                sum += starts[i + 1] - starts[i];
            }
        }

        if (from < to && to == leaf.Count && (flags[to - 1] & Visible) != 0)
        {
            sum += NextFirstStart(k) - starts[to - 1];
        }

        return sum;
    }

    /// <summary>
    /// Sets the flags of offset <paramref name="i"/> of leaf <paramref name="k"/>, whose current flags its leaf's
    /// counts and the directory's bits hold. The leaf's sum is the caller's: a change of Visible changes it by the
    /// position's length.
    /// </summary>
    private void SetFlags(int k, int i, byte flags)
    {
        var leaf = LeafAt(k);
        var changed = leaf.Flags[i] ^ flags;
        leaf.Flags[i] = flags;
        if ((changed & Break) != 0)
        {
            leaf.Breaks += (flags & Break) != 0 ? 1 : -1;
            if (_leaves != null)
            {
                SetBit(_hasBreak, k, leaf.Breaks != 0);
            }
        }

        if ((changed & Format) != 0)
        {
            leaf.Formats += (flags & Format) != 0 ? 1 : -1;
            if (_leaves != null)
            {
                SetBit(_hasFormat, k, leaf.Formats != 0);
            }
        }
    }

    /// <summary>Recomputes the flags of the position after offset <paramref name="i"/> of leaf <paramref name="k"/>, if there is one (its predecessor changed).</summary>
    private void RefreshSuccessor(int k, int i)
    {
        if (i + 1 < LeafAt(k).Count)
        {
            SetFlags(k, i + 1, FlagsAt(k, i + 1));
        }
        else if (k + 1 < _leafCount)
        {
            SetFlags(k + 1, 0, FlagsAt(k + 1, 0));
        }
    }

    /// <summary>
    /// The struct at <paramref name="index"/> changed what the index holds of it: it was deleted, or its left
    /// neighbour changed (an item integrated between it and its predecessor).
    /// </summary>
    internal void Refresh(int index)
    {
        var k = Locate(index, out var i);
        var old = ValueAt(k, i);
        SetFlags(k, i, FlagsAt(k, i));
        var value = ValueAt(k, i);
        if (value != old)
        {
            AddSum(k, value - old);
        }
    }

    /// <summary>The last break at or before position <paramref name="index"/> (position 0 is one).</summary>
    private int LastBreak(int index)
    {
        var k = Locate(index, out var i);
        var leaf = LeafAt(k);
        var at = leaf.Flags.AsSpan(0, i + 1).LastIndexOfAny(Breaks);
        if (at >= 0)
        {
            return FirstPosition(k) + at;
        }

        Debug.Assert(k > 0, "Position 0 is a break.");
        k = PreviousWithBit(_hasBreak, k);
        leaf = _leaves[k];
        return _firstPositions[k] + leaf.Flags.AsSpan(0, leaf.Count).LastIndexOfAny(Breaks);
    }

    /// <summary>The first break at or after position <paramref name="from"/>, or -1.</summary>
    private int NextBreak(int from)
    {
        if (from >= _count)
        {
            return -1;
        }

        var k = Locate(from, out var i);
        var leaf = LeafAt(k);
        var at = leaf.Flags.AsSpan(i, leaf.Count - i).IndexOfAny(Breaks);
        if (at >= 0)
        {
            return FirstPosition(k) + i + at;
        }

        if (_leaves == null || (k = NextWithBit(_hasBreak, k)) < 0)
        {
            return -1;
        }

        leaf = _leaves[k];
        return _firstPositions[k] + leaf.Flags.AsSpan(0, leaf.Count).IndexOfAny(Breaks);
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
    /// by <see cref="ItemRef.Right"/> links: a linked position is its predecessor's right): the furthest position k such
    /// that the positions passed, [index, k), are shorter than <paramref name="target"/> (so every walk that stops at
    /// the item holding the target, or at the end of one, passes over them), and, with
    /// <paramref name="stopAtFormats"/>, hold no live format (a text's walk reads those). <paramref name="passed"/>
    /// is their visible length.
    /// </summary>
    internal int SeekRight(int index, long target, bool stopAtFormats, out long passed)
    {
        var next = NextBreak(index + 1);
        var last = next < 0 ? _count - 1 : next - 1;
        var lastLeaf = Locate(last, out var lastOffset);
        var k = Locate(index, out var i);
        var sum = 0L;
        while (true)
        {
            // Position by position in leaf k, up to the run's last position, which the walk never passes.
            var leaf = LeafAt(k);
            var flags = leaf.Flags;
            var starts = leaf.Starts;
            var end = k == lastLeaf ? lastOffset : leaf.Count;
            for (; i < end; i++)
            {
                var value = (flags[i] & Visible) == 0 ? 0 : (i + 1 < leaf.Count ? starts[i + 1] : NextFirstStart(k)) - starts[i];
                if (sum + value >= target || (stopAtFormats && (flags[i] & Format) != 0))
                {
                    passed = sum;
                    return FirstPosition(k) + i;
                }

                sum += value;
            }

            if (k == lastLeaf)
            {
                passed = sum;
                return last;
            }

            // Whole leaves before the run's last leaf (and, with stopAtFormats, before the next leaf with a live
            // format) while the sum stays below the target: up to the leaf c in which it reaches it, the smallest c
            // with SumBefore(c + 1) >= SumBefore(k + 1) + target - sum.
            var bound = lastLeaf;
            if (stopAtFormats && NextWithBit(_hasFormat, k) is var format && format >= 0 && format < bound)
            {
                bound = format;
            }

            if (bound > k + 1)
            {
                var before = SumBefore(k + 1);
                var reaching = LeavesUpTo(before + target - sum - 1);
                var stop = Math.Min(reaching, bound);
                sum += SumBefore(stop) - before;
                k = stop;
            }
            else
            {
                k++;
            }

            i = 0;
        }
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
        var headLeaf = Locate(head, out var headOffset);
        var k = Locate(index, out var j);
        var sum = 0L;
        while (true)
        {
            // Position by position in leaf k, down to the run's head.
            var leaf = LeafAt(k);
            var flags = leaf.Flags;
            var starts = leaf.Starts;
            var low = k == headLeaf ? headOffset : 0;
            for (; j > low; j--)
            {
                var value = (flags[j - 1] & Visible) == 0 ? 0 : (j < leaf.Count ? starts[j] : NextFirstStart(k)) - starts[j - 1];
                if (sum + value >= target)
                {
                    passed = sum;
                    return FirstPosition(k) + j;
                }

                sum += value;
            }

            if (k == headLeaf)
            {
                passed = sum;
                return head;
            }

            // Whole leaves after the head's leaf while the sum stays below the target: down to the leaf m in which it
            // reaches it, the largest m with SumBefore(m) <= SumBefore(k) + sum - target.
            k--;
            if (k > headLeaf)
            {
                var top = SumBefore(k + 1);
                var stop = Math.Max(LeavesUpTo(top + sum - target), headLeaf);
                sum += top - SumBefore(stop + 1);
                k = stop;
            }

            j = LeafAt(k).Count;
        }
    }

    /// <summary>The visible length of the positions from <paramref name="from"/> up to <paramref name="to"/> (exclusive).</summary>
    private long VisibleBetween(int from, int to)
    {
        if (from >= to)
        {
            return 0;
        }

        var first = Locate(from, out var i);
        var last = Locate(to - 1, out var end);
        if (first == last)
        {
            return LeafVisible(first, i, end + 1);
        }

        return LeafVisible(first, i, LeafAt(first).Count) + SumBefore(last) - SumBefore(first + 1) + LeafVisible(last, 0, end + 1);
    }

    /// <summary>What is wrong with the run-head index against a recount, or null.</summary>
    private string IndexViolations()
    {
        var position = 0;
        for (var k = 0; k < _leafCount; k++)
        {
            var leaf = LeafAt(k);
            for (var i = 0; i < leaf.Count; i++, position++)
            {
                if (leaf.Flags[i] != FlagsAt(k, i))
                {
                    return $"position {position} ({leaf.Structs[i].In(_slab).Id.Client}:{leaf.Structs[i].In(_slab).Id.Clock}) has flags {leaf.Flags[i]}, a recount gives {FlagsAt(k, i)}";
                }

                if (position > 0 && (leaf.Flags[i] & Break) == 0 && this[position - 1].Right != leaf.Structs[i].In(_slab))
                {
                    return $"position {position} is linked to its left, which does not have it on its right";
                }
            }
        }

        return null;
    }
}
