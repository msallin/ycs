using System.Diagnostics;

namespace Ycs;

/// <summary>The page sizes the item layout gives its two arenas.</summary>
internal static class Arena
{
    internal const int CharFirstPage = 256;
    internal const int CharMaxPage = 32 * 1024;
    internal const int RefFirstPage = 64;
    internal const int RefMaxPage = 8 * 1024;
}

/// <summary>
/// Runs of elements in pages, addressed by a 64-bit offset (<c>page &lt;&lt; 32 | index</c>). The arena knows runs only
/// as the (offset, length) pairs its caller holds: it never learns which are live except when asked to
/// <see cref="Compact"/> or check itself, and it never moves a run but by the caller's call.
/// </summary>
/// <remarks>
/// A run lies in one page. Page k, counted among the pages held, holds <c>min(first * 2^k, max)</c> elements (more when
/// a run needs it). A run longer than <c>max</c> gets a page of its own, twice its length. A run that outgrows its room
/// is copied to the write end with as much slack again, kept in a tail table: run end offset to the end of its
/// reservation, so two runs growing alternately copy O(log n) times. A table entry is the only owner of the space
/// between a run's end and its reservation end; a run split in two leaves the entry with the right part, which holds
/// the end. Per page, <c>Used = live + dead + reserved</c>; the page's elements past <c>Used</c> are unwritten.
/// An offset or a span is valid until the next <see cref="Compact"/>, which frees pages.
/// </remarks>
internal sealed class Arena<T>
{
    /// <summary>The first page size of arenas made while it is not 0 (the arena's own when it is).</summary>
    internal static int FirstPageForTests;

    /// <summary>The largest page size of arenas made while it is not 0.</summary>
    internal static int MaxPageForTests;

    private const int MaxArrayLength = 0x7FFFFFC7;

    private readonly List<Page?> _pages = [];
    private readonly Stack<int> _released = [];
    private readonly Dictionary<long, long> _tails = [];
    private readonly int _first;
    private readonly int _max;

    // The page that appends go to; -1 before the first. It is never freed by Compact.
    private int _write = -1;
    private long _live;
    private long _dead;

    public Arena(int firstPage, int maxPage)
    {
        _max = Math.Max(1, MaxPageForTests != 0 ? MaxPageForTests : maxPage);
        _first = Math.Clamp(FirstPageForTests != 0 ? FirstPageForTests : firstPage, 1, _max);
    }

    /// <summary>Elements in live runs: what was appended, less what was reported dead.</summary>
    public long Live => _live;

    /// <summary>Elements written that no live run holds, reservations dropped included.</summary>
    public long Dead => _dead;

    /// <summary>The pages held.</summary>
    public int PageCount => _pages.Count - _released.Count;

    /// <summary>The runs that have room reserved after them.</summary>
    public int TailCount => _tails.Count;

    public static int PageOf(long offset) => (int)(offset >> 32);

    public static int IndexOf(long offset) => (int)(offset & 0xFFFFFFFF);

    /// <summary>The elements a page holds room for; 0 for a page that is not held.</summary>
    public int PageCapacity(int page) => page < _pages.Count ? _pages[page]?.Data.Length ?? 0 : 0;

    public ReadOnlySpan<T> Slice(long offset, int length) => PageAt(offset).Data.AsSpan(IndexOf(offset), length);

    /// <summary>A new run holding <paramref name="values"/>, at the write end (a run longer than a page: in a page of its own).</summary>
    public long Append(ReadOnlySpan<T> values)
    {
        if (values.Length == 0)
        {
            throw new ArgumentException("A run holds at least one element.", nameof(values));
        }

        var offset = Place(values.Length, false);
        values.CopyTo(PageAt(offset).Data.AsSpan(IndexOf(offset)));
        _live += values.Length;
        return offset;
    }

    /// <summary>
    /// Adds <paramref name="values"/> after the run (<paramref name="offset"/>, <paramref name="length"/>) and returns
    /// the run's offset: the same when the run ends at the write end or inside its reservation, else the run is
    /// copied with slack, the old range counts dead and its old elements stay where they were (the caller clears them
    /// if they are references).
    /// </summary>
    public long Extend(long offset, int length, ReadOnlySpan<T> values)
    {
        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "A run holds at least one element.");
        }

        if (values.Length == 0)
        {
            return offset;
        }

        var page = PageAt(offset);
        var end = offset + length;
        var endIndex = IndexOf(end);
        if (endIndex > page.Used)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The run reaches past what the page holds.");
        }

        if (_tails.TryGetValue(end, out var reservedEnd))
        {
            if (values.Length <= reservedEnd - end)
            {
                values.CopyTo(page.Data.AsSpan(endIndex));
                _tails.Remove(end);
                if (end + values.Length < reservedEnd)
                {
                    _tails[end + values.Length] = reservedEnd;
                }

                _live += values.Length;
                return offset;
            }
        }
        else if (PageOf(offset) == _write && endIndex == page.Used && values.Length <= page.Data.Length - endIndex)
        {
            values.CopyTo(page.Data.AsSpan(endIndex));
            page.Used += values.Length;
            _live += values.Length;
            return offset;
        }

        var total = (long)length + values.Length;
        if (total > MaxArrayLength / 2)
        {
            throw new InvalidOperationException("The run would outgrow the largest array; nothing was changed.");
        }

        var moved = Place((int)total, true);
        var data = PageAt(moved).Data.AsSpan(IndexOf(moved));
        page.Data.AsSpan(IndexOf(offset), length).CopyTo(data);
        values.CopyTo(data[length..]);
        _live += total;
        Forget(offset, length);
        return moved;
    }

    /// <summary>The range's elements are garbage: a run that ended, or a part of one the caller will not use again.</summary>
    public void MarkDead(long offset, int length)
    {
        _ = PageAt(offset);
        Forget(offset, length);
    }

    /// <summary>Replaces the elements from <paramref name="offset"/> on by <paramref name="values"/>, which lie within the used part of the page; no run is added or ended.</summary>
    public void Overwrite(long offset, ReadOnlySpan<T> values)
    {
        var page = PageAt(offset);
        if (IndexOf(offset) + (long)values.Length > page.Used)
        {
            throw new ArgumentOutOfRangeException(nameof(values), "The values reach past what the page holds.");
        }

        values.CopyTo(page.Data.AsSpan(IndexOf(offset)));
    }

    /// <summary>Sets a range to the default of <typeparamref name="T"/>, so a reference in it is not kept alive.</summary>
    public void Clear(long offset, int length) => PageAt(offset).Data.AsSpan(IndexOf(offset), length).Clear();

    /// <summary>
    /// Moves the runs on pages less than half live to the write end and frees those pages (the write page stays).
    /// <paramref name="live"/> is every live range, deleted-but-kept ones included, and is enumerated twice with the
    /// arena unchanged between; <paramref name="relocate"/> is called per moved run (old offset, new offset, length)
    /// and the caller stores the new offset before the enumeration goes on. Reservations are dropped: their entries
    /// may name freed pages. After it, <see cref="Live"/> is the sum of the lengths given.
    /// </summary>
    public void Compact(IEnumerable<(long Offset, int Length)> live, Action<long, long, int> relocate)
    {
        var liveIn = new long[_pages.Count];
        long total = 0;
        foreach (var (offset, length) in live)
        {
            _ = PageAt(offset);
            liveIn[PageOf(offset)] += length;
            total += length;
        }

        var free = new bool[_pages.Count];
        for (var number = 0; number < free.Length; number++)
        {
            var page = _pages[number];
            free[number] = page != null && number != _write && liveIn[number] * 2 < page.Data.Length;
        }

        _tails.Clear();
        foreach (var (offset, length) in live)
        {
            if (!free[PageOf(offset)])
            {
                continue;
            }

            var moved = Place(length, false);
            PageAt(offset).Data.AsSpan(IndexOf(offset), length).CopyTo(PageAt(moved).Data.AsSpan(IndexOf(moved)));
            relocate(offset, moved, length);
        }

        for (var number = 0; number < free.Length; number++)
        {
            if (free[number])
            {
                Release(number);
            }
        }

        long used = 0;
        foreach (var page in _pages)
        {
            used += page?.Used ?? 0;
        }

        _live = total;
        _dead = used - total - Reserved();
    }

    /// <summary>
    /// Every inconsistency found by recounting against <paramref name="live"/> (the caller's live ranges): none when
    /// sound. Live ranges lie within the used part of a held page, disjoint from each other and from the
    /// reservations; the counters and the tail table agree with them.
    /// </summary>
    public List<string> Violations(IEnumerable<(long Offset, int Length)> live)
    {
        var found = new List<string>();
        var spans = new List<(long Start, long End, bool Reservation)>();
        long liveTotal = 0;
        var liveEnds = new HashSet<long>();
        foreach (var (offset, length) in live)
        {
            var page = PageOf(offset) < _pages.Count && offset >= 0 ? _pages[PageOf(offset)] : null;
            if (page == null || length <= 0 || IndexOf(offset) + (long)length > page.Used)
            {
                found.Add($"live range {offset:X}+{length} is not inside the used part of a held page");
                continue;
            }

            spans.Add((offset, offset + length, false));
            liveEnds.Add(offset + length);
            liveTotal += length;
        }

        foreach (var (end, reservedEnd) in _tails)
        {
            var page = PageOf(end) < _pages.Count && end >= 0 ? _pages[PageOf(end)] : null;
            if (page == null || PageOf(reservedEnd) != PageOf(end) || reservedEnd <= end || IndexOf(reservedEnd) > page.Used)
            {
                found.Add($"tail {end:X} to {reservedEnd:X} is not inside the used part of a held page");
                continue;
            }

            if (!liveEnds.Contains(end))
            {
                found.Add($"tail {end:X} is the end of no live range");
            }

            spans.Add((end, reservedEnd, true));
        }

        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (var i = 1; i < spans.Count; i++)
        {
            if (spans[i].Start < spans[i - 1].End)
            {
                found.Add($"{(spans[i - 1].Reservation ? "reservation" : "range")} {spans[i - 1].Start:X} to {spans[i - 1].End:X} overlaps {spans[i].Start:X}");
            }
        }

        if (liveTotal != _live)
        {
            found.Add($"live count {_live}, ranges hold {liveTotal}");
        }

        long used = 0;
        for (var number = 0; number < _pages.Count; number++)
        {
            var page = _pages[number];
            if (page == null)
            {
                if (!_released.Contains(number))
                {
                    found.Add($"page {number} is released but not listed as such");
                }

                continue;
            }

            if (page.Used > page.Data.Length)
            {
                found.Add($"page {number}: {page.Used} used of {page.Data.Length}");
            }

            used += page.Used;
        }

        if (_write >= 0 && _pages[_write] == null)
        {
            found.Add("the write page is released");
        }

        var dead = used - liveTotal - Reserved();
        if (dead != _dead)
        {
            found.Add($"dead count {_dead}, recount {dead}");
        }

        return found;
    }

    private long Reserved()
    {
        long reserved = 0;
        foreach (var (end, reservedEnd) in _tails)
        {
            reserved += reservedEnd - end;
        }

        return reserved;
    }

    // The range stops being a run: its elements and its reservation are garbage.
    private void Forget(long offset, int length)
    {
        _dead += length;
        _live -= length;
        var end = offset + length;
        if (_tails.Remove(end, out var reservedEnd))
        {
            _dead += reservedEnd - end;
        }
    }

    // Room for a run of the length at the write end, with as much again reserved after it when slack is asked.
    private long Place(int length, bool slack)
    {
        if (length > _max)
        {
            if (length > MaxArrayLength / 2)
            {
                throw new InvalidOperationException("The run would outgrow the largest array; nothing was changed.");
            }

            var number = Open(length * 2);
            var own = _pages[number]!;
            own.Used = own.Data.Length;
            _tails[((long)number << 32) + length] = ((long)number << 32) + own.Data.Length;
            return (long)number << 32;
        }

        var write = _write < 0 ? null : _pages[_write];
        var want = slack ? 2L * length : length;
        if (write == null || write.Data.Length - write.Used < want)
        {
            _write = Open((int)Math.Min(_max, Math.Max(NextSize(), want)));
            write = _pages[_write]!;
        }

        var start = ((long)_write << 32) + write.Used;
        write.Used += length;
        if (slack)
        {
            var reserve = Math.Min(length, write.Data.Length - write.Used);
            if (reserve > 0)
            {
                _tails[start + length] = start + length + reserve;
                write.Used += (int)reserve;
            }
        }

        return start;
    }

    private long NextSize() => Math.Min(_max, (long)_first << Math.Min(PageCount, 30));

    private int Open(int size)
    {
        var number = _released.Count > 0 ? _released.Pop() : _pages.Count;
        if (number == _pages.Count)
        {
            _pages.Add(null);
        }

        _pages[number] = new Page(new T[size]);
        return number;
    }

    private Page PageAt(long offset)
    {
        var number = PageOf(offset);
        var page = offset >= 0 && number < _pages.Count ? _pages[number] : null;
        if (page == null)
        {
            throw new ArgumentException("The offset names no page of this arena (a page freed by a compaction, or an offset never given).", nameof(offset));
        }

        return page;
    }

    private void Release(int number)
    {
        PoisonPage(_pages[number]!.Data);
        _pages[number] = null;
        _released.Push(number);
    }

    // A span kept across a compaction points into the freed page: reading it shows these values.
    [Conditional("DEBUG")]
    private static void PoisonPage(T[] data)
    {
        if (typeof(T) == typeof(char))
        {
            data.AsSpan().Fill((T)(object)'婚');
        }
        else
        {
            Array.Clear(data);
        }
    }

    private sealed class Page(T[] data)
    {
        public readonly T[] Data = data;
        public int Used;
    }
}
