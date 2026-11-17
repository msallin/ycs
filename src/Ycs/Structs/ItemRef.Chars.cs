using System.Buffers;

namespace Ycs;

// The text of a String item lives in the slab's character arena and nowhere else: the slot's Payload is the offset of a
// range, its Length the range's length (a String is never empty: the decoder and the inserts refuse an empty one).
// Each write path of the text keeps it: NewItem (copy in; an item adopted at an offset copies from there), AppendChars
// (extend), SplitItem (payload + diff), MergeItemWith (extend or copy), ReleaseChars (collected, freed): the String cases
// of the dispatchers in ItemRef.Content.cs.
//
// Two windows have a slot whose Length lags its range: ExtendRun's AppendChars (the range is longer by the appended text until
// the caller adds to Length) and MergeItemWith (the left range is extended before Length += right.Length). No compaction
// hook may sit in them: compaction reads each slot's Length as its range's.
internal readonly partial struct ItemRef
{
    private const char Replacement = '\uFFFD';

    /// <summary>Test switch: a merge of two Strings copies even when the right range follows the left.</summary>
    internal static bool CopyOnMergeForTests;

    // Copies the text into the arena and makes the range the slot's. firstReplaced: the text starts after the first half of a
    // surrogate pair that the store holds (an item adopted at an offset), so its first character becomes U+FFFD, as the right
    // part of a split does (SplitChars).
    private static void StoreChars(Slab slab, int handle, ReadOnlySpan<char> text, bool firstReplaced = false)
    {
        var payload = slab.Chars.Append(text);
        if (firstReplaced)
        {
            slab.Chars.Overwrite(payload, [Replacement]);
        }

        slab.Write(handle).Payload = payload;
    }

    /// <summary>
    /// A new String item (not integrated) between <paramref name="left"/> and <paramref name="right"/> whose text is a copy
    /// of <paramref name="text"/>, which must not be empty.
    /// </summary>
    public static ItemRef New(Slab slab, ItemId id, ItemRef left, ItemRef right, YType? parent, string? parentSub, ReadOnlySpan<char> text) =>
        NewItem(slab, id, left, left.IsNull ? null : left.LastId, right, right.IsNull ? null : right.Id, parent, parentSub, SlotKind.String, text.Length, null, text, default, default);

    // The range of the right part after a split of this String at diff; a pair cut in two becomes two replacement
    // characters in place. The tail reservation of the range stays with its end, which
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
        _slab.Chars.MarkDead(slot.Payload, slot.Length);
    }

    /// <summary>
    /// The text of a <see cref="SlotKind.String"/> item, read from the arena. The span is valid only until user code runs
    /// or a batch ends (a compaction moves and frees ranges): consume it before.
    /// </summary>
    internal ReadOnlySpan<char> Chars
    {
        get
        {
            ref readonly var slot = ref Read();
            return slot.Kind == (int)SlotKind.String
                ? _slab.Chars.Slice(slot.Payload, slot.Length)
                : throw new InvalidOperationException($"The struct holds {Kind}, not {SlotKind.String} content.");
        }
    }

    /// <summary>The text of a <see cref="SlotKind.String"/> item, one string per UTF-16 code unit (Yjs' <c>getContent</c>).</summary>
    private object[] CharElements()
    {
        var chars = Chars;
        var result = new object[chars.Length];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = chars[i].ToString();
        }

        return result;
    }

    /// <summary>Appends <paramref name="text"/> to a <see cref="SlotKind.String"/> item; the caller adjusts <see cref="Length"/>.</summary>
    internal void AppendChars(string text)
    {
        StructStore.CompactForTests(_slab, true);
        if (Kind != SlotKind.String)
        {
            throw new InvalidOperationException($"The struct holds {Kind}, not {SlotKind.String} content.");
        }

        ref var slot = ref Write();
        slot.Payload = _slab.Chars.Extend(slot.Payload, slot.Length, text);
    }

    /// <summary>Copies the text of a <see cref="SlotKind.String"/> item to <paramref name="destination"/>, which holds at least <see cref="Length"/> characters.</summary>
    internal void CopyCharsTo(Span<char> destination) => Chars.CopyTo(destination);

    /// <summary>
    /// Writes the text of a <see cref="SlotKind.String"/> item to <paramref name="writer"/> and returns the item the walk
    /// goes on with (default at the end of the list). The writer is asked for its span first: user code in <c>GetSpan</c>
    /// may edit the document, the end of its batch may compact the arena and a struct that left the list may have freed
    /// this item's slot. The struct that holds the item's id is looked up again after it, and its current text is
    /// sliced from that id, a piece at a time while it is longer than the span asked for; a struct that is not a live
    /// String now writes nothing.
    /// </summary>
    internal ItemRef WriteCharsTo(IBufferWriter<char> writer, StructStore store)
    {
        var id = Id;
        var length = Length;
        var held = this;
        while (true)
        {
            var departures = store.Departures;
            var span = writer.GetSpan(length);
            held = store.Resolve(held, id, departures);
            if (held.IsNull)
            {
                return default;
            }

            if (held.Kind != SlotKind.String || held.Deleted)
            {
                return held.Right.AsItem;
            }

            var offset = (int)(id.Clock - held.Id.Clock);
            var count = Math.Min(length, held.Length - offset);
            held.Chars.Slice(offset, count).CopyTo(span);
            writer.Advance(count);
            if (offset + count == held.Length)
            {
                return held.Right.AsItem;
            }

            id = new ItemId(id.Client, id.Clock + count);
            length = held.Length - offset - count;
        }
    }
}
