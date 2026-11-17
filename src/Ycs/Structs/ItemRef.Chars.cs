#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

// The shadow of a String item's text in the slab's character arena: the slot's Payload is the offset of a range, its
// Length the range's length, and the range equals the text of the content object at every write (a String is never empty:
// the decoder and the inserts refuse an empty one).
// Each write path of the text keeps it: NewItem (copy in), AppendChars (extend), SplitItem (payload + diff), MergeItemWith
// (extend or copy), CutCharsHead (an item integrated from an offset), ReleaseChars (collected, freed).
internal readonly partial struct ItemRef
{
    private const char Replacement = '\uFFFD';

    /// <summary>Test switch: a merge of two Strings copies even when the right range follows the left.</summary>
    internal static bool CopyOnMergeForTests;

    private static long CopyChars(Slab slab, ContentString text) =>
        slab.Chars.Append(text.TryGetSpan(out var span) ? span : text.GetString());

    // The part before offset leaves the item (an item integrated from an offset): its range is the rest.
    private void CutCharsHead(int offset)
    {
        var chars = _slab.Chars;
        var start = Read().Payload;
        chars.MarkDead(start, offset);
        if (char.IsHighSurrogate(chars.Slice(start, offset)[^1]))
        {
            chars.Overwrite(start + offset, [Replacement]);
        }

        Write().Payload = start + offset;
    }

    // The range of the right part after a split of this String at diff; a pair cut in two becomes two replacement
    // characters in place, as ContentString.Splice does. The tail reservation of the range stays with its end, which
    // the right part holds.
    private long SplitChars(int diff)
    {
        var chars = _slab.Chars;
        var start = Read().Payload;
        if (char.IsHighSurrogate(chars.Slice(start, diff)[^1]))
        {
            chars.Overwrite(start + diff - 1, [Replacement, Replacement]);
        }

        return start + diff;
    }

    // The range of the right neighbour joins this one's; Length is not yet the sum.
    private void MergeChars(ItemRef right)
    {
        var chars = _slab.Chars;
        var leftStart = Read().Payload;
        var leftLength = Read().Length;
        var rightStart = right.Read().Payload;
        var rightLength = right.Read().Length;
        if (!CopyOnMergeForTests && leftStart + leftLength == rightStart)
        {
            return;
        }

        var moved = chars.Extend(leftStart, leftLength, chars.Slice(rightStart, rightLength));
        chars.MarkDead(rightStart, rightLength);
        Write().Payload = moved;
    }

    // The range of this String ends: it is collected, or its slot is freed.
    private void ReleaseChars()
    {
        ref readonly var slot = ref Read();
        if (slot.Kind == (int)SlotKind.String)
        {
            _slab.Chars.MarkDead(slot.Payload, slot.Length);
        }
    }
}
