namespace Ycs;

/// <summary>Clock ranges per client, kept sorted, disjoint and joined where they touch.</summary>
internal sealed class ClockRanges
{
    private readonly Dictionary<long, List<(long Start, long End)>> _clients = new();

    /// <summary>Whether a range of <paramref name="client"/> shares a clock with [start, end).</summary>
    public bool Overlaps(long client, long start, long end)
    {
        if (!_clients.TryGetValue(client, out var ranges))
        {
            return false;
        }

        var i = FirstEndingAfter(ranges, start);
        return i < ranges.Count && ranges[i].Start < end;
    }

    public void Add(long client, long start, long end)
    {
        if (!_clients.TryGetValue(client, out var ranges))
        {
            _clients[client] = ranges = new List<(long, long)>();
        }

        // The ranges that overlap or touch [start, end) become one.
        var i = FirstEndingAfter(ranges, start - 1);
        var j = i;
        while (j < ranges.Count && ranges[j].Start <= end)
        {
            start = Math.Min(start, ranges[j].Start);
            end = Math.Max(end, ranges[j].End);
            j++;
        }

        if (j == i)
        {
            ranges.Insert(i, (start, end));
        }
        else
        {
            ranges[i] = (start, end);
            ranges.RemoveRange(i + 1, j - i - 1);
        }
    }

    // The first range that ends after clock.
    private static int FirstEndingAfter(List<(long Start, long End)> ranges, long clock)
    {
        int low = 0, high = ranges.Count;
        while (low < high)
        {
            var mid = (low + high) >> 1;
            if (ranges[mid].End > clock)
            {
                high = mid;
            }
            else
            {
                low = mid + 1;
            }
        }

        return low;
    }
}
