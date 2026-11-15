using System.Numerics;

namespace Ycs.Layout;

/// <summary>
/// The run-head index of one client (design section "The marker walk"): over the client's clock order, a bit per
/// position where the chain breaks (the struct's left neighbour in its list is not the struct before it by clock,
/// i.e. Yjs' walk-left stops there), a summary bit per 64 of them, and the visible countable length per position
/// summed in 64-position chunks under a Fenwick tree. Yjs' walk-left from position i ends at the last break at or
/// before i, and subtracts the visible length of the positions in between: one scan of bit words and two prefix sums.
/// Built once here; the design keeps it with every split, merge, insert and delete (a bit and a Fenwick update).
/// </summary>
internal sealed class ChainIndex
{
    private readonly ulong[] _breaks;
    private readonly ulong[] _summary;   // bit k: _breaks[k] != 0
    private readonly int[] _visible;
    private readonly long[] _fenwick;    // over the chunk sums of _visible, 1-based

    public ChainIndex(LayoutStore store, int client)
    {
        var order = store.Orders[client];
        var n = order.Count;
        _breaks = new ulong[(n + 63) / 64];
        _summary = new ulong[(_breaks.Length + 63) / 64];
        _visible = new int[n];
        var chunks = new long[_breaks.Length];
        for (var i = 0; i < n; i++)
        {
            ref var s = ref store.Slots[order.Handles[i]];
            if (i == 0 || s.Left != order.Handles[i - 1])
            {
                _breaks[i >> 6] |= 1UL << (i & 63);
            }

            _visible[i] = s.Countable && !s.Deleted ? s.Length : 0;
            chunks[i >> 6] += _visible[i];
        }

        for (var k = 0; k < _breaks.Length; k++)
        {
            if (_breaks[k] != 0)
            {
                _summary[k >> 6] |= 1UL << (k & 63);
            }
        }

        _fenwick = new long[chunks.Length + 1];
        for (var k = 0; k < chunks.Length; k++)
        {
            for (var j = k + 1; j < _fenwick.Length; j += j & -j)
            {
                _fenwick[j] += chunks[k];
            }
        }
    }

    /// <summary>The position where Yjs' walk-left from position <paramref name="i"/> ends.</summary>
    public int Head(int i)
    {
        var w = i >> 6;
        var bits = _breaks[w] & (ulong.MaxValue >> (63 - (i & 63)));
        if (bits != 0)
        {
            return (w << 6) + 63 - BitOperations.LeadingZeroCount(bits);
        }

        // The last word before w with a break, from the summary.
        var sw = (w - 1) >> 6;
        var sbits = w == 0 ? 0 : _summary[sw] & (ulong.MaxValue >> (63 - ((w - 1) & 63)));
        while (sbits == 0)
        {
            sw--;
            sbits = _summary[sw]; // position 0 is a break: this ends
        }

        var word = (sw << 6) + 63 - BitOperations.LeadingZeroCount(sbits);
        return (word << 6) + 63 - BitOperations.LeadingZeroCount(_breaks[word]);
    }

    /// <summary>The visible countable length of positions [0, x).</summary>
    public long Prefix(int x)
    {
        long sum = 0;
        var chunk = x >> 6;
        for (var j = chunk; j > 0; j -= j & -j)
        {
            sum += _fenwick[j];
        }

        for (var i = chunk << 6; i < x; i++)
        {
            sum += _visible[i];
        }

        return sum;
    }
}
