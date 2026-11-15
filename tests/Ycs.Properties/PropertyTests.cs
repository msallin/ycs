using System.Globalization;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs.Properties;

/// <summary>
/// Checks every property of <see cref="Properties"/> on generated scripts (no Node). Deterministic by
/// default (YCS_PROP_SEEDS scripts per property, default 2,000 (about 45 s for all of them), from YCS_PROP_SEED, default 1); a
/// violation is shrunk to a minimal script and fails the test unless a known failure explains it.
/// </summary>
[TestClass]
public sealed class PropertyTests
{
    public TestContext TestContext { get; set; }

    public static IEnumerable<object[]> Names => Properties.All.Select(p => new object[] { p.Name });

    [TestMethod]
    [DynamicData(nameof(Names))]
    [Timeout(int.MaxValue)]
    public void Property_HoldsOnGeneratedScripts(string name)
    {
        var spec = Properties.Named(name);
        var count = int.Parse(Environment.GetEnvironmentVariable("YCS_PROP_SEEDS") is { Length: > 0 } n ? n : "2000", CultureInfo.InvariantCulture);
        var first = ulong.Parse(Environment.GetEnvironmentVariable("YCS_PROP_SEED") is { Length: > 0 } s ? s : "1", CultureInfo.InvariantCulture);
        var found = new Dictionary<string, (ulong Seed, Violation Violation, Dictionary<string, object> Minimal, int Count)>(StringComparer.Ordinal);
        for (var seed = first; seed < first + (ulong)count; seed++)
        {
            var script = spec.Generate(seed);
            var violation = spec.Check(script);
            if (violation == null)
            {
                continue;
            }

            if (found.TryGetValue(violation.Signature, out var existing))
            {
                found[violation.Signature] = existing with { Count = existing.Count + 1 };
                continue;
            }

            var minimal = Shrinker.Shrink(script, candidate => spec.Check(candidate)?.Signature == violation.Signature, budget: 1500);
            found[violation.Signature] = (seed, spec.Check(minimal) ?? violation, minimal, 1);
        }

        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"{name}: {count} scripts from seed {first}, {found.Count} distinct violation(s)");
        foreach (var (signature, (seed, violation, minimal, hits)) in found)
        {
            var known = KnownFailures.Explain(violation);
            report.AppendLine(CultureInfo.InvariantCulture, $"  {hits}x {signature}{(known != null ? $" (known: {known.Name})" : "")}, first at seed {seed}");
            report.AppendLine("    " + violation.Detail.Replace("\n", "\n    ", StringComparison.Ordinal));
            report.AppendLine("    minimal: " + Json.Canonical(minimal));
        }

        TestContext.WriteLine(report.ToString());
        var unknown = found.Values.Where(f => KnownFailures.Explain(f.Violation) == null).ToList();
        Assert.IsEmpty(unknown, report.ToString());
        if (found.Count > 0)
        {
            Assert.Inconclusive("KNOWN FAILURE(S): " + report);
        }
    }

    /// <summary>YCS_PROP_ONE=&lt;script file&gt; YCS_PROP_ONE_PROPERTY=&lt;name&gt;: checks one property on one script and prints the violation.</summary>
    [TestMethod]
    public void Property_CheckOne()
    {
        var file = Environment.GetEnvironmentVariable("YCS_PROP_ONE");
        if (string.IsNullOrEmpty(file))
        {
            Assert.Inconclusive("Set YCS_PROP_ONE=<script.json> and YCS_PROP_ONE_PROPERTY=<property>.");
        }

        var spec = Properties.Named(Environment.GetEnvironmentVariable("YCS_PROP_ONE_PROPERTY"));
        var violation = spec.Check((Dictionary<string, object>)Json.Parse(File.ReadAllText(file)));
        TestContext.WriteLine(violation == null ? "holds" : $"{violation.Signature}: {violation.Detail}");
    }

    /// <summary>The event-replay property is right: it holds for real Yjs (needs Node; Inconclusive without).</summary>
    [TestMethod]
    public void EventReplay_HoldsForYjs()
    {
        if (!Oracle.Available)
        {
            Assert.Inconclusive("needs interop/properties: npm ci");
        }

        using var oracle = new Oracle();
        var spec = Properties.Named("event-replay");
        for (ulong seed = 1; seed <= 150; seed++)
        {
            var script = spec.Generate(seed);
            var observations = ((List<object>)Json.Parse(oracle.Run(Json.Canonical(script)))).Cast<Dictionary<string, object>>().ToList();
            var violation = Properties.EventReplayOn(script, observations);
            Assert.IsNull(violation, $"seed {seed}: {violation?.Detail}{Environment.NewLine}{Json.Canonical(script)}");
        }
    }
}
