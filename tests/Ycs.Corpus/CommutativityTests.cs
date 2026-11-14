using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs.Corpus;

/// <summary>
/// Brute force over delivery orders, Ycs against itself: small seeded scenarios of concurrent
/// single-character inserts from three clients (with some syncs between them, so items have
/// origins in each other's inserts) are applied to fresh docs in every order, and every doc must
/// end with the same text and the same item order. This is what the Lean development proves for
/// its integrate: `insert_commutative` (LeanYjs/Algorithm/Commutativity/InsertInsert.lean) for
/// two concurrent inserts, `YjsOperationNetwork_converge'` (Network/Yjs/YjsNetwork.lean) for
/// any two hb-consistent delivery orders of the same set.
/// </summary>
[TestClass]
public class CommutativityTests
{
    /// <summary>Every permutation (causal or not, so the pending path too) of 7 inserts: 5040 orders per seed.</summary>
    public static IEnumerable<object[]> AllOrderSeeds => Enumerable.Range(1, 20).Select(s => new object[] { s });

    /// <summary>Every causal order of 3 clients x 4 inserts: up to 12! / 4!^3 = 34650 orders per seed.</summary>
    public static IEnumerable<object[]> CausalOrderSeeds => Enumerable.Range(1, 5).Select(s => new object[] { s });

    [DataTestMethod]
    [DynamicData(nameof(AllOrderSeeds))]
    public void ApplyV1_EveryPermutationOfSevenInserts_SameTextAndItemOrder(int seed)
    {
        var s = Scenario.Create(seed, insertsPerClient: [3, 2, 2]);
        var orders = Permutations.Of(s.Updates.Count);
        AssertSameOutcome(s, orders, $"seed {seed}, every permutation");
    }

    [DataTestMethod]
    [DynamicData(nameof(CausalOrderSeeds))]
    public void ApplyV1_EveryCausalOrderOfTwelveInserts_SameTextAndItemOrder(int seed)
    {
        var s = Scenario.Create(1000 + seed, insertsPerClient: [4, 4, 4]);
        AssertSameOutcome(s, s.CausalOrders(), $"seed {1000 + seed}, every causal order");
    }

    private static void AssertSameOutcome(Scenario s, IEnumerable<int[]> orders, string what)
    {
        string first = null;
        var count = 0;
        foreach (var order in orders)
        {
            var doc = CorpusDoc.Create();
            var check = new Invariants(doc, Invariants.Text);
            doc.ApplyV1(s.Base);
            foreach (var k in order)
            {
                doc.ApplyV1(s.Updates[k]);
            }

            var label = $"{what}, order [{string.Join(", ", order)}]; {s}";
            check.Assert(full: true, $"after {label}");
            var outcome = $"text: {doc.Text("text")}\nitems: {ItemListInvariants.Runs(check.Last["text"])}";
            first ??= outcome;
            Assert.AreEqual(first, outcome, $"Delivery order changed the result ({label}).");
            count++;
        }

        Assert.IsTrue(count > 1, $"{what}: only {count} order.");
        Console.WriteLine($"{what}: {count} delivery orders, all giving\n{first}\n{s}");
    }

    /// <summary>
    /// Client 1 types "ab" (the base, delivered first everywhere); then clients with random
    /// distinct ids insert one character at a time at random positions, each insert one update,
    /// and now and then a client first receives everything another client has (so later inserts
    /// have origins in other clients' inserts, building chains).
    /// </summary>
    private sealed class Scenario
    {
        public byte[] Base { get; private init; }
        public List<byte[]> Updates { get; } = [];
        private readonly List<HashSet<int>> _deps = [];
        private readonly List<string> _script = [];

        public override string ToString() => $"script: {string.Join("; ", _script)}";

        public static Scenario Create(int seed, int[] insertsPerClient)
        {
            var rng = new Random(seed);
            var ids = Enumerable.Range(2, 8).OrderBy(_ => rng.Next()).Take(insertsPerClient.Length).ToArray();
            var origin = CorpusDoc.Create(clientId: 1);
            byte[] baseUpdate = null;
            origin.LocalUpdateV1 += u => baseUpdate = u;
            origin.InsertText("text", 0, "ab");

            var s = new Scenario { Base = baseUpdate };
            var docs = ids.Select(id => CorpusDoc.Create(clientId: id)).ToList();
            var known = ids.Select(_ => new HashSet<int>()).ToList();
            var made = new byte[ids.Length][];
            for (var c = 0; c < docs.Count; c++)
            {
                var client = c;
                docs[c].LocalUpdateV1 += u => made[client] = u;
                docs[c].ApplyV1(baseUpdate);
            }

            var left = (int[])insertsPerClient.Clone();
            while (left.Any(n => n > 0))
            {
                var i = rng.Next(ids.Length);
                if (left[i] == 0)
                {
                    continue;
                }

                if (rng.Next(4) == 0)
                {
                    var j = (i + 1 + rng.Next(ids.Length - 1)) % ids.Length;
                    foreach (var k in known[j].Except(known[i]).Order())
                    {
                        docs[i].ApplyV1(s.Updates[k]);
                        known[i].Add(k);
                    }

                    s._script.Add($"{ids[i]} gets {ids[j]}'s");
                }

                var at = rng.Next(docs[i].TextLength("text") + 1);
                var ch = (char)('A' + s.Updates.Count);
                docs[i].InsertText("text", at, ch.ToString());
                s._deps.Add([.. known[i]]);
                known[i].Add(s.Updates.Count);
                s.Updates.Add(made[i]);
                s._script.Add($"{ids[i]} inserts {ch} at {at}");
                left[i]--;
            }

            return s;
        }

        /// <summary>Every order in which each update comes after every update its author had when making it.</summary>
        public IEnumerable<int[]> CausalOrders()
        {
            var n = Updates.Count;
            var order = new int[n];
            var used = new bool[n];
            return Extend(0);

            IEnumerable<int[]> Extend(int depth)
            {
                if (depth == n)
                {
                    yield return (int[])order.Clone();
                    yield break;
                }

                for (var k = 0; k < n; k++)
                {
                    if (used[k] || _deps[k].Any(d => !used[d]))
                    {
                        continue;
                    }

                    used[k] = true;
                    order[depth] = k;
                    foreach (var o in Extend(depth + 1))
                    {
                        yield return o;
                    }

                    used[k] = false;
                }
            }
        }
    }
}
