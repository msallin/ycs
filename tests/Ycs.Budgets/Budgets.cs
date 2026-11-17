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
        ["text.insert-char-at-caret-100k"] = new(8, 50),               // 4 B, 1 µs (was 204 B: an item and its content per keystroke, merged at the end of the transaction)
        ["doc.apply-remote-char-update-100k"] = new(200, 50),          // 172 B, 1 µs (a 64-byte slot per item, the slot of a merged-away item reused; no item object)
        ["doc.encode-small-document-v1"] = new(104, 50),               // 88 B, 0.4 µs
        ["doc.encode-diff-of-one-edit-100k"] = new(7656, 2000),        // 6 656 B, 220 µs (the delete set written from the store)
        ["doc.encode-diff-100k-into-buffer-writer"] = new(0, 2000),    // 0 B, 220 µs (v1 written straight into the destination)
        ["text.to-string-100k"] = new(0, 15000),                       // 0 B, 0 µs (the string is kept until the text changes)
        ["text.to-string-after-edit-100k"] = new(230784, 15000),       // 200 680 B, 800 µs (an insert, then the string built again)
        ["position.relative-from-index-deep-100k"] = new(0, 5000),     // 0 B, 250 µs (walks the list from the start, as Yjs does)
        ["position.absolute-from-relative-deep-100k"] = new(0, 5000), // 0 B, 260 µs (walks the list to the start, as Yjs does)
        ["map.set-existing-key"] = new(488, 50),                       // 422 B, 1 µs (was 588 B: a HashSet for the changed key, a new list for the deletion, per set; the item is a slot, not an object; a deleted item holds no content object)
        ["text.insert-formatted-char-100k"] = new(360, 2000),          // 308 B, 180 µs (finds the position without search markers, as Yjs does; was 508 B)
        ["text.event-delta-read-100k"] = new(976, 6000),              // 844 B, 830 µs (the delta walks the whole text, as Yjs' does)
        ["undo.undo-redo-typing-100k"] = new(3384, 50),                // 2 940 B, 2 µs
        ["undo.redo-after-undoing-typing-100k"] = new(2064, 50),       // 1 808 B, 2 µs (was about 40 KB: each split copied the rest of the undone run)
        ["undo.delete-backwards-one-capture-100k"] = new(548, 50),     // 475 B, 3 µs (was 3 100 B: the deleted run was copied on every merge)
        ["array.push-100k-in-one-transaction"] = new(2415904, 800000),  // 2.1 MB, 14 ms (was beyond 8 GiB: each push walked the list, the end of the transaction copied the run into each left neighbour)
        ["text.type-100k-in-one-transaction"] = new(855000, 840000),    // 743 KB, 17 ms (was 58 s and 22 MB: each marker lookup walked back over the transaction's keystrokes, each made an item). 8.6 removes the string the arena shadows (218 KB); the arena's own pages stay: 525 KB, a run longer than a page doubles by copy (2 B per character, garbage included until the end of the batch compacts it), 100,000 characters held in 131,078 at the end)
        ["array.delete-random-100k"] = new(2464, 52),                 // 2 142 B, 4 µs (the marker walks cross the pieces split off the item through the run-head index; Yjs' loops walk them)
        ["doc.encode-state-with-pending-100k"] = new(814352, 80000),  // 708 KB, 6.5 ms (the state merged with what is pending, as Yjs does)
    };
}

internal sealed record Budget(long BytesPerOperation, double MicrosecondsPerOperation);
