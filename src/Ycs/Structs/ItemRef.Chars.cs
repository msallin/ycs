#nullable disable // Not annotated for nullable reference types yet.

using System.Buffers;

namespace Ycs;

// The shadow of a String item's text in the slab's character arena: the slot's Payload is the offset of a range, its
// Length the range's length, and the range equals the text of the content object at every write (a String is never empty:
// the decoder and the inserts refuse an empty one).
// Each write path of the text keeps it: NewItem (copy in), AppendChars (extend), SplitItem (payload + diff), MergeItemWith
// (extend or copy), CutCharsHead (an item integrated from an offset), ReleaseChars (collected, freed).
//
// Two windows have a slot whose Length lags its range: ExtendRun's AppendChars (the range is longer by the appended text until
// the caller adds to Length) and MergeItemWith (the left range is extended before Length += right.Length). No compaction
// hook may sit in them: compaction reads each slot's Length as its range's.
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
        var cutPair = char.IsHighSurrogate(chars.Slice(start, offset)[^1]);
        chars.MarkDead(start, offset);
        if (cutPair)
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

    /// <summary>The text of a <see cref="SlotKind.String"/> item; valid until the item's content changes.</summary>
    internal ReadOnlySpan<char> Chars => Typed<ContentString>(SlotKind.String).GetString().AsSpan();

    /// <summary>Appends <paramref name="text"/> to a <see cref="SlotKind.String"/> item; the caller adjusts <see cref="Length"/>.</summary>
    internal void AppendChars(string text)
    {
        StructStore.CompactForTests(_slab, true);
        Typed<ContentString>(SlotKind.String).Append(text);
        ref var slot = ref Write();
        slot.Payload = _slab.Chars.Extend(slot.Payload, slot.Length, text);
    }

    /// <summary>Copies the text of a <see cref="SlotKind.String"/> item to <paramref name="destination"/>, which holds at least <see cref="Length"/> characters, without materializing a run kept in a builder.</summary>
    internal void CopyCharsTo(Span<char> destination) => Typed<ContentString>(SlotKind.String).CopyTo(destination);

    /// <summary>
    /// Writes the text of a <see cref="SlotKind.String"/> item to <paramref name="writer"/>. The content is read before the writer
    /// is asked for its span: a writer that runs user code there may change the item.
    /// </summary>
    internal void WriteCharsTo(IBufferWriter<char> writer)
    {
        var content = Typed<ContentString>(SlotKind.String);
        var length = content.Length;
        content.CopyTo(writer.GetSpan(length));
        writer.Advance(length);
    }
}
