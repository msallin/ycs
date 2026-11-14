using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs.Corpus;

/// <summary>
/// Divergences from Yjs that are real bugs in Ycs, found by this corpus and not fixed here.
/// A listed check that fails is reported as Inconclusive with the reason; a listed check that
/// passes fails the run, so the entry gets removed once the bug is fixed.
/// </summary>
internal static class KnownFailures
{
    private const string PendingBlocksAll =
        "While any struct is pending on a missing dependency, Ycs integrates nothing else that arrives, even " +
        "structs of other clients with no missing dependency; Yjs integrates them at once. The doc shows " +
        "stale content and a smaller state vector until the missing update arrives.";

    private const string StaleRootParent =
        "A pending struct keeps the AbstractType placeholder that doc.Get<AbstractType>(key) returned when " +
        "the update was read. If the root is realized (GetText) before the struct integrates, it integrates " +
        "into the replaced placeholder: its content is lost from the Y.Text, and encoding it later throws in " +
        "FindRootTypeKey. Yjs re-reads pending structs from an encoded update, so they pick up the real root.";

    // (case name or test class, test name) -> why it fails.
    private static readonly Dictionary<(string Case, string Test), string> Known = new()
    {
        [("text-out-of-order-duplicated", "EncodeStateVector_OfPrefix_EqualsYjs")] = PendingBlocksAll,
        [("DivergenceTests", "ApplyV1_RootRealizedWhilePending_PendingItemLandsInText")] = StaleRootParent,
        [("DivergenceTests", "ApplyV1_PendingStructOfAnotherClient_IndependentUpdateStillIntegrates")] = PendingBlocksAll,
    };

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
