using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs.Corpus;

/// <summary>
/// Ycs against itself: N docs edit a text at random and exchange their updates in random order,
/// with duplicates; every doc must end with the same text. Reproduce a failure with its seed.
/// </summary>
[TestClass]
public class ConvergenceTests
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz 😀é";

    /// <summary>Seeds 1..40, or 1..YCS_CONVERGENCE_SEEDS for a longer soak.</summary>
    public static IEnumerable<object[]> Seeds =>
        Enumerable.Range(1, int.TryParse(Environment.GetEnvironmentVariable("YCS_CONVERGENCE_SEEDS"), out var n) ? n : 40)
            .Select(s => new object[] { s });

    [TestMethod]
    [DynamicData(nameof(Seeds))]
    public void RandomEdits_RandomDeliveryWithDuplicates_AllDocsConverge(int seed)
    {
        var (log, texts) = Simulate(seed);
        Assert.AreEqual(1, texts.Distinct().Count(),
            $"Docs diverged (seed {seed}, {texts.Count} docs, {log.Count} updates):\n{string.Join("\n", texts.Select((t, i) => $"doc {i}: {t}"))}");
    }

    /// <summary>Runs one seeded session; returns every update made (in order) and each doc's final text.</summary>
    internal static (List<byte[]> Log, List<string> Texts) Simulate(int seed)
    {
        var rng = new Random(seed);
        var n = 2 + rng.Next(4);
        var docs = Enumerable.Range(0, n).Select(i => CorpusDoc.Create(clientId: 1 + (i * 7919) + rng.Next(1000))).ToList();
        var log = new List<byte[]>();
        foreach (var doc in docs)
        {
            doc.LocalUpdateV1 += log.Add;

            // Realize the root before anything arrives, as an editor binding does; realizing it
            // while updates are pending loses them (DivergenceTests.ApplyV1_RootRealizedWhilePending_*).
            _ = doc.TextLength("text");
        }

        // inbox[i]: indices into log that doc i has not received yet.
        var inbox = docs.Select(_ => new List<int>()).ToList();
        for (var step = 0; step < 300; step++)
        {
            var i = rng.Next(n);
            if (rng.Next(4) == 0)
            {
                Deliver(rng, docs[i], inbox[i], log, all: false);
                continue;
            }

            var before = log.Count;
            Edit(rng, docs[i]);
            for (var k = before; k < log.Count; k++)
            {
                for (var j = 0; j < n; j++)
                {
                    if (j != i)
                    {
                        inbox[j].Add(k);
                    }
                }
            }
        }

        for (var i = 0; i < n; i++)
        {
            Deliver(rng, docs[i], inbox[i], log, all: true);
        }

        return (log, docs.Select(d => d.Text("text")).ToList());
    }

    private static void Edit(Random rng, CorpusDoc doc)
    {
        var length = doc.TextLength("text");
        if (length > 0 && rng.Next(3) == 0)
        {
            var at = rng.Next(length);
            doc.DeleteText("text", at, 1 + rng.Next(Math.Min(6, length - at)));
        }
        else
        {
            var word = string.Concat(Enumerable.Range(0, 1 + rng.Next(4)).Select(_ => PickTextElement(rng)));
            doc.InsertText("text", rng.Next(length + 1), word);
        }
    }

    private static string PickTextElement(Random rng)
    {
        var i = rng.Next(Alphabet.Length);
        return char.IsSurrogate(Alphabet[i]) ? "😀" : Alphabet[i].ToString();
    }

    /// <summary>Delivers some (or all) pending updates in random order, re-sending some already delivered ones.</summary>
    private static void Deliver(Random rng, CorpusDoc doc, List<int> pending, List<byte[]> log, bool all)
    {
        var take = all ? pending.Count : rng.Next(pending.Count + 1);
        var batch = pending.OrderBy(_ => rng.Next()).Take(take).ToList();
        foreach (var k in batch)
        {
            pending.Remove(k);
            doc.ApplyV1(log[k]);
            if (rng.Next(5) == 0)
            {
                doc.ApplyV1(log[rng.Next(log.Count)]);
            }
        }
    }
}
