namespace Ycs;

internal sealed partial class Slab
{
    // The calls of CompactForTests that were skipped to stay linear.
    private long _forced;

    /// <summary>
    /// <see cref="CompactChars"/> and <see cref="CompactValues"/> as the tests force them: every time for a slab of up to
    /// 2,048 live slots, else every count/64th time, so that a test of many edits stays linear in them
    /// (<see cref="TestHooks.CompactEveryTime"/> lifts that).
    /// </summary>
    internal void CompactForTests()
    {
        if (TestHooks.CompactEveryTime || _live <= 2048 || ++_forced % (_live / 64) == 0)
        {
            CompactChars();
            CompactValues();
        }
    }

    /// <summary>
    /// Moves the live character runs off the pages of <see cref="Chars"/> that are less than half live and frees those
    /// pages; reservations are dropped. The live runs are the ranges of the slots of kind String, found by walking the
    /// slots, so a slot not yet in a struct list counts. A deleted item that is not collected keeps its kind, and its
    /// range, which its encoding reads. A free slot (pending reuse included) holds no range. Only slots and the arena's
    /// tail table hold an offset, and no caller of this method keeps one across it.
    /// </summary>
    internal void CompactChars() => Compact(_chars, SlotKind.String);

    /// <summary><see cref="CompactChars"/> for the Any ranges of <see cref="Values"/>; a moved range's old values are cleared.</summary>
    internal void CompactValues() => Compact(_values, SlotKind.Any);

    private void Compact<T>(Arena<T>? arena, SlotKind kind)
    {
        if (arena == null)
        {
            return;
        }

        var ranges = new List<(long Offset, int Length, int Tag)>();
        for (var number = 0; number < _pageCount; number++)
        {
            if (_pages[number] is not { } page)
            {
                continue;
            }

            for (var index = 0; index < page.Used; index++)
            {
                ref var slot = ref page.Slots[index];
                if (slot.Kind == (int)kind)
                {
                    ranges.Add((slot.Payload, slot.Length, (number << PageBits) | index));
                }
            }
        }

        arena.Compact(ranges, (handle, moved) => Write(handle).Payload = moved);
    }
}
