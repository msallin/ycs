using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs.Corpus;

/// <summary>
/// Replays every case recorded from real Yjs (interop/corpus/cases) through Ycs. "State" below
/// means the content of every root (text, delta, JSON), the state vector and the delete set.
/// </summary>
[TestClass]
public class CorpusTests
{
    private const int Shuffles = 5;

    public static IEnumerable<object[]> Cases => CorpusCase.Names;

    [TestMethod]
    public void Corpus_IsPresent_HasCases() => Assert.IsTrue(CorpusCase.All.Count >= 20, $"Corpus not found in {CorpusCase.Directory}.");

    [DataTestMethod]
    [DynamicData(nameof(Cases))]
    public void ApplyV1_RecordedOrder_StateMatchesYjs(string name) => Run(name, c =>
        AssertState(c, Build(c, c.Updates, v2: false), "applying the v1 updates in the recorded order"));

    [DataTestMethod]
    [DynamicData(nameof(Cases))]
    public void ApplyV2_RecordedOrder_StateMatchesYjs(string name) => Run(name, c =>
        AssertState(c, Build(c, c.Updates, v2: true), "applying the v2 updates in the recorded order"));

    [DataTestMethod]
    [DynamicData(nameof(Cases))]
    public void ApplyV1_ShuffledWithDuplicates_StateMatchesYjs(string name) => Run(name, c => Shuffled(c, v2: false));

    [DataTestMethod]
    [DynamicData(nameof(Cases))]
    public void ApplyV2_ShuffledWithDuplicates_StateMatchesYjs(string name) => Run(name, c => Shuffled(c, v2: true));

    [DataTestMethod]
    [DynamicData(nameof(Cases))]
    public void ApplyYjsStateV1_FreshDoc_StateMatchesYjs(string name) => Run(name, c =>
        AssertState(c, Build(c, [c.ExpectedState], v2: false), "applying Yjs' encodeStateAsUpdate"));

    [DataTestMethod]
    [DynamicData(nameof(Cases))]
    public void ApplyYjsStateV2_FreshDoc_StateMatchesYjs(string name) => Run(name, c =>
        AssertState(c, Build(c, [c.ExpectedState], v2: true), "applying Yjs' encodeStateAsUpdateV2"));

    [DataTestMethod]
    [DynamicData(nameof(Cases))]
    public void ReEncodeStateV1_FreshDoc_StateMatchesYjs(string name) => Run(name, c =>
    {
        var copy = CorpusDoc.Create(c.Gc);
        copy.ApplyV1(Build(c, c.Updates, v2: false).EncodeStateV1());
        AssertState(c, copy, "re-encoding with Ycs (v1) into a fresh doc");
    });

    [DataTestMethod]
    [DynamicData(nameof(Cases))]
    public void ReEncodeStateV2_FreshDoc_StateMatchesYjs(string name) => Run(name, c =>
    {
        var copy = CorpusDoc.Create(c.Gc);
        copy.ApplyV2(Build(c, c.Updates, v2: true).EncodeStateV2());
        AssertState(c, copy, "re-encoding with Ycs (v2) into a fresh doc");
    });

    [DataTestMethod]
    [DynamicData(nameof(Cases))]
    public void EncodeStateV1_AfterUpdates_BytesEqualYjs(string name) => Run(name, c =>
        AssertBytes(c.ExpectedState.V1, Build(c, c.Updates, v2: false).EncodeStateV1(), "encodeStateAsUpdate"));

    [DataTestMethod]
    [DynamicData(nameof(Cases))]
    public void EncodeStateV2_AfterUpdates_BytesEqualYjs(string name) => Run(name, c =>
        AssertBytes(c.ExpectedState.V2, Build(c, c.Updates, v2: true).EncodeStateV2(), "encodeStateAsUpdateV2"));

    [DataTestMethod]
    [DynamicData(nameof(Cases))]
    public void EncodeStateVector_AfterUpdates_BytesEqualYjs(string name) => Run(name, c =>
        AssertBytes(c.ExpectedStateVectorBytes, Build(c, c.Updates, v2: false).EncodeStateVector(), "encodeStateVector"));

    [DataTestMethod]
    [DynamicData(nameof(Cases))]
    public void EncodeStateVector_OfPrefix_EqualsYjs(string name) => Run(name, c =>
        Assert.AreEqual(Observed.Clocks(c.DiffStateVectorMap), Observed.StateVector(Prefix(c, v2: false).EncodeStateVector()),
            $"state vector after the first {c.DiffPrefix} updates"));

    [DataTestMethod]
    [DynamicData(nameof(Cases))]
    public void EncodeStateV1_ForStateVector_CompletesPrefixDoc(string name) => Run(name, c =>
    {
        var prefix = Prefix(c, v2: false);
        prefix.ApplyV1(Build(c, c.Updates, v2: false).EncodeStateV1(c.DiffStateVector));
        AssertState(c, prefix, $"the first {c.DiffPrefix} updates plus Ycs' diff for their state vector");
    });

    [DataTestMethod]
    [DynamicData(nameof(Cases))]
    public void EncodeStateV2_ForStateVector_CompletesPrefixDoc(string name) => Run(name, c =>
    {
        var prefix = Prefix(c, v2: true);
        prefix.ApplyV2(Build(c, c.Updates, v2: true).EncodeStateV2(c.DiffStateVector));
        AssertState(c, prefix, $"the first {c.DiffPrefix} updates plus Ycs' v2 diff for their state vector");
    });

    [DataTestMethod]
    [DynamicData(nameof(Cases))]
    public void ApplyYjsDiff_ToPrefixDoc_StateMatchesYjs(string name) => Run(name, c =>
    {
        var prefix = Prefix(c, v2: false);
        prefix.ApplyV1(c.Diff.V1);
        AssertState(c, prefix, $"the first {c.DiffPrefix} updates plus Yjs' diff for their state vector");
    });

    [DataTestMethod]
    [DynamicData(nameof(Cases))]
    public void EncodeStateV1_ForStateVector_CoversWhatYjsDiffCovers(string name) => Run(name, c =>
        Assert.AreEqual(DiffSummary(c.Diff.V1, false), DiffSummary(Build(c, c.Updates, v2: false).EncodeStateV1(c.DiffStateVector), false),
            "client ranges and delete set of the diff for the prefix state vector"));

    [DataTestMethod]
    [DynamicData(nameof(Cases))]
    public void DiffUpdate_OfYjsState_CoversWhatYjsDiffCovers(string name) => Run(name, c =>
        Assert.AreEqual(DiffSummary(c.Diff.V1, false), DiffSummary(CorpusUpdates.Diff(c.ExpectedState.V1, c.DiffStateVector, v2: false), false),
            "UpdateOperations.DiffUpdate(Yjs state, prefix state vector) against Yjs' diff"));

    [DataTestMethod]
    [DynamicData(nameof(Cases))]
    public void DiffUpdateV2_OfYjsState_CoversWhatYjsDiffCovers(string name) => Run(name, c =>
        Assert.AreEqual(DiffSummary(c.Diff.V2, true), DiffSummary(CorpusUpdates.Diff(c.ExpectedState.V2, c.DiffStateVector, v2: true), true),
            "UpdateOperations.DiffUpdateV2(Yjs state, prefix state vector) against Yjs' diff"));

    [DataTestMethod]
    [DynamicData(nameof(Cases))]
    public void MergeUpdatesV1_RecordedUpdates_BytesEqualYjs(string name) => Run(name, c =>
        AssertBytes(c.Merged.V1, CorpusUpdates.Merge(c.Updates.Select(u => u.V1).ToList(), v2: false), "mergeUpdates"));

    [DataTestMethod]
    [DynamicData(nameof(Cases))]
    public void MergeUpdatesV2_RecordedUpdates_BytesEqualYjs(string name) => Run(name, c =>
        AssertBytes(c.Merged.V2, CorpusUpdates.Merge(c.Updates.Select(u => u.V2).ToList(), v2: true), "mergeUpdatesV2"));

    [DataTestMethod]
    [DynamicData(nameof(Cases))]
    public void MergeUpdatesV1_Applied_StateMatchesYjs(string name) => Run(name, c =>
    {
        var doc = CorpusDoc.Create(c.Gc);
        doc.ApplyV1(CorpusUpdates.Merge(c.Updates.Select(u => u.V1).ToList(), v2: false));
        AssertState(c, doc, "applying Ycs' mergeUpdates of the recorded updates");
    });

    [DataTestMethod]
    [DynamicData(nameof(Cases))]
    public void ApplyYjsMergedV1_FreshDoc_StateMatchesYjs(string name) => Run(name, c =>
        AssertState(c, Build(c, [c.Merged], v2: false), "applying Yjs' mergeUpdates of the recorded updates"));

    // ------------------------------------------------------------------ helpers

    private static void Run(string name, Action<CorpusCase> test, [CallerMemberName] string testName = "") =>
        KnownFailures.Check(name, testName, () => test(CorpusCase.Named(name)));

    internal static CorpusDoc Build(CorpusCase c, IEnumerable<Update> updates, bool v2)
    {
        var doc = CorpusDoc.Create(c.Gc);
        foreach (var u in updates)
        {
            if (v2)
            {
                doc.ApplyV2(u.V2);
            }
            else
            {
                doc.ApplyV1(u.V1);
            }
        }

        return doc;
    }

    private static CorpusDoc Prefix(CorpusCase c, bool v2) => Build(c, c.Updates.Take(c.DiffPrefix), v2);

    private static void Shuffled(CorpusCase c, bool v2)
    {
        for (var seed = 1; seed <= Shuffles; seed++)
        {
            var rng = new Random(seed);
            var order = c.Updates.OrderBy(_ => rng.Next()).ToList();
            var withDuplicates = new List<Update>();
            foreach (var u in order)
            {
                withDuplicates.Add(u);
                if (rng.Next(3) == 0)
                {
                    withDuplicates.Add(order[rng.Next(order.Count)]);
                }
            }

            AssertState(c, Build(c, withDuplicates, v2), $"applying the {(v2 ? "v2" : "v1")} updates shuffled with duplicates (System.Random seed {seed})");
        }
    }

    internal static string State(CorpusDoc doc, CorpusCase c) =>
        $"content: {Observed.Content(doc, c.Roots)}\nstate vector: {Observed.StateVector(doc.EncodeStateVector())}\ndelete set: {Observed.DeleteSet(CorpusUpdates.DeleteSet(doc.EncodeStateV1(), v2: false))}";

    internal static string ExpectedState(CorpusCase c) =>
        $"content: {Observed.Expected(c)}\nstate vector: {Observed.Clocks(c.ExpectedStateVector)}\ndelete set: {Observed.DeleteSet(c.ExpectedDeleteSet)}";

    private static void AssertState(CorpusCase c, CorpusDoc doc, string after) =>
        Assert.AreEqual(ExpectedState(c), State(doc, c), $"State differs from Yjs after {after}.");

    private static string DiffSummary(byte[] update, bool v2)
    {
        var (from, to) = CorpusUpdates.Meta(update, v2);
        return $"from: {Observed.Clocks(from)}\nto: {Observed.Clocks(to)}\ndelete set: {Observed.DeleteSet(CorpusUpdates.DeleteSet(update, v2))}";
    }

    private static void AssertBytes(byte[] expected, byte[] actual, string what)
    {
        if (expected.AsSpan().SequenceEqual(actual))
        {
            return;
        }

        var at = 0;
        while (at < Math.Min(expected.Length, actual.Length) && expected[at] == actual[at])
        {
            at++;
        }

        Assert.Fail($"{what}: {actual.Length} bytes from Ycs, {expected.Length} from Yjs; first difference at byte {at}." +
            $"\nYjs:  {Convert.ToBase64String(expected)}\nYcs:  {Convert.ToBase64String(actual)}");
    }
}
