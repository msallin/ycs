
namespace Ycs.Client;

/// <summary>
/// One change of a text: <see cref="Removed"/> code units at <see cref="Start"/> are replaced by
/// <see cref="Inserted"/>. Positions are UTF-16 code units, as in Yjs and in a textarea.
/// </summary>
/// <param name="Start">Where the change starts.</param>
/// <param name="Removed">How many code units of the old text it removes.</param>
/// <param name="Inserted">What it puts in their place.</param>
internal readonly record struct TextDiff(int Start, int Removed, string Inserted)
{
    private const int SuffixBlock = 256;

    /// <summary>Gets a value indicating whether the change changes nothing.</summary>
    public bool IsEmpty => Removed == 0 && Inserted.Length == 0;

    /// <summary>
    /// The one replacement that turns <paramref name="before"/> into <paramref name="after"/>: the common prefix and
    /// suffix stay. Typing "o" before an "o" gives the same text as typing it after; <paramref name="caret"/> (in
    /// <paramref name="after"/>; by default its end) tells
    /// which, and it matters to the others, whose text may be between the two. It never splits a surrogate pair.
    /// </summary>
    public static TextDiff Between(string before, string after, int caret = int.MaxValue)
    {
        // The edit ends at the caret at the latest, so it starts at most as far before it as the text grew.
        var prefix = Math.Min(
            before.AsSpan().CommonPrefixLength(after),
            Math.Max(0, Math.Min(caret, after.Length) - Math.Max(0, after.Length - before.Length)));

        // A pair split in two would reach other clients as two lone surrogates.
        if (prefix > 0 && char.IsHighSurrogate(before[prefix - 1]))
        {
            prefix--;
        }

        var limit = Math.Min(before.Length, after.Length) - prefix;
        var suffix = 0;

        // In blocks first: SequenceEqual is vectorized, a loop over characters is not (WebAssembly interprets it).
        while (limit - suffix >= SuffixBlock
            && before.AsSpan(before.Length - suffix - SuffixBlock, SuffixBlock).SequenceEqual(after.AsSpan(after.Length - suffix - SuffixBlock, SuffixBlock)))
        {
            suffix += SuffixBlock;
        }

        while (suffix < limit && before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix])
        {
            suffix++;
        }

        if (suffix > 0 && char.IsLowSurrogate(before[^suffix]))
        {
            suffix--;
        }

        return new TextDiff(prefix, before.Length - prefix - suffix, after[prefix..^suffix]);
    }

    /// <summary>
    /// Where a selection of the text before <paramref name="changes"/> is after them. A caret and a selection's end
    /// stay before text inserted right at them, so that two people typing at one place do not interleave; a
    /// selection's start follows the character after it. A position inside removed text goes to where the removal was.
    /// </summary>
    public static (int Start, int End) MoveSelection(int start, int end, IReadOnlyList<TextDiff> changes)
    {
        var collapsed = start == end;
        foreach (var change in changes)
        {
            start = change.Move(start, !collapsed);
            end = change.Move(end, false);
        }

        return (start, Math.Max(start, end));
    }

    /// <summary>
    /// Adds a Y.Text event's delta to <paramref name="changes"/>, one change per insert or delete. An embed or other
    /// content that is not a string counts as one placeholder character, as it does in the Y.Text's positions; an embed
    /// whose value is null (another peer's <c>{ insert: null }</c>) arrives as <see cref="Delta.NullEmbed"/>, which is
    /// not a string and is not a missing insert, so it too counts as one placeholder.
    /// </summary>
    public static void FromDelta(IReadOnlyList<Delta> delta, List<TextDiff> changes)
    {
        var at = 0;
        foreach (var op in delta)
        {
            if (op.Retain is { } retain)
            {
                at += retain;
            }
            else if (op.Delete is { } delete)
            {
                changes.Add(new TextDiff(at, delete, string.Empty));
            }
            else if (op.Insert is not null)
            {
                var inserted = op.Insert as string ?? "\uFFFC";
                changes.Add(new TextDiff(at, 0, inserted));
                at += inserted.Length;
            }
        }
    }

    /// <summary>
    /// Where a position of the text before this change is after it: text inserted exactly at it goes after it when
    /// <paramref name="after"/> (the position moves right), before it otherwise; a position inside removed text goes
    /// to where the removal was.
    /// </summary>
    public int Move(int index, bool after)
    {
        if (Start + Removed <= index)
        {
            index -= Removed;
        }
        else if (Start < index)
        {
            index = Start;
        }

        return Start < index || (Start == index && after) ? index + Inserted.Length : index;
    }
}
