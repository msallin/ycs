namespace Ycs;

internal sealed partial class Slab
{
    // The calls of CompactCharsForTests that were skipped to stay linear.
    private long _forced;

    /// <summary>Test switch: <see cref="CompactCharsForTests"/> compacts every time, whatever the size of the slab.</summary>
    internal static bool CompactEveryTimeForTests;

    /// <summary>
    /// <see cref="CompactChars"/> as the tests force it: every time for a slab of up to 2,048 live slots, else every
    /// count/64th time, so that a test of many edits stays linear in them (<see cref="CompactEveryTimeForTests"/> lifts that).
    /// </summary>
    internal void CompactCharsForTests()
    {
        if (CompactEveryTimeForTests || _live <= 2048 || ++_forced % (_live / 64) == 0)
        {
            CompactChars();
        }
    }

    /// <summary>
    /// Moves the live character runs off the pages of <see cref="Chars"/> that are less than half live and frees those
    /// pages; reservations are dropped. The live runs are the ranges of the slots of kind String, found by walking the
    /// slots, so a slot not yet in a struct list counts. A deleted item that is not collected keeps its kind, and its
    /// range, which its encoding reads. A free slot (pending reuse included) holds no range. Only slots and the arena's
    /// tail table hold an offset, and no caller of this method keeps one across it.
    /// </summary>
    internal void CompactChars()
    {
        if (_chars == null)
        {
            return;
        }

        var ranges = new List<(long Offset, int Length, int Tag)>();
        for (var number = 0; number < _pages.Count; number++)
        {
            if (_pages[number] is not { } page)
            {
                continue;
            }

            for (var index = 0; index < page.Used; index++)
            {
                ref var slot = ref page.Slots[index];
                if (slot.Kind == (int)SlotKind.String)
                {
                    ranges.Add((slot.Payload, slot.Length, (number << PageBits) | index));
                }
            }
        }

        _chars.Compact(ranges, (handle, moved) => Write(handle).Payload = moved);
    }
}
