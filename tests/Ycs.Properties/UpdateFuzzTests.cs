using System.Globalization;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs.Properties;

/// <summary>
/// The structure-aware update fuzzer (UpdateFuzz.cs): valid updates built from a model, applied as
/// raw bytes to two receivers in two orders. Without Node, the property rejects-cleanly (Properties.cs,
/// run by PropertyTests): every rejection is a YUpdateException that leaves the document as it was. The
/// receivers need not converge: the origins are arbitrary (an origin right of its right origin, say), which
/// YATA's convergence assumes away, and Yjs' receivers end apart on such updates too. With Node (YCS_PROP_FUZZ=&lt;count&gt;):
/// the same scripts are compared with Yjs step by step. A Ycs rejection of what Yjs accepts is a bug
/// unless docs/api.md documents the limit.
/// </summary>
[TestClass]
public sealed class UpdateFuzzTests
{
    public TestContext TestContext { get; set; }

    private static (ulong First, int Count) Seeds(int fallback) =>
        (ulong.Parse(Environment.GetEnvironmentVariable("YCS_PROP_SEED") is { Length: > 0 } s ? s : "1", CultureInfo.InvariantCulture),
         int.Parse(Environment.GetEnvironmentVariable("YCS_PROP_FUZZ") is { Length: > 0 } n ? n : fallback.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));

    /// <summary>YCS_PROP_FUZZ=&lt;count&gt; (and Node): every fuzzed script compared with Yjs; mismatches shrunk and listed.</summary>
    [TestMethod]
    [Timeout(int.MaxValue)]
    public void StructuredUpdates_AgreeWithYjs()
    {
        if (Environment.GetEnvironmentVariable("YCS_PROP_FUZZ") is not { Length: > 0 } || !Oracle.Available)
        {
            Assert.Inconclusive("Set YCS_PROP_FUZZ=<count> (and run interop/properties: npm ci) to compare fuzzed updates with Yjs.");
        }

        var (first, count) = Seeds(0);
        using var oracle = new Oracle();
        var found = new Dictionary<string, (ulong Seed, Dictionary<string, object> Minimal, Mismatch Mismatch, int Count)>(StringComparer.Ordinal);
        var known = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var seed = first; seed < first + (ulong)count; seed++)
        {
            var (script, _) = UpdateFuzzer.Generate(seed, sortedDeleteSets: true);
            var mismatch = Differential.Compare(oracle.Run(Json.Canonical(script)), Differential.RunYcs(script), script);
            if (mismatch == null)
            {
                continue;
            }

            if (KnownFailures.Explain(mismatch, script) is { } k)
            {
                known[k.Name] = known.GetValueOrDefault(k.Name) + 1;
                continue;
            }

            if (found.TryGetValue(mismatch.Signature, out var f))
            {
                if (f.Count < 0 && KnownFailures.Explain(f.Mismatch, f.Minimal) is { } k2)
                {
                    known[k2.Name] = known.GetValueOrDefault(k2.Name) + 1;
                }
                else
                {
                    found[mismatch.Signature] = f with { Count = f.Count + 1 };
                }

                continue;
            }

            var minimal = SoakTests.Shrink(oracle, script, mismatch);
            var minimalMismatch = Differential.Compare(oracle.Run(Json.Canonical(minimal)), Differential.RunYcs(minimal), minimal) ?? mismatch;
            // The minimal script shows the cause best: a known failure there is known.
            if (KnownFailures.Explain(minimalMismatch, minimal) is { } shrunk)
            {
                known[shrunk.Name] = known.GetValueOrDefault(shrunk.Name) + 1;
                found[mismatch.Signature] = (seed, minimal, minimalMismatch, -1);
                continue;
            }

            found[mismatch.Signature] = (seed, minimal, minimalMismatch, 1);
        }

        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"{count} fuzzed scripts from seed {first}: {found.Count(f => f.Value.Count > 0)} distinct unknown mismatch(es)");
        foreach (var (name, n) in known)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"  {n}x known: {name}");
        }

        foreach (var (signature, (seed, minimal, m, n)) in found.Where(f => f.Value.Count > 0))
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"  {n}x {signature}, first at seed {seed}");
            report.AppendLine("    " + m.Describe().Replace("\n", "\n    ", StringComparison.Ordinal).TrimEnd());
            report.AppendLine("    minimal: " + Json.Canonical(minimal));
        }

        TestContext.WriteLine(report.ToString());
        Assert.IsEmpty(found.Where(f => f.Value.Count > 0).ToList(), report.ToString());
    }

    /// <summary>YCS_PROP_FUZZ_DUMP=&lt;seed&gt;: writes that seed's fuzz script (unsorted delete sets) to the scratch file YCS_PROP_FUZZ_OUT.</summary>
    [TestMethod]
    public void DumpFuzzScript()
    {
        var seed = Environment.GetEnvironmentVariable("YCS_PROP_FUZZ_DUMP");
        if (string.IsNullOrEmpty(seed))
        {
            Assert.Inconclusive("Set YCS_PROP_FUZZ_DUMP=<seed> and YCS_PROP_FUZZ_OUT=<file>.");
        }

        File.WriteAllText(Environment.GetEnvironmentVariable("YCS_PROP_FUZZ_OUT")!, Json.Canonical(UpdateFuzzer.Generate(ulong.Parse(seed, CultureInfo.InvariantCulture)).Script));
    }

    /// <summary>Nesting up to the documented limit (256) applies; beyond it, the update is rejected cleanly.</summary>
    [TestMethod]
    public void DeepNesting_AppliesUpToTheLimit_RejectsBeyond()
    {
        foreach (var (depth, accepted) in new[] { (250, true), (300, false) })
        {
            var update = UpdateFuzzer.DeepNesting(depth);
            var script = UpdateFuzzer.ToScript([update], 1);
            var run = new ScriptRun(script);
            var observation = run.Step((Dictionary<string, object>)script.List("steps")[0]);
            Assert.IsEmpty(run.RejectionViolations, $"depth {depth}: {string.Join("; ", run.RejectionViolations)}");
            Assert.AreEqual(accepted, observation.Get("err") is not true, $"depth {depth}");
        }
    }
}
