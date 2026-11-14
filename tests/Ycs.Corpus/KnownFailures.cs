using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs.Corpus;

/// <summary>
/// Divergences from Yjs that are real bugs in Ycs, found by this corpus and not fixed here.
/// A listed check that fails is reported as Inconclusive with the reason; a listed check that
/// passes fails the run, so the entry gets removed once the bug is fixed.
/// </summary>
internal static class KnownFailures
{
    // (case name or test class, test name) -> why it fails.
    private static readonly Dictionary<(string Case, string Test), string> Known = new()
    {
    };

    // Every adversarial-* case of gen.mjs hits StateVectorOrder, except the one whose clients
    // happen to enter Ycs' store in descending id order.
    static KnownFailures()
    {
        foreach (var c in CorpusCase.All.Where(c => c.Name.StartsWith("adversarial-", StringComparison.Ordinal) && c.Name != "adversarial-yata-pseudocode-a3-b7-c5"))
        {
            Known[(c.Name, "EncodeStateVector_AfterUpdates_BytesEqualYjs")] = StateVectorOrder;
        }
    }

    public static void Check(string caseName, string testName, Action assertion)
    {
        if (!Known.TryGetValue((caseName, testName), out var reason))
        {
            assertion();
            return;
        }

        try
        {
            assertion();
        }
        catch (Exception e) when (e is not AssertInconclusiveException)
        {
            Assert.Inconclusive($"KNOWN FAILURE ({caseName} / {testName}): {reason}{Environment.NewLine}{e.GetType().Name}: {e.Message}");
        }

        Assert.Fail($"{caseName} / {testName} is listed in KnownFailures but now passes; remove the entry.");
    }
}
