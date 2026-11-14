using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs.Corpus;

/// <summary>Asserts <see cref="ItemListInvariants"/> on every sequence of a doc, one update at a time.</summary>
internal sealed class Invariants(CorpusDoc doc, IReadOnlyList<(string Name, string Type)> roots)
{
    private Dictionary<string, ItemListInvariants.Report> _last = [];

    public static readonly IReadOnlyList<(string Name, string Type)> Text = [("text", "text")];

    public IReadOnlyDictionary<string, ItemListInvariants.Report> Last => _last;

    /// <summary>
    /// Checks every sequence and compares it with the previous check of the same doc
    /// (insertions only, nothing moves). <paramref name="context"/> goes into the failure message.
    /// </summary>
    public void Assert(bool full, string context)
    {
        var next = new Dictionary<string, ItemListInvariants.Report>();
        foreach (var (path, runs) in doc.Sequences(roots))
        {
            // A path through an array index ("[2]") names a different sequence once something is
            // inserted before it, so only paths of root and map keys compare with the last check.
            var previous = path.Contains('[') ? null : _last.GetValueOrDefault(path);
            var report = ItemListInvariants.Check(runs, full, previous);
            if (report.Violations.Count > 0)
            {
                Microsoft.VisualStudio.TestTools.UnitTesting.Assert.Fail(
                    $"Lean list invariant violated in sequence '{path}' {context}:\n  {string.Join("\n  ", report.Violations)}\n" +
                    $"item order: {ItemListInvariants.Runs(report)}");
            }

            next[path] = report;
        }

        _last = next;
    }
}
