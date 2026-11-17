using System.Runtime.InteropServices;

namespace Ycs.Layout;

/// <summary>
/// One struct (item, GC) of the designed layout: no reference field, so the slab is not scanned by the garbage
/// collector. 13 four-byte fields: 52 bytes (sequential layout, 4-byte aligned). Handles are indexes into
/// <see cref="LayoutStore.Slots"/>; -1 is none. A clock is 32 bits here; the design keeps a side table for the clocks
/// above 2^32 - 2 (<see cref="LayoutStore.WideClock"/> marks one), which no measured document needs.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Slot
{
    public uint Clock;
    public int Length;
    public int Client;          // index into LayoutStore.ClientIds
    public int Left;
    public int Right;
    public int OriginClient;    // -1: no left origin
    public uint OriginClock;
    public int ROriginClient;   // -1: no right origin
    public uint ROriginClock;
    public int Parent;          // index into LayoutStore.Types; -1: none (a GC struct)
    public int Info;            // Flags, and the content kind in bits 8..15
    public int PayloadLo;       // by kind: an arena offset, a type index, or nothing
    public int PayloadHi;

    public readonly ContentKind Kind => (ContentKind)((Info >> 8) & 0xFF);

    public readonly bool Deleted => (Info & Flags.Deleted) != 0;

    public readonly bool Countable => (Info & Flags.Countable) != 0;

    public long Payload
    {
        readonly get => ((long)PayloadHi << 32) | (uint)PayloadLo;
        set
        {
            PayloadLo = (int)value;
            PayloadHi = (int)(value >> 32);
        }
    }
}

internal static class Flags
{
    public const int Keep = 1 << 0;
    public const int Countable = 1 << 1;
    public const int Deleted = 1 << 2;
    public const int Marker = 1 << 3;
    public const int HasRedone = 1 << 4;   // the redone id is in LayoutStore.Redone
}

/// <summary>The content kinds. Inline: their data is in the slot or an arena; Cold: an object in LayoutStore.Refs.</summary>
internal enum ContentKind : byte
{
    Gc = 0,
    Deleted = 1,        // inline: the length is the slot's
    String = 2,         // inline: the characters at Payload in the char arena
    Any = 3,            // inline: the values at Payload in the value arena
    Type = 4,           // inline: Payload is the type's index
    Cold = 5,           // format, embed, binary, json, doc: the IContent object, in Refs
}

/// <summary>A type: its list (start handle) and its map (key to the handle of the key's last item).</summary>
internal sealed class TypeEntry
{
    public int Start = -1;
    public Dictionary<string, int> Map;
    public int Item = -1;
    public string RootName;
    public int Length;      // the visible countable length of the list (YType.Length)
    public int MapCount;    // the live map entries (YType._mapCount)
}

/// <summary>A key and a cold content object for one item, when it has both (a nested type in a map holds no object).</summary>
internal sealed record KeyedRef(string Key, object Content);

/// <summary>
/// Append-only pages of <typeparamref name="T"/>; an offset is (page &lt;&lt; 32) | index. A run never crosses a page:
/// one that does not fit starts a new page (one longer than a page gets a page of its own). Page k holds
/// min(<paramref name="firstPage"/> * 2^k, <paramref name="maxPage"/>) elements.
/// </summary>
internal sealed class Arena<T>(int firstPage, int maxPage)
{
    private readonly List<T[]> _pages = [];
    private int _used;

    public long Append(ReadOnlySpan<T> values)
    {
        if (_pages.Count == 0 || _used + values.Length > _pages[^1].Length)
        {
            var size = (int)Math.Min(maxPage, (long)firstPage << Math.Min(_pages.Count, 30));
            _pages.Add(new T[Math.Max(size, values.Length)]);
            _used = 0;
        }

        var offset = ((long)(_pages.Count - 1) << 32) | (uint)_used;
        values.CopyTo(_pages[^1].AsSpan(_used));
        _used += values.Length;
        return offset;
    }

    public ReadOnlySpan<T> Slice(long offset, int length) => _pages[(int)(offset >> 32)].AsSpan((int)(uint)offset, length);

    /// <summary>The offset <paramref name="by"/> elements on: the same page (a run never crosses one).</summary>
    public static long Advance(long offset, int by) => offset + by;
}

/// <summary>
/// The page sizes of the slab and the arenas. <see cref="Trimmed"/> is what the workload tables measure (the slab cut to
/// its count, the arenas in pages of 64 Ki characters and 16 Ki values); <see cref="Fixed"/> the pages of the design's
/// first draft (1,024 slots from the first struct); <see cref="Geometric"/> the slab growing from 16 slots by doubling
/// to pages of 1,024 and arena pages doubling from 256 characters and 64 values to 64 KB (below the large object heap).
/// </summary>
internal sealed record Paging(string Name, int FirstSlots, int SlotPage, int FirstChars, int CharPage, int FirstValues, int ValuePage)
{
    public static readonly Paging Trimmed = new("trimmed", 0, 0, 1 << 16, 1 << 16, 1 << 14, 1 << 14);
    public static readonly Paging Fixed = new("fixed", 1024, 1024, 1 << 16, 1 << 16, 1 << 14, 1 << 14);
    public static readonly Paging Geometric = new("geometric", 16, 1024, 256, 1 << 15, 64, 1 << 13);

    /// <summary>The slab's capacity for <paramref name="count"/> structs (a paged slab holds whole pages).</summary>
    public int SlabCapacity(int count)
    {
        if (SlotPage == 0 || count == 0)
        {
            return count;
        }

        if (count <= SlotPage)
        {
            var c = FirstSlots;
            while (c < count)
            {
                c *= 2;
            }

            return Math.Min(c, SlotPage);
        }

        return (count + SlotPage - 1) / SlotPage * SlotPage;
    }
}

/// <summary>A client's structs in clock order: their start clocks (searched) and their handles.</summary>
internal sealed class ClientOrder
{
    public uint[] Starts = new uint[4];
    public int[] Handles = new int[4];
    public int Count;

    public void Add(uint start, int handle) => Insert(Count, start, handle);

    public void Insert(int at, uint start, int handle)
    {
        if (Count == Starts.Length)
        {
            Array.Resize(ref Starts, Count * 2);
            Array.Resize(ref Handles, Count * 2);
        }

        Array.Copy(Starts, at, Starts, at + 1, Count - at);
        Array.Copy(Handles, at, Handles, at + 1, Count - at);
        Starts[at] = start;
        Handles[at] = handle;
        Count++;
    }

}

/// <summary>
/// The designed store, built from a ycs document: a slab of <see cref="Slot"/>, a parallel array of references (the
/// key of a map item, a cold content object), a client table, per client the structs in clock order, the types, and
/// the arenas of characters and values.
/// </summary>
internal sealed class LayoutStore
{
    public const uint WideClock = uint.MaxValue;

    public Slot[] Slots = new Slot[16];
    public object[] Refs = new object[16];
    public int Count;
    public long[] ClientIds = [];
    public readonly Dictionary<long, int> ClientIndex = [];
    public ClientOrder[] Orders = [];
    public readonly List<TypeEntry> Types = [];
    public readonly Arena<char> Chars;
    public readonly Arena<object> Values;
    public readonly Paging Paging;

    public LayoutStore(Paging paging)
    {
        Paging = paging;
        Chars = new(paging.FirstChars, paging.CharPage);
        Values = new(paging.FirstValues, paging.ValuePage);
    }

    // Rare: the redone ids (Yjs keeps one per item; most items have none).
    public readonly Dictionary<int, ItemId> Redone = [];

    public int NewSlot()
    {
        if (Count == Slots.Length)
        {
            Array.Resize(ref Slots, Math.Max(16, Count * 2));
            Array.Resize(ref Refs, Math.Max(16, Count * 2));
        }

        return Count++;
    }

    /// <summary>Grows the slab to at least <paramref name="capacity"/> slots.</summary>
    public void Reserve(int capacity)
    {
        if (capacity > Slots.Length)
        {
            Array.Resize(ref Slots, capacity);
            Array.Resize(ref Refs, capacity);
        }
    }

    /// <summary>
    /// Sets the slab's spare capacity to what <see cref="Paging"/> gives (a paged slab holds whole pages; one array of
    /// that capacity holds the same bytes). The clock orders keep their doubling slack, as a List does.
    /// </summary>
    public void Trim()
    {
        var capacity = Paging.SlabCapacity(Count);
        Array.Resize(ref Slots, capacity);
        Array.Resize(ref Refs, capacity);
    }

    public long ClockOf(int handle) => Slots[handle].Clock;

    /// <summary>The position in its client's order of the struct that holds <paramref name="clock"/> (Yjs' findIndexSS).</summary>
    public static int FindIndex(ClientOrder order, uint clock)
    {
        var starts = order.Starts;
        var right = order.Count - 1;
        var last = starts[right];
        if (last <= clock)
        {
            return right;
        }

        var left = 0;
        var end = (long)last + 1; // the pivot of FindIndex needs the end; the last start is close enough for a pivot
        var mid = (int)(clock * (long)right / end);
        while (left <= right)
        {
            if (starts[mid] <= clock)
            {
                if (mid == order.Count - 1 || clock < starts[mid + 1])
                {
                    return mid;
                }

                left = mid + 1;
            }
            else
            {
                right = mid - 1;
            }

            mid = (left + right) / 2;
        }

        throw new InvalidOperationException($"Clock {clock} is not in the store.");
    }

    /// <summary>
    /// Splits the struct of <paramref name="client"/> that holds <paramref name="clock"/> so that a struct starts at
    /// it (Yjs' splitItem as getItemCleanStart calls it): a new slot for the right part, linked in, and inserted into the
    /// client's order. Returns the right part's handle (or the struct's when it starts there already).
    /// </summary>
    public int SplitAt(int client, uint clock)
    {
        var pos = FindIndex(Orders[client], clock);
        var left = Orders[client].Handles[pos];
        return clock == Slots[left].Clock ? left : SplitAt(client, pos, clock);
    }

    /// <summary>
    /// Splits the struct at position <paramref name="pos"/> of <paramref name="client"/>'s order, which holds
    /// <paramref name="clock"/> past its start, and returns the right part's handle.
    /// </summary>
    public int SplitAt(int client, int pos, uint clock)
    {
        var right = SplitSlot(Orders[client].Handles[pos], clock);
        Orders[client].Insert(pos + 1, clock, right);
        return right;
    }

    /// <summary>
    /// The slot half of a split: <paramref name="left"/> ends before <paramref name="clock"/>, a new slot holds the rest
    /// and is linked in. The caller inserts it into its client's order. Returns the new slot's handle.
    /// </summary>
    public int SplitSlot(int left, uint clock)
    {
        var diff = (int)(clock - Slots[left].Clock);
        var right = NewSlot();
        ref var ls = ref Slots[left]; // the slab may have moved
        ref var r = ref Slots[right];
        r = ls;
        r.Clock = clock;
        r.Length = ls.Length - diff;
        r.OriginClient = ls.Client;
        r.OriginClock = clock - 1;
        r.Left = left;
        r.Info &= ~Flags.Marker;
        switch (r.Kind)
        {
            case ContentKind.String:
            case ContentKind.Any:
                r.Payload = Arena<char>.Advance(ls.Payload, diff);
                break;
        }

        Refs[right] = Refs[left];
        if (ls.Right >= 0)
        {
            Slots[ls.Right].Left = right;
        }

        ls.Right = right;
        ls.Length = diff;
        if (r.Right < 0 && KeyOf(right) is string key)
        {
            Types[r.Parent].Map[key] = right;
        }

        return right;
    }

    public string KeyOf(int handle) => Refs[handle] switch
    {
        string key => key,
        KeyedRef k => k.Key,
        _ => null,
    };
}
