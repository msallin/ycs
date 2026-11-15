using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs.Properties;

/// <summary>A recorded script and what real Yjs observed when it ran it.</summary>
/// <summary>
/// A recorded script and what real Yjs observed when it ran it: in full for a pinned case, compacted
/// (<see cref="Differential.Compact"/>) otherwise. <see cref="Known"/> is the known failure that
/// explained Ycs' difference when it was recorded, if any.
/// </summary>
internal sealed record Fixture(string Kind, string Name, Dictionary<string, object> Script, string Yjs, string Known);

/// <summary>interop/properties/fixtures/*.jsonl, one fixture per line, recorded by <see cref="RecordedTests.RecordFixtures"/>.</summary>
internal static class Fixtures
{
    public static readonly string[] Files = ["pairs", "random", "windows", "fuzz", "pinned", "fixed", "laws"];

    private static readonly Lazy<Dictionary<string, Fixture>> LazyAll = new(() =>
    {
        var all = new Dictionary<string, Fixture>(StringComparer.Ordinal);
        foreach (var file in Files)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "fixtures", file + ".jsonl");
            if (!File.Exists(path))
            {
                continue;
            }

            foreach (var line in File.ReadLines(path).Where(l => l.Length > 0))
            {
                var o = (Dictionary<string, object>)Json.Parse(line);
                var f = new Fixture(o.Str("kind"), o.Str("name"), (Dictionary<string, object>)o["script"], Json.Canonical(o["yjs"]), o.Str("known"));
                all[f.Kind + "/" + f.Name] = f;
            }
        }

        return all;
    });

    public static IReadOnlyDictionary<string, Fixture> All => LazyAll.Value;

    public static IEnumerable<object[]> Of(params string[] kinds) =>
        All.Values.Where(f => kinds.Contains(f.Kind)).Select(f => new object[] { f.Kind + "/" + f.Name });
}

/// <summary>
/// Replays the recorded scripts on Ycs (no Node needed) and compares every step with what Yjs
/// observed. A mismatch that a known failure explains is Inconclusive with its name; any other fails.
/// </summary>
[TestClass]
public sealed class RecordedTests
{
    public TestContext TestContext { get; set; }

    public static IEnumerable<object[]> Recorded => Fixtures.Of("pair", "random", "windows", "fuzz", "fixed", "law");

    [TestMethod]
    [DynamicData(nameof(Recorded))]
    public void Recorded_ObservationsEqualYjs(string name)
    {
        var f = Fixtures.All[name];
        var mismatch = Differential.Compare(f.Yjs, Differential.Compact(Differential.RunYcs(f.Script)), f.Script, normalize: false);
        if (mismatch == null)
        {
            return;
        }

        // Recorded with Release: a Debug-only failure is recognized now.
        var knownName = f.Known ?? KnownFailures.All.FirstOrDefault(k => k.DebugOnly && Debugger.IsDebugBuild && k.Matches(mismatch, f.Script))?.Name;
        if (knownName != null)
        {
            var known = KnownFailures.Named(knownName);
            Assert.Inconclusive($"KNOWN FAILURE {known.Name}: {known.Reason}{Environment.NewLine}{mismatch.Describe()}");
        }

        Assert.Fail($"{mismatch.Describe()}(he/hs: the step's events/states differ; YCS_PROP_EXPLAIN shows how){Environment.NewLine}script: {Json.Canonical(f.Script)}");
    }

    /// <summary>Every fixed property violation's script: the property holds.</summary>
    [TestMethod]
    public void FixedProperties_Hold()
    {
        var violations = FixedFailures.All.Where(f => f.Property != null)
            .Select(f => (f.Name, Violation: Properties.Named(f.Property).Check((Dictionary<string, object>)Json.Parse(f.Script))))
            .Where(x => x.Violation != null)
            .Select(x => $"{x.Name}: {x.Violation.Detail}")
            .ToList();
        Assert.IsEmpty(violations, string.Join(Environment.NewLine, violations));
    }

    /// <summary>Every operation of the language, every delivery format and every pair of kinds is in the recorded scripts.</summary>
    [TestMethod]
    public void Fixtures_CoverEveryOperationAndPair()
    {
        string[] ops =
        [
            "insert", "delete", "format", "embed", "delta", "setAttr", "rmAttr", "ains", "push", "unshift", "xafter", "set",
            "mdel", "clear", "transact", "um", "undo", "redo", "stop", "umclear", "snap", "readSnap", "docFromSnap", "rpos",
            "rres", "observe", "react", "deliver", "sync", "merged",
        ];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var vias = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in Fixtures.All.Values)
        {
            foreach (Dictionary<string, object> step in f.Script.List("steps"))
            {
                seen.Add(step.Str("op"));
                foreach (var inner in step.List("ops")?.Cast<Dictionary<string, object>>() ?? [])
                {
                    seen.Add(inner.Str("op"));
                }

                if (step.Str("via") is { } via)
                {
                    vias.Add(step.Str("op") + ":" + via + (step.Bool("diff") ? ":diff" : ""));
                }
            }
        }

        var pairs = Fixtures.All.Values.Count(f => f.Kind == "pair");
        Assert.IsEmpty(ops.Except(seen).ToList(), "operations no recorded script uses: " + string.Join(", ", ops.Except(seen)));
        Assert.IsEmpty(new[] { "deliver:v1", "deliver:v2", "deliver:v1to2", "deliver:v2to1", "sync:v1", "sync:v2", "merged:v1", "merged:v2", "merged:v1:diff", "merged:v2:diff" }.Except(vias).ToList(), "delivery formats no recorded script uses");
        Assert.AreEqual(OpKinds.Paired.Length * (OpKinds.Paired.Length + 1) / 2, pairs, "pairwise fixtures");
    }

    /// <summary>
    /// YCS_PROP_RECORD=1: regenerates interop/properties/fixtures/*.jsonl from the generator and real
    /// Yjs (Node, interop/properties: npm ci). Deterministic: the same files every time.
    /// </summary>
    [TestMethod]
    [Timeout(int.MaxValue)]
    public void RecordFixtures()
    {
        if (Environment.GetEnvironmentVariable("YCS_PROP_RECORD") is not "1")
        {
            Assert.Inconclusive("Set YCS_PROP_RECORD=1 to regenerate the fixtures (needs Node and interop/properties: npm ci).");
        }

        var dir = Path.Combine(Oracle.Directory, "fixtures");
        Directory.CreateDirectory(dir);
        using var oracle = new Oracle();

        var unexplained = new List<string>();
        string Line(string kind, string name, Dictionary<string, object> script)
        {
            var yjs = oracle.Run(Json.Canonical(script));
            Assert.IsFalse(yjs.StartsWith("{\"fatal\"", StringComparison.Ordinal), $"{name}: {yjs}");
            string known = null;
            if (kind != "pinned" && Differential.Compare(yjs, Differential.RunYcs(script), script) is { } mismatch)
            {
                known = KnownFailures.Explain(mismatch, script)?.Name;
                if (known == null)
                {
                    unexplained.Add($"{kind}/{name}: {mismatch.Signature}{Environment.NewLine}{mismatch.Describe()}");
                }
            }

            var recorded = kind == "pinned" ? yjs : Differential.Compact(yjs);
            return Json.Canonical(Json.Obj(("kind", kind), ("name", name), ("script", script), ("yjs", Json.Parse(recorded)), ("known", known)));
        }

        var pairs = new List<string>();
        for (var i = 0; i < OpKinds.Paired.Length; i++)
        {
            for (var j = i; j < OpKinds.Paired.Length; j++)
            {
                var (a, b) = (OpKinds.Paired[i], OpKinds.Paired[j]);
                pairs.Add(Line("pair", $"{a}+{b}", Generator.GeneratePair((ulong)(10_000 + (i * 100) + j), a, b, compact: true)));
            }
        }

        var random = new List<string>();
        for (ulong seed = 1; seed <= 150; seed++)
        {
            var options = SoakTests.OptionsFor(seed) with { Compact = true, MaxSteps = 24 };
            random.Add(Line("random", seed.ToString(CultureInfo.InvariantCulture), Generator.Generate(seed, options)));
        }

        // Where a run extended in place could differ from Yjs' items (GenOptions.Windows), and long lists typed on with
        // updates applied in the middle of transactions.
        var windows = new List<string>();
        for (ulong seed = 1; seed <= 150; seed++)
        {
            windows.Add(Line("windows", seed.ToString(CultureInfo.InvariantCulture), Generator.Generate(50_000 + seed, new GenOptions { Windows = true, Compact = true, MaxSteps = 30 })));
        }

        for (ulong seed = 1; seed <= 20; seed++)
        {
            var script = ListScripts.Generate((2 * seed) - 1, windows: true);
            script["compact"] = true;
            windows.Add(Line("windows", "lists-" + seed.ToString(CultureInfo.InvariantCulture), script));
        }

        var fuzz = new List<string>();
        for (ulong seed = 1; seed <= 80; seed++)
        {
            fuzz.Add(Line("fuzz", seed.ToString(CultureInfo.InvariantCulture), UpdateFuzzer.Generate(seed, sortedDeleteSets: true).Script));
        }

        var pinned = KnownFailures.All.Where(k => k.Property == null).Select(k => Line("pinned", k.Name, (Dictionary<string, object>)Json.Parse(k.Script))).ToList();
        var fixedScripts = FixedFailures.All.Where(f => f.Property == null).Select(f => Line("fixed", f.Name, (Dictionary<string, object>)Json.Parse(f.Script))).ToList();
        var laws = YjsLaws.All.Select(l => Line("law", l.Name, (Dictionary<string, object>)Json.Parse(l.Script))).ToList();

        File.WriteAllLines(Path.Combine(dir, "pairs.jsonl"), pairs);
        File.WriteAllLines(Path.Combine(dir, "random.jsonl"), random);
        File.WriteAllLines(Path.Combine(dir, "windows.jsonl"), windows);
        File.WriteAllLines(Path.Combine(dir, "fuzz.jsonl"), fuzz);
        File.WriteAllLines(Path.Combine(dir, "pinned.jsonl"), pinned);
        File.WriteAllLines(Path.Combine(dir, "fixed.jsonl"), fixedScripts);
        File.WriteAllLines(Path.Combine(dir, "laws.jsonl"), laws);
        TestContext.WriteLine($"{pairs.Count} pairs, {random.Count} random, {windows.Count} windows, {fuzz.Count} fuzz, {pinned.Count} pinned, {fixedScripts.Count} fixed, {laws.Count} law fixtures in {dir}");
        Assert.IsEmpty(unexplained, "mismatches no known failure explains; pin them (KnownFailures) and record again:" + Environment.NewLine + string.Join(Environment.NewLine, unexplained));
    }
}

/// <summary>Whether this test project (and so the library) was built in Debug, where Debug.Assert is live.</summary>
internal static class Debugger
{
#if DEBUG
    public const bool IsDebugBuild = true;
#else
    public const bool IsDebugBuild = false;
#endif
}

/// <summary>Each known failure, replayed: Inconclusive while it still differs from Yjs, failing once it agrees.</summary>
[TestClass]
public sealed class PinnedTests
{
    public static IEnumerable<object[]> Pinned => KnownFailures.All.Select(k => new object[] { k.Name });

    [TestMethod]
    [DynamicData(nameof(Pinned))]
    public void Pinned_StillDiffersFromYjs(string name)
    {
        var known = KnownFailures.Named(name);
        if (known.DebugOnly && !Debugger.IsDebugBuild)
        {
            Assert.Inconclusive($"{name} fails in Debug builds only.");
        }

        if (known.Property != null)
        {
            // The entry's Property is a prefix of the violation's signature ("undo-" covers both undo properties); the
            // pinned script is checked with the last property it names.
            var property = Properties.All.Last(p => p.Name.StartsWith(known.Property.Split(':')[0], StringComparison.Ordinal));
            var violation = property.Check((Dictionary<string, object>)Json.Parse(known.Script));
            KnownFailures.Check(known, () => Assert.IsNull(violation, violation?.Detail));
            return;
        }

        Assert.IsTrue(Fixtures.All.TryGetValue("pinned/" + name, out var f), $"{name} has no recorded Yjs observations; run RecordFixtures.");
        Assert.AreEqual(Json.Canonical(Json.Parse(known.Script)), Json.Canonical(f.Script), $"{name}: the script changed; run RecordFixtures.");

        // Every other entry's normalization applies: this entry must be what still differs.
        var mismatch = Differential.Compare(f.Yjs, Differential.RunYcs(f.Script), f.Script, normalizeExcept: known);
        KnownFailures.Check(known, () => Assert.IsNull(mismatch, mismatch?.Describe()));
    }
}
