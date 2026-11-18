namespace Ycs.Budgets;

/// <summary>
/// THE budget file: allocated bytes and time per operation for each scenario in
/// <see cref="Scenarios"/>. Bytes are today's measurement plus about 15 %, so a regression fails;
/// when a change makes a scenario cheaper, lower its number here (the test prints the measured
/// values and a suggested budget). Times are loose ceilings (about 10x today's on a developer
/// machine) that only catch an algorithmic regression, not noise.
/// Measured with -c Release on .NET 11.0.0, x64. A struct of the store is a 64-byte slot and no object; the numbers
/// that were lowered with the removal of the objects say so.
/// </summary>
internal static class Budgets
{
    public static readonly IReadOnlyDictionary<string, Budget> All = new Dictionary<string, Budget>(StringComparer.Ordinal)
    {
        // name                                           bytes/op   µs/op ceiling   (today: bytes, µs)
        ["text.insert-char-at-caret-100k"] = new(0, 50),               // 0 B, 1 µs (was 204 B: an item and its content per keystroke, merged at the end of the transaction; the run takes the text in the arena)
        ["text.insert-char-in-document-of-5000-clients"] = new(0, 30),  // 0 B, 3 µs (a transaction's work is that of the clients it changes; it was 903 KB and 245 µs: both state vectors copied, their dictionaries made again)
        ["doc.apply-remote-char-update-100k"] = new(0, 50),            // 0 B, 1 µs (was 172 B: the update is read into the reused staging table and adopted into a 64-byte slot, the slot of a merged-away item reused; no struct object)
        ["doc.encode-small-document-v1"] = new(104, 50),               // 88 B, 0.4 µs
        ["doc.encode-diff-of-one-edit-100k"] = new(7656, 2000),        // 6 656 B, 220 µs (the delete set written from the store)
        ["doc.encode-diff-100k-into-buffer-writer"] = new(0, 2000),    // 0 B, 220 µs (v1 written straight into the destination)
        ["text.to-string-100k"] = new(0, 15000),                       // 0 B, 0 µs (the string is kept until the text changes)
        ["text.to-string-after-edit-100k"] = new(230784, 15000),       // 200 166 B, 600 µs (an insert, then the string built again: the 200 KB are the string itself)
        ["position.relative-from-index-deep-100k"] = new(0, 5000),     // 0 B, 250 µs (walks the list from the start, as Yjs does)
        ["position.absolute-from-relative-deep-100k"] = new(0, 5000), // 0 B, 260 µs (walks the list to the start, as Yjs does)
        ["map.set-existing-key"] = new(368, 50),                       // 318 B, 1 µs (was 588 B: a HashSet for the changed key, a new list for the deletion, per set; the item is a slot, not an object, and its value is in the arena: no content object)
        ["text.insert-formatted-char-100k"] = new(360, 2000),          // 308 B, 180 µs (finds the position without search markers, as Yjs does; was 508 B)
        ["text.event-delta-read-100k"] = new(976, 6000),              // 844 B, 830 µs (the delta walks the whole text, as Yjs' does)
        ["undo.undo-redo-typing-100k"] = new(3336, 50),                // 2 900 B, 2 µs
        ["undo.redo-after-undoing-typing-100k"] = new(1956, 50),       // 1 700 B, 2 µs (was about 40 KB: each split copied the rest of the undone run)
        ["undo.delete-backwards-one-capture-100k"] = new(510, 50),     // 443 B, 3 µs (was 3 100 B: the deleted run was copied on every merge)
        ["array.push-100k-in-one-transaction"] = new(2425568, 800000),  // 2.1 MB, 15 ms (was beyond 8 GiB: each push walked the list, the end of the transaction copied the run into each left neighbour). The values are in the arena only: a run longer than a page doubles by copy (8 B per value, garbage included until the end of the batch compacts it)
        ["text.type-100k-in-one-transaction"] = new(611400, 840000),    // 532 KB, 15 ms (was 58 s and 22 MB: each marker lookup walked back over the transaction's keystrokes, each made an item). The text is in the arena only: a run longer than a page doubles by copy (2 B per character, garbage included until the end of the batch compacts it), 100,000 characters held in 131,078 at the end
        ["array.enumerate-100k"] = new(256, 20000),                   // 0 B, 2 ms (the struct enumerator; the copy of the run comes from the array pool)
        ["array.delete-random-100k"] = new(216, 52),                 // 183 B, 3 µs (the marker walks cross the pieces split off the item through the run-head index; Yjs' loops walk them; a split moves no content object: the right part's range is the left's offset plus the cut)
        ["doc.encode-state-with-pending-100k"] = new(814352, 80000),  // 708 KB, 6.5 ms (the state merged with what is pending, as Yjs does)
        // The operations on encoded updates, read and written without a document (decoders rewritten in place: these must not grow).
        ["update.merge-10-large-v1"] = new(333312, 80000),          // 289.8 KB, 7.8 ms
        ["update.merge-two-states-100k-20k-v1"] = new(523296, 90000),          // 455.0 KB, 8.9 ms
        ["update.diff-100k-v1"] = new(406056, 50000),          // 353.1 KB, 3.7 ms
        ["update.convert-100k-v1"] = new(301928, 50000),          // 262.5 KB, 4.1 ms
        ["update.encode-state-vector-100k-v1"] = new(760, 40000),          // 656 B, 3.4 ms
        ["update.parse-meta-100k-v1"] = new(936, 40000),          // 808 B, 2.4 ms
        ["update.merge-10-large-v2"] = new(268208, 80000),          // 233.2 KB, 3.9 ms
        ["update.merge-two-states-100k-20k-v2"] = new(403488, 80000),          // 350.9 KB, 7.2 ms
        ["update.diff-100k-v2"] = new(303088, 150000),          // 263.6 KB, 4.1 ms (14 ms in an earlier run)
        ["update.convert-100k-v2"] = new(407416, 50000),          // 354.3 KB, 3.6 ms
        ["update.encode-state-vector-100k-v2"] = new(1928, 40000),          // 1 672 B, 2.5 ms
        ["update.parse-meta-100k-v2"] = new(2104, 40000),          // 1 824 B, 2.4 ms
    };
}

internal sealed record Budget(long BytesPerOperation, double MicrosecondsPerOperation);
