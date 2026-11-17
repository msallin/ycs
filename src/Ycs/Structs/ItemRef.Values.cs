#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

// The values of an Any item live in the slab's value arena and nowhere else: the slot's Payload is the offset of a range,
// its Length the range's length (an Any is never empty).
// The range's elements are the only references the arena keeps: every exit of a range clears them (Arena.Clear), so a
// value is collectible as soon as nothing else holds it, before any compaction.
// Each write path keeps it: NewItem (copy in), AppendValues (extend; a copy clears the old range), SplitItem (payload + diff),
// MergeItemWith (extend, or copy: the old ranges are cleared), CutValuesHead (an item integrated from an offset),
// ReleaseValues (collected, freed): the Any cases of the dispatchers in ItemRef.Content.cs.
//
// The windows where a slot's Length lags its range are those of ItemRef.Chars.cs: no compaction hook may sit in them.
internal readonly partial struct ItemRef
{
    // Copies the values into the arena and makes the range the slot's.
    private static void StoreValues(Slab slab, int handle, ReadOnlySpan<object> values)
    {
        var payload = slab.Values.Append(values);
        slab.Write(handle).Payload = payload;
    }

    /// <summary>
    /// A new Any item (not integrated) between <paramref name="left"/> and <paramref name="right"/> whose values are a copy
    /// of <paramref name="values"/>, which must not be empty.
    /// </summary>
    public static ItemRef New(Slab slab, ID id, ItemRef left, ItemRef right, AbstractType parent, string parentSub, ReadOnlySpan<object> values) =>
        NewItem(slab, id, left, left.IsNull ? null : left.LastId, right, right.IsNull ? null : right.Id, parent, parentSub, SlotKind.Any, values.Length, null, default, values, default);

    // The part before offset leaves the item (an item integrated from an offset): its range is the rest.
    private void CutValuesHead(int offset)
    {
        var values = _slab.Values;
        var start = Read().Payload;
        values.Clear(start, offset);
        values.MarkDead(start, offset);
        Write().Payload = start + offset;
    }

    // The range of the right part after a split of this Any at diff; the tail reservation of the range stays with its end,
    // which the right part holds.
    private long SplitValues(int diff) => Read().Payload + diff;

    // The range of the right neighbour joins this one's; Length is not yet the sum. A run that is copied away has its
    // old values cleared at once.
    private void MergeValues(ItemRef right)
    {
        var values = _slab.Values;
        var leftStart = Read().Payload;
        var leftLength = Read().Length;
        var rightStart = right.Read().Payload;
        var rightLength = right.Read().Length;
        if (!CopyOnMergeForTests && leftStart + leftLength == rightStart)
        {
            return;
        }

        var moved = values.Extend(leftStart, leftLength, values.Slice(rightStart, rightLength));
        values.Clear(rightStart, rightLength);
        values.MarkDead(rightStart, rightLength);
        if (moved != leftStart)
        {
            values.Clear(leftStart, leftLength);
        }

        Write().Payload = moved;
    }

    // The range of this Any ends: it is collected, or its slot is freed.
    private void ReleaseValues()
    {
        ref readonly var slot = ref Read();
        var values = _slab.Values;
        values.Clear(slot.Payload, slot.Length);
        values.MarkDead(slot.Payload, slot.Length);
    }

    /// <summary>
    /// The raw values of a <see cref="SlotKind.Any"/> item, read from the arena. The span is valid only until user code runs
    /// or a batch ends (a compaction moves and frees ranges): consume it before.
    /// </summary>
    internal ReadOnlySpan<object> ValueSpan
    {
        get
        {
            ref readonly var slot = ref Read();
            return slot.Kind == (int)SlotKind.Any
                ? _slab.Values.Slice(slot.Payload, slot.Length)
                : throw new InvalidOperationException($"The struct holds {Kind}, not {SlotKind.Any} content.");
        }
    }

    /// <summary>The <paramref name="index"/>th value of a <see cref="SlotKind.Any"/> item, read from the arena.</summary>
    internal object ValueAt(int index) => ValueFreezer.OnRead(ValueSpan[index]);

    /// <summary>
    /// A read-only copy of the values of a <see cref="SlotKind.Any"/> item, as <see cref="FrozenList"/> hands them out. It is a
    /// copy, not a view of the range: its reader (an <see cref="ItemView"/>, an event handler) may run after the batch
    /// has moved or freed the range.
    /// </summary>
    internal IReadOnlyList<object> Values => new ContentView(ValueSpan.ToArray());

    /// <summary>Appends <paramref name="values"/> to a <see cref="SlotKind.Any"/> item; the caller adjusts <see cref="Length"/>.</summary>
    internal void AppendValues(ReadOnlySpan<object> values)
    {
        StructStore.CompactForTests(_slab, true);
        if (Kind != SlotKind.Any)
        {
            throw new InvalidOperationException($"The struct holds {Kind}, not {SlotKind.Any} content.");
        }

        var arena = _slab.Values;
        ref var slot = ref Write();
        var moved = arena.Extend(slot.Payload, slot.Length, values);
        if (moved != slot.Payload)
        {
            arena.Clear(slot.Payload, slot.Length);
        }

        slot.Payload = moved;
    }

    // The values from offset on, as ContentAny.Write encodes them.
    private void WriteValues(IUpdateEncoder encoder, int offset)
    {
        var values = ValueSpan;
        encoder.WriteLength(values.Length - offset);
        for (var i = offset; i < values.Length; i++)
        {
            encoder.WriteAny(values[i]);
        }
    }

    /// <summary><see cref="CanExtendRun"/> for <see cref="SlotKind.Any"/> values.</summary>
    internal bool CanExtendAnyRun(Transaction transaction, AbstractType parent, ItemRef right, long client) =>
        CanExtendRun(transaction, parent, right, SlotKind.Any, client);
}
