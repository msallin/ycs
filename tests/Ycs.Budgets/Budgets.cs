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
        ["text.insert-char-at-caret-100k"] = new(4232, 50),            // 3 676 B, 3 µs
        ["doc.apply-remote-char-update-100k"] = new(7784, 50),         // 6 764 B, 3 µs
        ["doc.encode-small-document-v1"] = new(1896, 50),              // 1 648 B, 2 µs
        ["doc.encode-diff-of-one-edit-100k"] = new(103904, 2000),      // 90 348 B, 120 µs (writes the whole delete set)
        ["text.to-string-100k"] = new(460576, 15000),                  // 400 500 B, 1 050 µs
        ["position.relative-from-index-deep-100k"] = new(160, 5000),   // 136 B, 400 µs (walks the list from the start, as Yjs does)
        ["position.absolute-from-relative-deep-100k"] = new(40, 5000), // 32 B, 390 µs (walks the list to the start, as Yjs does)
        ["map.set-existing-key"] = new(4112, 50),                      // 3 572 B, 2 µs
    };
}

internal sealed record Budget(long BytesPerOperation, double MicrosecondsPerOperation);
