using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs.Corpus;

/// <summary>
/// The Lean list invariant (<see cref="ItemListInvariants"/>) on the recorded corpus, and the item
/// order Ycs builds against the one Yjs built (recorded by gen.mjs as expected.itemOrder).
/// </summary>
[TestClass]
public class ItemListInvariantsTests
{
    private const int Shuffles = 5;
    private const int MaxPermutedUpdates = 8;

    public static IEnumerable<object[]> Cases => CorpusCase.Names;

    /// <summary>The adversarial-* cases: few updates, so every delivery order is affordable.</summary>
    public static IEnumerable<object[]> AdversarialCases =>
        CorpusCase.All.Where(c => c.Name.StartsWith("adversarial-", StringComparison.Ordinal)).Select(c => new object[] { c.Name });

    [TestMethod]
    public void Corpus_AdversarialCases_ArePresent() =>
        Assert.IsTrue(AdversarialCases.Count() >= 40, "interop/corpus/gen.mjs records the adversarial-* cases; regenerate the corpus.");

    [TestMethod]
    [DynamicData(nameof(Cases))]
    public void ApplyV1_EveryRecordedUpdate_LeanListInvariantHolds(string name) => Run(name, c =>
    {
        var (doc, check) = Fresh(c);
        for (var k = 0; k < c.Updates.Count; k++)
        {
            doc.ApplyV1(c.Updates[k].V1);
            check.Assert(full: false, $"after recorded update {k} of {c.Name}");
        }

        check.Assert(full: true, $"after all recorded updates of {c.Name}");
    });

    [TestMethod]
    [DynamicData(nameof(Cases))]
    public void ApplyV1_ShuffledWithDuplicates_LeanListInvariantHolds(string name) => Run(name, c =>
    {
        for (var seed = 1; seed <= Shuffles; seed++)
        {
            var rng = new Random(seed);
            var (doc, check) = Fresh(c);
            foreach (var u in c.Updates.OrderBy(_ => rng.Next()).ToList())
            {
                doc.ApplyV1(u.V1);
                check.Assert(full: false, $"in {c.Name} shuffled with System.Random seed {seed}");
                if (rng.Next(3) == 0)
                {
                    doc.ApplyV1(c.Updates[rng.Next(c.Updates.Count)].V1);
                    check.Assert(full: false, $"after a duplicate in {c.Name} shuffled with System.Random seed {seed}");
                }
            }

            check.Assert(full: true, $"at the end of {c.Name} shuffled with System.Random seed {seed}");
        }
    });

    [TestMethod]
    [DynamicData(nameof(Cases))]
    public void ApplyV1_RecordedOrder_ItemOrderEqualsYjs(string name) => Run(name, c =>
    {
        var doc = CorpusTests.Build(c, c.Updates, v2: false);
        foreach (var (root, expected) in c.ExpectedItemOrder)
        {
            var type = c.Roots.Single(r => r.Name == root).Type;
            Assert.AreEqual(expected, Order(doc, root, type), $"item order of '{root}' (client:clock+length, d = deleted)");
        }
    });

    /// <summary>
    /// Every delivery order of an adversarial case, causal or not, must give the text and the item
    /// order Yjs gave for the recorded one (Lean: `YjsOperationNetwork_converge'`; for the causal
    /// orders, `insert_commutative` step by step), with the list invariant intact.
    /// </summary>
    [TestMethod]
    [DynamicData(nameof(AdversarialCases))]
    public void ApplyV1_EveryPermutation_TextAndItemOrderEqualYjs(string name) => Run(name, c =>
    {
        Assert.IsTrue(c.Updates.Count <= MaxPermutedUpdates, $"{c.Updates.Count} updates: too many to permute.");
        var text = c.ExpectedContent.GetProperty("text").GetProperty("text").GetString();
        var order = c.ExpectedItemOrder["text"];
        foreach (var perm in Permutations.Of(c.Updates.Count))
        {
            var (doc, check) = Fresh(c);
            foreach (var k in perm)
            {
                doc.ApplyV1(c.Updates[k].V1);
            }

            var label = $"delivery order [{string.Join(", ", perm)}] of {c.Name}";
            check.Assert(full: true, $"after {label}");
            Assert.AreEqual(text, doc.Text("text"), $"text after {label}");
            Assert.AreEqual(order, Order(doc, "text", "text"), $"item order after {label}");
        }
    });

    private static (CorpusDoc Doc, Invariants Check) Fresh(CorpusCase c)
    {
        var doc = CorpusDoc.Create(c.Gc);
        var check = new Invariants(doc, c.Roots.Select(r => (r.Name, r.Type)).ToList());

        // The first check realizes the roots before anything arrives, as an editor binding does;
        // realizing one while updates are pending loses them (KnownFailures.StaleRootParent).
        check.Assert(full: true, "on the empty doc");
        return (doc, check);
    }

    private static string Order(CorpusDoc doc, string root, string type) =>
        ItemListInvariants.Runs(ItemListInvariants.Check(doc.Items(root, type), full: false));

    private static void Run(string name, Action<CorpusCase> test, [CallerMemberName] string testName = "") =>
        KnownFailures.Check(name, testName, () => test(CorpusCase.Named(name)));
}

/// <summary>Every permutation of 0..n-1 (Heap's algorithm, iterative), each as a fresh array.</summary>
internal static class Permutations
{
    public static IEnumerable<int[]> Of(int n)
    {
        var a = Enumerable.Range(0, n).ToArray();
        var c = new int[n];
        yield return (int[])a.Clone();
        var i = 0;
        while (i < n)
        {
            if (c[i] < i)
            {
                var j = i % 2 == 0 ? 0 : c[i];
                (a[j], a[i]) = (a[i], a[j]);
                yield return (int[])a.Clone();
                c[i]++;
                i = 0;
            }
            else
            {
                c[i] = 0;
                i++;
            }
        }
    }
}
