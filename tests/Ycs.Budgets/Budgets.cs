namespace Ycs.Budgets;

/// <summary>
/// THE budget file: allocated bytes and time per operation for each scenario in
/// <see cref="Scenarios"/>. Bytes are today's measurement plus about 15 %, so a regression fails;
/// when a change makes a scenario cheaper, lower its number here (the test prints the measured
/// values and a suggested budget). Times are loose ceilings (about 10x today's on a developer
/// machine) that only catch an algorithmic regression, not noise.
/// Measured with -c Release on .NET 11.0.0, x64.
/// </summary>
internal static class Budgets
{
    public static readonly IReadOnlyDictionary<string, Budget> All = new Dictionary<string, Budget>(StringComparer.Ordinal)
    {
        // name                                           bytes/op   µs/op ceiling   (today: bytes, µs)
        ["text.insert-char-at-caret-100k"] = new(1584, 50),            // 1 372 B, 3 µs
        ["doc.apply-remote-char-update-100k"] = new(3360, 50),         // 2 916 B, 4 µs
        ["doc.encode-small-document-v1"] = new(1544, 50),              // 1 336 B, 1 µs
        ["doc.encode-diff-of-one-edit-100k"] = new(103904, 2000),      // 90 348 B, 120 µs (writes the whole delete set)
        ["doc.encode-diff-100k-into-buffer-writer"] = new(95184, 2000), // 82 768 B, 150 µs (as above, then a private buffer copied once)
        ["text.to-string-100k"] = new(0, 15000),                       // 0 B, 0 µs (the string is kept until the text changes)
        ["text.to-string-after-edit-100k"] = new(233176, 15000),       // 202 735 B, 800 µs (an insert, then the string built again)
        ["position.relative-from-index-deep-100k"] = new(96, 5000),    // 80 B, 330 µs (walks the list from the start, as Yjs does)
        ["position.absolute-from-relative-deep-100k"] = new(40, 5000), // 32 B, 390 µs (walks the list to the start, as Yjs does)
        ["map.set-existing-key"] = new(1304, 50),                      // 1 132 B, 2 µs
    };
}

internal sealed record Budget(long BytesPerOperation, double MicrosecondsPerOperation);
