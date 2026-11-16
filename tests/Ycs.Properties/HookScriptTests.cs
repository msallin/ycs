using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs.Properties;

/// <summary>
/// Scripts whose handlers on the document (<c>afterTransactionCleanup</c>, <c>update</c>, <c>afterTransaction</c>)
/// and on types make nested transactions, which split and merge structs while the cleanup that called them still has
/// merging to do (<see cref="GenOptions.Hooks"/>), against real Yjs. The recorded ones are in
/// <c>fixtures/hooks.jsonl</c> (<see cref="RecordedTests"/>); this is the same on fresh seeds.
/// </summary>
[TestClass]
public sealed class HookScriptTests
{
    public TestContext TestContext { get; set; }

    /// <summary>YCS_PROP_HOOKS=&lt;scripts&gt; (default 300) from seed 90,001 (YCS_PROP_SEED); needs Node (interop/properties: npm ci).</summary>
    [TestMethod]
    [Timeout(int.MaxValue)]
    public void HookScripts_FreshSeeds_AgreeWithYjs()
    {
        if (!Oracle.Available)
        {
            Assert.Inconclusive("Run npm ci in interop/properties for the scripts against Yjs.");
        }

        var count = int.Parse(Environment.GetEnvironmentVariable("YCS_PROP_HOOKS") is { Length: > 0 } n ? n : "300", CultureInfo.InvariantCulture);
        var first = ulong.Parse(Environment.GetEnvironmentVariable("YCS_PROP_SEED") is { Length: > 0 } s ? s : "90001", CultureInfo.InvariantCulture);
        using var oracle = new Oracle();
        var unexplained = new List<string>();
        for (var seed = first; seed < first + (ulong)count; seed++)
        {
            var script = Generator.Generate(seed, new GenOptions { Windows = true, Hooks = true, MaxSteps = 30 });
            var mismatch = Differential.Compare(oracle.Run(Json.Canonical(script)), Differential.RunYcs(script), script);
            if (mismatch != null && KnownFailures.Explain(mismatch, script) == null)
            {
                var minimal = SoakTests.Shrink(oracle, script, mismatch);
                unexplained.Add($"seed {seed}: {mismatch.Signature}{Environment.NewLine}{mismatch.Describe()}{Environment.NewLine}minimal: {Json.Canonical(minimal)}");
            }
        }

        Assert.IsEmpty(unexplained, string.Join(Environment.NewLine, unexplained));
    }
}
