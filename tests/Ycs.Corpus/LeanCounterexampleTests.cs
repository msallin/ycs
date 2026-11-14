using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs.Corpus;

/// <summary>
/// The checker itself: lists that break one Lean invariant each must be reported, and the
/// counterexample of the Lean development (Insert/Basic.lean, namespace InconsistencyExample,
/// theorem `inconsistent1_inconsistent2`) must behave in Ycs as it does in Lean and in Yjs.
/// </summary>
[TestClass]
public class LeanCounterexampleTests
{
    // Insert/Basic.lean InconsistencyExample: a = (0,0) origin first; b = (1,0) origin a;
    // n = (3,0) origin a; o = (2,0) origin first, right origin b. a, b and n are real Yjs 13.6.32
    // updates. o cannot come out of Yjs (a client that has b has a, so o's origin would be a); it
    // is hand-encoded: one struct of client 2 at clock 0, info 0x44 (right origin, string), right
    // origin 1:0, "o", empty delete set.
    private const string A = "AQEAAAQBBHRleHQBYQA=";
    private const string B = "AQEBAIQAAAFiAA==";
    private const string O = "AQECAEQBAAFvAA==";
    private const string N = "AQEDAIQAAAFuAA==";

    private static CorpusDoc Apply(params string[] updates)
    {
        var doc = CorpusDoc.Create();
        _ = doc.Text("text");
        foreach (var u in updates)
        {
            doc.ApplyV1(Convert.FromBase64String(u));
        }

        return doc;
    }

    // Lean: inconsistent1 = "anob", inconsistent2 = "aobn"; Yjs 13.6.32 gives the same.
    [TestMethod]
    public void ApplyV1_LeanCounterexampleOThenN_GivesAnobLikeLeanAndYjs() => Assert.AreEqual("anob", Apply(A, B, O, N).Text("text"));

    [TestMethod]
    public void ApplyV1_LeanCounterexampleNThenO_GivesAobnLikeLeanAndYjs() => Assert.AreEqual("aobn", Apply(A, B, N, O).Text("text"));

    // The state is one the Lean invariant excludes: a is reachable from o (through b's origin)
    // and lies strictly between o's origin (first) and right origin (b), which is I3.
    [TestMethod]
    public void Check_LeanCounterexample_ReportsOriginAdjacency()
    {
        var report = ItemListInvariants.Check(Apply(A, B, O, N).Items("text", "text"), full: true);
        Assert.IsTrue(report.Violations.Any(v => v.StartsWith("origins_adjacent_in_reachable (I3): 0:0 is reachable from 2:0", StringComparison.Ordinal)),
            string.Join("\n", report.Violations));
    }

    [TestMethod]
    public void Check_ValidListWithSplitRun_NoViolation()
    {
        // "aXb": client 1 typed "ab" as one run, client 2 inserted X between, which splits it.
        var report = ItemListInvariants.Check(
            [Run(1, 0, 1, null, null), Run(2, 0, 1, (1, 0), (1, 1)), Run(1, 1, 1, (1, 0), null)], full: true);
        Assert.AreEqual(0, report.Violations.Count, string.Join("\n", report.Violations));
    }

    [TestMethod]
    public void Check_SameOriginWrongClientOrder_ReportsSorted()
    {
        // X (client 5) and Y (client 3) both after a with right origin last: Y must come first.
        var report = ItemListInvariants.Check(
            [Run(1, 0, 1, null, null), Run(5, 0, 1, (1, 0), null), Run(3, 0, 1, (1, 0), null)], full: false);
        Assert.IsTrue(report.Violations.Any(v => v.StartsWith("sorted: 5:0", StringComparison.Ordinal)), string.Join("\n", report.Violations));
    }

    [TestMethod]
    public void Check_OriginToTheRight_ReportsOriginLtSelf()
    {
        var report = ItemListInvariants.Check([Run(2, 0, 1, (1, 0), null), Run(1, 0, 1, null, null)], full: false);
        Assert.IsTrue(report.Violations.Any(v => v.StartsWith("origin_lt_self", StringComparison.Ordinal)), string.Join("\n", report.Violations));
    }

    [TestMethod]
    public void Check_DuplicateId_ReportsUniqueId()
    {
        var report = ItemListInvariants.Check([Run(1, 0, 2, null, null), Run(1, 1, 1, (1, 0), null)], full: false);
        Assert.IsTrue(report.Violations.Any(v => v.StartsWith("uniqueId", StringComparison.Ordinal)), string.Join("\n", report.Violations));
    }

    [TestMethod]
    public void Check_OriginNotInList_ReportsClosedLeft()
    {
        var report = ItemListInvariants.Check([Run(2, 0, 1, (1, 0), null)], full: false);
        Assert.IsTrue(report.Violations.Any(v => v.StartsWith("closedLeft", StringComparison.Ordinal)), string.Join("\n", report.Violations));
    }

    [TestMethod]
    public void Check_RightOriginAndOriginPointAtEachOther_ReportsNotInductive()
    {
        // x's right origin is y and y's origin is x: positions are fine, the history is not.
        var report = ItemListInvariants.Check([Run(1, 0, 1, null, (2, 0)), Run(2, 0, 1, (1, 0), null)], full: false);
        Assert.IsTrue(report.Violations.Any(v => v.StartsWith("YjsItem is inductive", StringComparison.Ordinal)), string.Join("\n", report.Violations));
    }

    [TestMethod]
    public void Check_ElementMovedSincePreviousCheck_ReportsInsertIdx()
    {
        // Two independent items at the start (no origins); swapping them is a move, not an insert.
        var before = ItemListInvariants.Check([Run(1, 0, 1, null, null), Run(2, 0, 1, null, null)], full: false);
        var after = ItemListInvariants.Check([Run(2, 0, 1, null, null), Run(1, 0, 1, null, null)], full: false, before);
        Assert.IsTrue(after.Violations.Any(v => v.StartsWith("insert_ok_exists_insertIdx: 2:0 moved", StringComparison.Ordinal)), string.Join("\n", after.Violations));
    }

    [TestMethod]
    public void Check_TombstoneRevived_ReportsDeleteById()
    {
        var before = ItemListInvariants.Check([Run(1, 0, 1, null, null, deleted: true)], full: false);
        var after = ItemListInvariants.Check([Run(1, 0, 1, null, null)], full: false, before);
        Assert.IsTrue(after.Violations.Any(v => v.StartsWith("deleteById", StringComparison.Ordinal)), string.Join("\n", after.Violations));
    }

    private static ItemRun Run(long client, long clock, int length, (long, long)? origin, (long, long)? right, bool deleted = false) =>
        new(client, clock, length, origin, right, deleted, ItemRun.ItemKind, LinkedBack: true, InParent: true);
}
