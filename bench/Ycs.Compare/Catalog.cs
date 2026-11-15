namespace Ycs.Compare;

using YDotNet.Document.Options;

/// <summary>
/// A workload: the same <see cref="Name"/> on both libraries. <see cref="Root"/> names the root type whose content the
/// checks decode from each library's state; <see cref="Expected"/>, where a plain model exists, is the content
/// <see cref="Prepared.Content"/> must return.
/// </summary>
internal sealed record Case(string Name, string Group, string What, string Root, Func<Fixtures, Prepared> Ycs, Func<Fixtures, Prepared> Yrs, Func<Fixtures, string> Expected)
{
    /// <summary>A workload that takes a minute or more on one library: no warm-up and one timed run per process.</summary>
    public bool Heavy { get; init; }
}

internal static class Catalog
{
    private static string Typed() => string.Concat(Inputs.Typing);

    private static string Typed(int n) => string.Concat(Inputs.Typing.Take(n));

    public static readonly IReadOnlyList<Case> All =
    [
        new("text-append", "text", "100,000 one-character inserts at the end, one transaction each", "text",
            YcsCases.TextAppend, YrsCases.TextAppend, _ => Typed()),
        new("text-append-utf8", "text", "text-append, YDotNet with DocEncoding.Utf8 (UTF-8 indexes: the same for this ASCII input, not interoperable with Yjs for other text); ycs as in text-append", "text",
            YcsCases.TextAppend, f => YrsCases.TextAppend(f, DocEncoding.Utf8), _ => Typed()),
        new("text-append-1txn", "text", "the same 100,000 inserts in one transaction (batched form)", "text",
            f => YcsCases.TextAppendOneTxn(f, Inputs.TypingN), f => YrsCases.TextAppendOneTxn(f, Inputs.TypingN), _ => Typed()) { Heavy = true },
        new("text-append-1txn-25k", "text", "the first 25,000 of them in one transaction (how the batched form scales)", "text",
            f => YcsCases.TextAppendOneTxn(f, 25_000), f => YrsCases.TextAppendOneTxn(f, 25_000), _ => Typed(25_000)),
        new("txn-empty", "text", "100,000 empty write transactions (the fixed cost of a transaction)", "text",
            YcsCases.EmptyTransactions, YrsCases.EmptyTransactions, _ => string.Empty),
        new("text-random", "text", "20,000 one-character inserts at random positions, one transaction each", "text",
            f => YcsCases.TextRandom(f, 20_000), f => YrsCases.TextRandom(f, 20_000), _ => Inputs.Replay(string.Empty, Inputs.RandomTyping.Take(20_000))),
        new("text-random-100k", "text", "the same, 100,000 inserts", "text",
            f => YcsCases.TextRandom(f, 100_000), f => YrsCases.TextRandom(f, 100_000), _ => Inputs.Replay(string.Empty, Inputs.RandomTyping)) { Heavy = true },
        new("trace-replay", "text", "automerge-paper trace: 259,778 edits, one transaction each, as recorded", "text",
            YcsCases.TraceReplay, YrsCases.TraceReplay, f => f.Trace.EndContent),
        new("concurrent-20", "text", "20 clients, 250 offline edits each on a shared 1,000-character text; encode the 20 diffs, apply the 380 (one transaction each)", "text",
            YcsCases.Concurrent, YrsCases.Concurrent, null),

        new("apply-state-v1/ycs", "apply", "load the trace's final state, v1 as ycs encodes it, into an empty document; 10 times", "text",
            f => YcsCases.ApplyState(f, "trace.ycs.v1", false), f => YrsCases.ApplyState(f, "trace.ycs.v1", false), f => f.Trace.EndContent),
        new("apply-state-v1/yrs", "apply", "the same, v1 as yrs encodes it", "text",
            f => YcsCases.ApplyState(f, "trace.yrs.v1", false), f => YrsCases.ApplyState(f, "trace.yrs.v1", false), f => f.Trace.EndContent),
        new("apply-state-v2/ycs", "apply", "the same, v2 as ycs encodes it", "text",
            f => YcsCases.ApplyState(f, "trace.ycs.v2", true), f => YrsCases.ApplyState(f, "trace.ycs.v2", true), f => f.Trace.EndContent),
        new("apply-state-v2/yrs", "apply", "the same, v2 as yrs encodes it", "text",
            f => YcsCases.ApplyState(f, "trace.yrs.v2", true), f => YrsCases.ApplyState(f, "trace.yrs.v2", true), f => f.Trace.EndContent),
        new("apply-updates/ycs", "apply", "apply the trace's 259,778 per-transaction v1 updates (as ycs emitted them) in order, one transaction each", "text",
            f => YcsCases.ApplyUpdates(f, "updates.ycs.v1", false), f => YrsCases.ApplyUpdates(f, "updates.ycs.v1", false), f => f.Trace.EndContent),
        new("apply-updates/yrs", "apply", "the same, the updates yrs emitted", "text",
            f => YcsCases.ApplyUpdates(f, "updates.yrs.v1", false), f => YrsCases.ApplyUpdates(f, "updates.yrs.v1", false), f => f.Trace.EndContent),
        new("apply-updates-1txn/yrs", "apply", "the updates yrs emitted, all in one transaction (batched form)", "text",
            f => YcsCases.ApplyUpdates(f, "updates.yrs.v1", true), f => YrsCases.ApplyUpdates(f, "updates.yrs.v1", true), f => f.Trace.EndContent),

        new("encode-v1", "encode", "the loaded trace document: state as a v1 update, 10 times", "text",
            f => YcsCases.Encode(f, "v1"), f => YrsCases.Encode(f, "v1"), f => f.Trace.EndContent),
        new("encode-v2", "encode", "the same as v2, 10 times", "text",
            f => YcsCases.Encode(f, "v2"), f => YrsCases.Encode(f, "v2"), f => f.Trace.EndContent),
        new("encode-sv", "encode", "its state vector, 1,000 times", "text",
            f => YcsCases.Encode(f, "sv"), f => YrsCases.Encode(f, "sv"), f => f.Trace.EndContent),
        new("encode-diff", "encode", "a v1 diff against the state vector halfway through the trace, 10 times", "text",
            f => YcsCases.Encode(f, "diff"), f => YrsCases.Encode(f, "diff"), f => f.Trace.EndContent),

        new("map-set-distinct", "map", "100,000 sets of distinct keys, one transaction each", "map",
            f => YcsCases.MapSetDistinct(f, false), f => YrsCases.MapSetDistinct(f, false), _ => MapModel()),
        new("map-set-distinct-1txn", "map", "the same in one transaction (batched form)", "map",
            f => YcsCases.MapSetDistinct(f, true), f => YrsCases.MapSetDistinct(f, true), _ => MapModel()),
        new("map-set-same", "map", "100,000 sets of the same key, one transaction each", "map",
            YcsCases.MapSetSame, YrsCases.MapSetSame, _ => $"key={Inputs.MapN - 1}\n"),
        new("array-push", "array", "100,000 pushes of an integer, one transaction each", "array",
            f => YcsCases.ArrayPush(f, false, Inputs.ArrayN), f => YrsCases.ArrayPush(f, false, Inputs.ArrayN), _ => Canonical.Array(Enumerable.Range(0, Inputs.ArrayN).Select(i => (long)i))),
        new("array-push-1txn", "array", "the same in one transaction (batched form)", "array",
            f => YcsCases.ArrayPush(f, true, Inputs.ArrayN), f => YrsCases.ArrayPush(f, true, Inputs.ArrayN), _ => Canonical.Array(Enumerable.Range(0, Inputs.ArrayN).Select(i => (long)i))) { Heavy = true },
        new("array-push-1txn-20k", "array", "20,000 pushes in one transaction (how the batched form scales)", "array",
            f => YcsCases.ArrayPush(f, true, 20_000), f => YrsCases.ArrayPush(f, true, 20_000), _ => Canonical.Array(Enumerable.Range(0, 20_000).Select(i => (long)i))),
        new("array-insert-random", "array", "50,000 integers inserted at random positions, one transaction each", "array",
            YcsCases.ArrayInsertRandom, YrsCases.ArrayInsertRandom, _ => ArrayInsertModel()),
        new("array-delete-random", "array", "50,000 single deletes at random positions of a 100,000-element array, one transaction each", "array",
            YcsCases.ArrayDeleteRandom, YrsCases.ArrayDeleteRandom, _ => ArrayDeleteModel()) { Heavy = true },

        new("read-text", "read", "the loaded trace document's text as a string, 100 times", "text",
            YcsCases.ReadText, YrsCases.ReadText, f => $"{100L * f.Trace.EndContent.Length} {f.Trace.EndContent}"),
        new("read-array", "read", "iterate a 100,000-integer array, 10 times", "array",
            YcsCases.ReadArray, YrsCases.ReadArray, _ => (10L * Inputs.ArrayN * (Inputs.ArrayN - 1) / 2).ToString()),
        new("read-map", "read", "iterate the keys and values of a 100,000-key map, 10 times", "map",
            YcsCases.ReadMap, YrsCases.ReadMap, _ => (10L * Enumerable.Range(0, Inputs.MapN).Sum(i => (long)Inputs.Keys[i].Length + i)).ToString()),

        new("undo-type", "undo", "20,000 keystrokes with an undo manager attached (capture timeout 0)", "text",
            f => YcsCases.Undo(f, "type"), f => YrsCases.Undo(f, "type"), _ => $"{Inputs.UndoN} {Typed(Inputs.UndoN)}"),
        new("undo-all", "undo", "undo the 20,000 keystrokes one step at a time", "text",
            f => YcsCases.Undo(f, "undo"), f => YrsCases.Undo(f, "undo"), _ => $"{Inputs.UndoN} "),
        new("redo-all", "undo", "redo them one step at a time", "text",
            f => YcsCases.Undo(f, "redo"), f => YrsCases.Undo(f, "redo"), _ => $"{Inputs.UndoN} {Typed(Inputs.UndoN)}"),

        new("observe-count", "observe", "text-append with a text observer that counts its events", "text",
            f => YcsCases.Observe(f, false), f => YrsCases.Observe(f, false), _ => $"{Inputs.TypingN} {Typed()}"),
        new("observe-delta", "observe", "text-append with a text observer that reads each event's delta", "text",
            f => YcsCases.Observe(f, true), f => YrsCases.Observe(f, true), _ => $"{2 * Inputs.TypingN - 1} {Typed()}"),
    ];

    public static Case Find(string name) => All.Single(c => c.Name == name);

    private static string MapModel() => Canonical.Map(Enumerable.Range(0, Inputs.MapN).Select(i => (Inputs.Keys[i], (long)i)));

    private static string ArrayInsertModel()
    {
        var list = new List<long>();
        for (var i = 0; i < Inputs.ArrayInserts.Length; i++)
        {
            list.Insert(Inputs.ArrayInserts[i], i);
        }

        return Canonical.Array(list);
    }

    private static string ArrayDeleteModel()
    {
        var list = Enumerable.Range(0, Inputs.ArrayN).Select(i => (long)i).ToList();
        foreach (var p in Inputs.ArrayDeletes)
        {
            list.RemoveAt(p);
        }

        return Canonical.Array(list);
    }
}
