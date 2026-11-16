using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ycs;

/// <summary>
/// One struct of the item layout: sequential, no reference field, so the garbage collector does not scan a page.
/// Handles are -1 for none; a client is an index into the client table, a parent an index into the type table.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Slot
{
    /// <summary>The kind (bits 8..15 of <see cref="Info"/>) of a slot that is on a free list.</summary>
    public const int FreeKind = 0xFF;

    // The flag bits of Info, as the item's own.
    public const int KeepFlag = 1 << 0;
    public const int CountableFlag = 1 << 1;
    public const int DeletedFlag = 1 << 2;
    public const int MarkerFlag = 1 << 3;
    public const int HasRedoneFlag = 1 << 6;

    public long Clock;
    public long OriginClock;
    public long ROriginClock;
    public int Length;
    public int Client;
    public int OriginClient;
    public int ROriginClient;
    public int Left;
    public int Right;
    public int Parent;
    public int Info;
    public long Payload;

    public readonly int Kind => (Info >> 8) & 0xFF;

    public readonly bool IsFree => Kind == FreeKind;

    internal static Slot Empty => new() { OriginClient = -1, ROriginClient = -1, Left = -1, Right = -1, Parent = -1 };
}

/// <summary>
/// Slots in pages, addressed by 32-bit handles (<c>page &lt;&lt; 10 | index</c>). Page 0 starts small and doubles by
/// copy up to <see cref="PageSlots"/>; later pages hold <see cref="PageSlots"/> slots and never move. Beside each page
/// are three object pages: <c>Refs</c> (content) and <c>Keys</c> (a map item's key), allocated on first use, and
/// <c>Owners</c> (the struct a handle resolves to); null for a free slot.
/// </summary>
/// <remarks>
/// A freed slot keeps its fields but <see cref="Slot.Payload"/>, which holds the free-list link (readers in the same
/// cleanup may still look at the others), and its object entries are cleared. It is reused only after
/// <see cref="AllowReuse"/>, which the owner calls when nothing that could read a freed slot is pending (the outermost
/// batch's end). A page, but page 0, whose slots are all free is released then. A <c>ref Slot</c> is valid until the
/// next <see cref="Allocate"/>, which may move page 0.
/// </remarks>
internal sealed class Slab
{
    internal const int PageBits = 10;
    internal const int PageSlots = 1 << PageBits;
    private const int DefaultFirstSlots = 16;

    /// <summary>The first size of page 0 for slabs made without one: 1 makes every allocation of page 0 copy.</summary>
    internal static int FirstPageSlotsForTests;

    // Pages are released as a whole: the list holds null for a released one, and its index is on _released.
    private readonly List<Page?> _pages = [];

    // The pages' arrays by page number, for the reads: two indexes reach a slot. Published whenever a page is made, grown,
    // gets its object pages or is released.
    private Slot[]?[] _slotPages = new Slot[]?[4];
    private object?[]?[] _refPages = new object?[]?[4];
    private object?[]?[] _keyPages = new object?[]?[4];
    private object?[]?[] _ownerPages = new object?[]?[4];
    private readonly Stack<int> _released = [];

    // Pages that may have room, most recently opened last; an entry for a released or full page is dropped when met.
    private readonly Stack<int> _open = [];
    private readonly List<int> _pending = [];
    private readonly int _firstSlots;
    private int _live;

    public Slab(int firstPageSlots = 0)
    {
        var first = firstPageSlots != 0 ? firstPageSlots : FirstPageSlotsForTests != 0 ? FirstPageSlotsForTests : DefaultFirstSlots;
        _firstSlots = Math.Clamp(first, 1, PageSlots);
    }

    public int LiveCount => _live;

    /// <summary>The pages held, released ones excluded.</summary>
    public int PageCount => _pages.Count - _released.Count;

    /// <summary>The slots page 0 holds room for (0 before the first allocation).</summary>
    public int FirstPageCapacity => _pages.Count == 0 ? 0 : _pages[0]!.Slots.Length;

    public static int PageOf(int handle) => handle >> PageBits;

    public static int IndexOf(int handle) => handle & (PageSlots - 1);

    /// <summary>A new slot: <see cref="Slot.Empty"/>, no kind, no objects.</summary>
    public int Allocate()
    {
        var page = FindPage(out var number);
        int index;
        if (page.FreeHead >= 0)
        {
            index = page.FreeHead;
            page.FreeHead = (int)page.Slots[index].Payload;
            page.FreeCount--;
        }
        else
        {
            if (page.Used == page.Slots.Length)
            {
                Grow(page, number);
            }

            index = page.Used++;
        }

        page.Slots[index] = Slot.Empty;
        _live++;
        return number << PageBits | index;
    }

    /// <summary>The slot of a live or just freed handle; valid until the next <see cref="Allocate"/>.</summary>
    public ref Slot At(int handle) => ref PageAt(handle).Slots[IndexOf(handle)];

    public object? GetRef(int handle) => PageAt(handle).Refs?[IndexOf(handle)];

    /// <summary>
    /// The slot of a live handle for reading, without <see cref="At"/>'s checks: the caller holds a handle of a live
    /// struct (a negative or released one throws all the same, from the array).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly Slot Read(int handle) => ref _slotPages[handle >> PageBits]![handle & (PageSlots - 1)];

    /// <summary><see cref="GetRef"/> of a live handle, unchecked as <see cref="Read"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public object? ReadRef(int handle) => _refPages[handle >> PageBits]?[handle & (PageSlots - 1)];

    /// <summary><see cref="GetKey"/> of a live handle, unchecked as <see cref="Read"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public object? ReadKey(int handle) => _keyPages[handle >> PageBits]?[handle & (PageSlots - 1)];

    /// <summary>The struct that owns a live slot (the object a handle resolves to).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public object ReadOwner(int handle) => _ownerPages[handle >> PageBits]![handle & (PageSlots - 1)]!;

    /// <summary>
    /// Debug only: the slot's owner is <paramref name="owner"/>. A handle that outlived its struct names a free slot or,
    /// once the slot is reused, another struct; it fails here, not as a wrong answer later.
    /// </summary>
    [Conditional("DEBUG")]
    public void CheckOwner(int handle, object owner)
    {
        var current = PageOrNull(handle) is { } page && IndexOf(handle) < page.Used ? page.Owners[IndexOf(handle)] : null;
        if (!ReferenceEquals(current, owner))
        {
            throw new InvalidOperationException(
                $"The slot {handle} is owned by {(current == null ? "no struct (it is free)" : "another struct")}, not by the struct that reads it: a handle outlived its struct and the slot was freed or reused. Nothing was changed.");
        }
    }

    private Page? PageOrNull(int handle)
    {
        var number = PageOf(handle);
        return handle >= 0 && number < _pages.Count ? _pages[number] : null;
    }

    public void SetOwner(int handle, object? owner)
    {
        var page = PageAt(handle);
        CheckLive(page, handle);
        page.Owners[IndexOf(handle)] = owner;
    }

    public void SetRef(int handle, object? value)
    {
        var page = PageAt(handle);
        CheckLive(page, handle);
        if (value != null || page.Refs != null)
        {
            (page.Refs ??= new object?[page.Slots.Length])[IndexOf(handle)] = value;
            Publish(PageOf(handle), page);
        }
    }

    public object? GetKey(int handle) => PageAt(handle).Keys?[IndexOf(handle)];

    public void SetKey(int handle, object? value)
    {
        var page = PageAt(handle);
        CheckLive(page, handle);
        if (value != null || page.Keys != null)
        {
            (page.Keys ??= new object?[page.Slots.Length])[IndexOf(handle)] = value;
            Publish(PageOf(handle), page);
        }
    }

    /// <summary>
    /// Frees a live slot: its kind becomes <see cref="Slot.FreeKind"/> and its objects are dropped. The other fields
    /// stay readable until the slot is reused, which waits for <see cref="AllowReuse"/>.
    /// </summary>
    public void Free(int handle)
    {
        var page = PageAt(handle);
        CheckLive(page, handle);
        var index = IndexOf(handle);
        ref var slot = ref page.Slots[index];
        slot.Info = (slot.Info & ~(0xFF << 8)) | (Slot.FreeKind << 8);
        slot.Payload = page.PendingHead;
        page.PendingHead = index;
        page.PendingCount++;
        if (page.Refs != null)
        {
            page.Refs[index] = null;
        }

        if (page.Keys != null)
        {
            page.Keys[index] = null;
        }

        page.Owners[index] = null;

        if (!page.Dirty)
        {
            page.Dirty = true;
            _pending.Add(PageOf(handle));
        }

        _live--;
    }

    /// <summary>
    /// The slots freed so far may be reused. Pages left with no live slot are released, except page 0.
    /// </summary>
    public void AllowReuse()
    {
        foreach (var number in _pending)
        {
            var page = _pages[number]!;
            page.Dirty = false;
            if (page.PendingHead < 0)
            {
                continue;
            }

            // The pending chain ends in the free chain's head.
            var tail = page.PendingHead;
            PoisonFreed(ref page.Slots[tail]);
            while (page.Slots[tail].Payload >= 0)
            {
                tail = (int)page.Slots[tail].Payload;
                PoisonFreed(ref page.Slots[tail]);
            }

            page.Slots[tail].Payload = page.FreeHead;
            page.FreeHead = page.PendingHead;
            page.FreeCount += page.PendingCount;
            page.PendingHead = -1;
            page.PendingCount = 0;

            if (page.Live == 0 && number != 0)
            {
                _pages[number] = null;
                Publish(number, null);
                _released.Push(number);
            }
            else if (!page.Open)
            {
                page.Open = true;
                _open.Push(number);
            }
        }

        _pending.Clear();
    }

    /// <summary>Every inconsistency found by recounting the pages, free lists and counters: none when sound.</summary>
    public List<string> Violations()
    {
        var found = new List<string>();
        var live = 0;
        var released = 0;
        for (var number = 0; number < _pages.Count; number++)
        {
            var page = _pages[number];
            if (page == null)
            {
                released++;
                if (!_released.Contains(number))
                {
                    found.Add($"page {number} is released but not listed as such");
                }

                continue;
            }

            CheckPage(page, number, found);
            live += page.Live;
        }

        if (released != _released.Count)
        {
            found.Add($"{_released.Count} pages listed as released, {released} are");
        }

        if (live != _live)
        {
            found.Add($"live count {_live}, pages hold {live}");
        }

        return found;
    }

    private static void CheckPage(Page page, int number, List<string> found)
    {
        if (page.Used > page.Slots.Length || (number != 0 && page.Slots.Length != PageSlots) || page.Slots.Length > PageSlots)
        {
            found.Add($"page {number}: {page.Used} used of {page.Slots.Length} slots");
            return;
        }

        var onList = new bool[page.Used];
        CheckChain(page, number, page.FreeHead, page.FreeCount, "free", onList, found);
        CheckChain(page, number, page.PendingHead, page.PendingCount, "pending", onList, found);
        for (var i = 0; i < page.Used; i++)
        {
            if (!onList[i] && page.Slots[i].IsFree)
            {
                found.Add($"page {number}: slot {i} is free but on no list");
            }

            if (onList[i] && ((page.Refs?[i] ?? page.Keys?[i] ?? page.Owners[i]) != null))
            {
                found.Add($"page {number}: free slot {i} holds an object");
            }
        }

        if (page.Owners.Length != page.Slots.Length || (page.Refs != null && page.Refs.Length != page.Slots.Length) || (page.Keys != null && page.Keys.Length != page.Slots.Length))
        {
            found.Add($"page {number}: an object page does not match the slots");
        }
    }

    private static void CheckChain(Page page, int number, int head, int expected, string name, bool[] onList, List<string> found)
    {
        var count = 0;
        for (var i = head; i >= 0; i = (int)page.Slots[i].Payload)
        {
            if (i >= page.Used)
            {
                found.Add($"page {number}: {name} list reaches slot {i} beyond {page.Used} used");
                return;
            }

            if (onList[i])
            {
                found.Add($"page {number}: slot {i} is on a list twice");
                return;
            }

            onList[i] = true;
            if (!page.Slots[i].IsFree)
            {
                found.Add($"page {number}: live slot {i} is on the {name} list");
            }

            count++;
        }

        if (count != expected)
        {
            found.Add($"page {number}: {name} list holds {count}, counted {expected}");
        }
    }

    private Page PageAt(int handle)
    {
        var number = PageOf(handle);
        var page = handle >= 0 && number < _pages.Count ? _pages[number] : null;
        if (page == null || IndexOf(handle) >= page.Used)
        {
            throw new ArgumentException("The handle names no slot of this slab (a released page or a handle never allocated).", nameof(handle));
        }

        return page;
    }

    private static void CheckLive(Page page, int handle)
    {
        if (page.Slots[IndexOf(handle)].IsFree)
        {
            throw new InvalidOperationException("The slot is free: it was freed already and holds nothing to write or free again.");
        }
    }

    private Page FindPage(out int number)
    {
        while (_open.Count > 0)
        {
            number = _open.Peek();
            var page = _pages[number];
            if (page != null && (page.FreeHead >= 0 || page.Used < PageSlots))
            {
                return page;
            }

            _open.Pop();
            if (page != null)
            {
                page.Open = false;
            }
        }

        if (_released.Count > 0)
        {
            number = _released.Pop();
        }
        else
        {
            number = _pages.Count;
            _pages.Add(null);
            if (number > int.MaxValue >> PageBits)
            {
                throw new InvalidOperationException("The slab holds the most slots a 32-bit handle can name.");
            }
        }

        var fresh = new Page(number == 0 ? _firstSlots : PageSlots) { Open = true };
        _pages[number] = fresh;
        Publish(number, fresh);
        _open.Push(number);
        return fresh;
    }

    private void Publish(int number, Page? page)
    {
        if (number >= _slotPages.Length)
        {
            var size = Math.Max(number + 1, _slotPages.Length * 2);
            Array.Resize(ref _slotPages, size);
            Array.Resize(ref _refPages, size);
            Array.Resize(ref _keyPages, size);
            Array.Resize(ref _ownerPages, size);
        }

        _slotPages[number] = page?.Slots;
        _refPages[number] = page?.Refs;
        _keyPages[number] = page?.Keys;
        _ownerPages[number] = page?.Owners;
    }

    private void Grow(Page page, int number)
    {
        var old = page.Slots;
        var slots = new Slot[Math.Min(old.Length * 2, PageSlots)];
        Array.Copy(old, slots, old.Length);
        page.Slots = slots;
        Array.Resize(ref page.Owners, slots.Length);
        if (page.Refs != null)
        {
            Array.Resize(ref page.Refs, slots.Length);
        }

        if (page.Keys != null)
        {
            Array.Resize(ref page.Keys, slots.Length);
        }

        Publish(number, page);
        PoisonOld(old);
    }

    // A slot may be read until it can be reused; from then on its fields (but the kind and the free-list link) are garbage,
    // so a holder that still reads one gets a handle that names no page.
    [Conditional("DEBUG")]
    private static void PoisonFreed(ref Slot slot)
    {
        const int garbage = 0x5A5A5A5A;
        slot.Clock = garbage;
        slot.OriginClock = garbage;
        slot.ROriginClock = garbage;
        slot.Length = garbage;
        slot.Client = garbage;
        slot.OriginClient = garbage;
        slot.ROriginClient = garbage;
        slot.Left = garbage;
        slot.Right = garbage;
        slot.Parent = garbage;
    }

    // A ref Slot kept across the growth points into the old array: reading it shows these values.
    [Conditional("DEBUG")]
    private static void PoisonOld(Slot[] old)
    {
        Array.Fill(old, new Slot
        {
            Clock = -0x5A5A5A5A,
            OriginClock = -0x5A5A5A5A,
            ROriginClock = -0x5A5A5A5A,
            Length = -0x5A5A5A5A,
            Client = -0x5A5A5A5A,
            OriginClient = -0x5A5A5A5A,
            ROriginClient = -0x5A5A5A5A,
            Left = -0x5A5A5A5A,
            Right = -0x5A5A5A5A,
            Parent = -0x5A5A5A5A,
            Info = -0x5A5A5A5A,
            Payload = -0x5A5A5A5A,
        });
    }

    private sealed class Page(int capacity)
    {
        public Slot[] Slots = new Slot[capacity];
        public object?[]? Refs;
        public object?[]? Keys;
        public object?[] Owners = new object?[capacity];
        public int Used;
        public int FreeHead = -1;
        public int FreeCount;
        public int PendingHead = -1;
        public int PendingCount;
        public bool Open;
        public bool Dirty;

        public int Live => Used - FreeCount - PendingCount;
    }
}
