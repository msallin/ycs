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

    /// <summary>
    /// The same scripts with page 0 of the slab at one slot, so that page 0 grows by copy at every allocation: a
    /// <c>ref Slot</c> held across an <c>Allocate</c> would then point into the old page and the update bytes differ
    /// from Yjs. (Freed slots are reused last-in first-out in every run, this setting or not.) YCS_PROP_HOOKS and
    /// YCS_PROP_SEED as above, from seed 190,001.
    /// </summary>
    [TestMethod]
    [Timeout(int.MaxValue)]
    public void HookScripts_WithConstantSlotReuse_AgreeWithYjs()
    {
        if (!Oracle.Available)
        {
            Assert.Inconclusive("Run npm ci in interop/properties for the scripts against Yjs.");
        }

        var count = int.Parse(Environment.GetEnvironmentVariable("YCS_PROP_HOOKS") is { Length: > 0 } n ? n : "300", CultureInfo.InvariantCulture);
        var first = ulong.Parse(Environment.GetEnvironmentVariable("YCS_PROP_SEED") is { Length: > 0 } s ? s : "190001", CultureInfo.InvariantCulture);
        var before = TestHooks.SlabFirstPageSlots;
        TestHooks.SlabFirstPageSlots = 1;
        try
        {
            using var oracle = new Oracle();
            var unexplained = new List<string>();
            for (var seed = first; seed < first + (ulong)count; seed++)
            {
                var script = Generator.Generate(seed, new GenOptions { Windows = true, Hooks = true, MaxSteps = 30 });
                var mismatch = Differential.Compare(oracle.Run(Json.Canonical(script)), Differential.RunYcs(script), script);
                if (mismatch != null && KnownFailures.Explain(mismatch, script) == null)
                {
                    unexplained.Add($"seed {seed}: {mismatch.Signature}{Environment.NewLine}{mismatch.Describe()}{Environment.NewLine}minimal: {Json.Canonical(script)}");
                }
            }

            Assert.IsEmpty(unexplained, string.Join(Environment.NewLine, unexplained));
        }
        finally
        {
            TestHooks.SlabFirstPageSlots = before;
        }
    }
}
