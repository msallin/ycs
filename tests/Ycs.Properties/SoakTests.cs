using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs.Properties;

/// <summary>
/// The live soak (interop/properties/soak.sh): fresh random scripts by seed, run by Yjs (Node) and
/// by Ycs, compared after every step; a mismatch is shrunk to a minimal script, written to
/// interop/properties/failures/ and printed. Off unless YCS_PROP_SOAK is set (the number of
/// scripts); YCS_PROP_SEED is the first seed (default 1), YCS_PROP_WORKERS the number of Node
/// processes (default: the processor count).
/// </summary>
[TestClass]
public sealed class SoakTests
{
    public TestContext TestContext { get; set; }

    [TestMethod]
    [Timeout(int.MaxValue)]
    public void LiveSoak_ScriptsAgreeWithYjs()
    {
        var count = Environment.GetEnvironmentVariable("YCS_PROP_SOAK");
        if (string.IsNullOrEmpty(count))
        {
            Assert.Inconclusive("Set YCS_PROP_SOAK=<number of scripts> (and run interop/properties: npm ci) for the live soak; see docs/testing.md.");
        }

        var total = long.Parse(count, CultureInfo.InvariantCulture);
        var first = ulong.Parse(Environment.GetEnvironmentVariable("YCS_PROP_SEED") is { Length: > 0 } s ? s : "1", CultureInfo.InvariantCulture);
        var workers = int.Parse(Environment.GetEnvironmentVariable("YCS_PROP_WORKERS") is { Length: > 0 } w ? w : Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        var report = Soak(first, total, workers, line => TestContext.WriteLine(line));
        TestContext.WriteLine(report.Summary);
        var unknown = report.Failures.Where(f => f.Known == null).ToList();
        Assert.IsEmpty(unknown, $"{unknown.Count} distinct mismatch(es) not in KnownFailures:{Environment.NewLine}{report.Summary}");
    }

    /// <summary>YCS_PROP_EXPLAIN=&lt;script file&gt;: prints the script and how Yjs' and Ycs' observations of it differ.</summary>
    [TestMethod]
    public void Explain_PrintsBothObservations()
    {
        var file = Environment.GetEnvironmentVariable("YCS_PROP_EXPLAIN");
        if (string.IsNullOrEmpty(file))
        {
            Assert.Inconclusive("Set YCS_PROP_EXPLAIN=<script.json> to compare one script.");
        }

        var script = (Dictionary<string, object>)Json.Parse(File.ReadAllText(file));
        using var oracle = new Oracle();
        var yjs = oracle.Run(Json.Canonical(script));
        var ycs = Differential.RunYcs(script);
        TestContext.WriteLine(Json.Canonical(script));
        var raw = Differential.Compare(yjs, ycs, script, normalize: false);
        var normalized = Differential.Compare(yjs, ycs, script);
        TestContext.WriteLine(raw == null ? "identical" : "raw: " + raw.Describe());
        TestContext.WriteLine(normalized == null ? "identical after normalization" : "normalized: " + normalized.Describe() + "\nYJS " + normalized.Yjs + "\nYCS " + normalized.Ycs);
        TestContext.WriteLine("known: " + (normalized == null ? "-" : KnownFailures.Explain(normalized, script)?.Name ?? "NONE"));
        TestContext.WriteLine("every difference (normalized):");
        foreach (var line in Differential.AllDifferences(yjs, ycs))
        {
            TestContext.WriteLine("  " + line);
        }
    }

    internal sealed record Failure(string Signature, ulong Seed, string File, string Description, int Count, string Known);

    internal sealed record Report(long Scripts, IReadOnlyList<Failure> Failures, string Summary);

    /// <summary>The soak itself; also used by the fixture recorder's self-check.</summary>
    internal static Report Soak(ulong firstSeed, long total, int workers, Action<string> log)
    {
        var failures = new ConcurrentDictionary<string, (ulong Seed, Dictionary<string, object> Script, Mismatch Mismatch, int Count)>();
        var knownCounts = new ConcurrentDictionary<string, int>();
        var next = (long)firstSeed - 1;
        var done = 0L;
        var (steps, extending) = (Interlocked.Read(ref ScriptRun.ExecutedSteps), Interlocked.Read(ref ScriptRun.ExecutedStepsExtending));
        var (scripts, scriptsExtending) = (Interlocked.Read(ref ScriptRun.ExecutedScripts), Interlocked.Read(ref ScriptRun.ExecutedScriptsExtending));
        var stopwatch = Stopwatch.StartNew();
        var last = Stopwatch.StartNew();
        var threads = Enumerable.Range(0, workers).Select(_ => new Thread(() =>
        {
            using var oracle = new Oracle();
            while (true)
            {
                var seed = (ulong)Interlocked.Increment(ref next);
                if (seed >= firstSeed + (ulong)total)
                {
                    return;
                }

                var script = Generator.Generate(seed, OptionsFor(seed));
                var json = Json.Canonical(script);
                var mismatch = Differential.Compare(oracle.Run(json), Differential.RunYcs(script), script);
                if (mismatch != null && KnownFailures.Explain(mismatch, script) is { } explained)
                {
                    knownCounts.AddOrUpdate(explained.Name, 1, (_, n) => n + 1);
                }
                else if (mismatch != null)
                {
                    var known = failures.TryGetValue(mismatch.Signature, out var existing);
                    if (!known)
                    {
                        var minimal = Shrink(oracle, script, mismatch);
                        var minimalMismatch = Differential.Compare(oracle.Run(Json.Canonical(minimal)), Differential.RunYcs(minimal), minimal) ?? mismatch;
                        failures.AddOrUpdate(mismatch.Signature, (seed, minimal, minimalMismatch, 1), (_, e) => (e.Seed, e.Script, e.Mismatch, e.Count + 1));
                        lock (log)
                        {
                            log($"MISMATCH {mismatch.Signature} at seed {seed}; minimal:{Environment.NewLine}{Json.Canonical(minimal)}{Environment.NewLine}{minimalMismatch.Describe()}");
                        }
                    }
                    else
                    {
                        failures.AddOrUpdate(mismatch.Signature, existing, (_, e) => (e.Seed, e.Script, e.Mismatch, e.Count + 1));
                    }
                }

                var d = Interlocked.Increment(ref done);
                if (last.Elapsed.TotalSeconds > 60)
                {
                    lock (log)
                    {
                        if (last.Elapsed.TotalSeconds > 60)
                        {
                            last.Restart();
                            log($"{d} scripts, {failures.Count} distinct mismatches, {stopwatch.Elapsed:hh\\:mm\\:ss}");
                        }
                    }
                }
            }
        })
        { IsBackground = true }).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        var dir = Path.Combine(Oracle.Directory, "failures");
        Directory.CreateDirectory(dir);
        var list = new List<Failure>();
        var summary = new StringBuilder();
        summary.AppendLine(CultureInfo.InvariantCulture, $"{done} scripts (seeds {firstSeed}..{firstSeed + (ulong)total - 1}), {failures.Count} distinct mismatches, {stopwatch.Elapsed:hh\\:mm\\:ss}");
        steps = Interlocked.Read(ref ScriptRun.ExecutedSteps) - steps;
        extending = Interlocked.Read(ref ScriptRun.ExecutedStepsExtending) - extending;
        scripts = Interlocked.Read(ref ScriptRun.ExecutedScripts) - scripts;
        scriptsExtending = Interlocked.Read(ref ScriptRun.ExecutedScriptsExtending) - scriptsExtending;
        summary.AppendLine(CultureInfo.InvariantCulture, $"  {extending} of {steps} steps ({100.0 * extending / Math.Max(1, steps):0.0}%) and {scriptsExtending} of {scripts} scripts ({100.0 * scriptsExtending / Math.Max(1, scripts):0.0}%) extended a run in place (shrinking included)");
        foreach (var (signature, (seed, script, mismatch, n)) in failures.OrderBy(f => Array.IndexOf(Differential.Severity, f.Value.Mismatch.Primary)))
        {
            var file = Path.Combine(dir, $"{Slug(signature)}.json");
            File.WriteAllText(file, Json.Canonical(script) + "\n");
            var known = KnownFailures.Explain(mismatch, script)?.Name;
            list.Add(new Failure(signature, seed, file, mismatch.Describe(), n, known));
            summary.AppendLine(CultureInfo.InvariantCulture, $"  {n,7}x {signature}{(known != null ? $" (known: {known})" : "")}, first at seed {seed}, minimal script {file}");
            summary.AppendLine("           " + mismatch.Describe().Replace("\n", "\n           ", StringComparison.Ordinal).TrimEnd());
        }

        foreach (var (name, n) in knownCounts.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            summary.AppendLine(CultureInfo.InvariantCulture, $"  {n,7}x known: {name}");
        }

        return new Report(done, list, summary.ToString());
    }

    /// <summary>The operation mix by seed: most scripts use everything; some concentrate on one area.</summary>
    // Every script has quiet clients (GenOptions.Windows), where Ycs extends runs.
    internal static GenOptions OptionsFor(ulong seed) => (seed % 10) switch
    {
        0 => new GenOptions { Windows = true, Kinds = [OpKinds.TextInsert, OpKinds.TextDelete, OpKinds.TextFormat, OpKinds.TextEmbed, OpKinds.TextDelta, OpKinds.TextAttr, OpKinds.Typing, OpKinds.Typing, OpKinds.Typing], MaxSteps = 50 },
        1 => new GenOptions { Windows = true, Kinds = [OpKinds.ArrayInsert, OpKinds.ArrayDelete, OpKinds.MapSet, OpKinds.MapDelete, OpKinds.Nested, OpKinds.Transact, OpKinds.Typing, OpKinds.Typing, OpKinds.React] },
        2 => new GenOptions { Windows = true, Kinds = [OpKinds.XmlInsert, OpKinds.XmlDelete, OpKinds.XmlAttr, OpKinds.XmlText, OpKinds.Observe, OpKinds.Typing] },
        3 => new GenOptions { Windows = true, Kinds = [OpKinds.Undo, OpKinds.TextInsert, OpKinds.TextDelete, OpKinds.TextFormat, OpKinds.ArrayInsert, OpKinds.ArrayDelete, OpKinds.MapSet, OpKinds.Transact, OpKinds.Typing], MaxSteps = 50 },
        4 => new GenOptions { Windows = true, Kinds = [OpKinds.Snapshot, OpKinds.RelPos, OpKinds.TextInsert, OpKinds.TextDelete, OpKinds.ArrayInsert, OpKinds.ArrayDelete, OpKinds.XmlInsert, OpKinds.Typing] },
        5 => new GenOptions { Windows = true, NetworkShare = 0.6, MaxSteps = 60 },
        9 => new GenOptions { Windows = true, Subdocs = true },
        _ => new GenOptions { Windows = true },
    };

    internal static Dictionary<string, object> Shrink(Oracle oracle, Dictionary<string, object> script, Mismatch original) =>
        Shrinker.Shrink(script, candidate =>
        {
            var m = Differential.Compare(oracle.Run(Json.Canonical(candidate)), Differential.RunYcs(candidate), candidate);
            return m != null && m.Signature == original.Signature;
        });

    private static string Slug(string signature) =>
        new string(signature.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Replace("---", "-", StringComparison.Ordinal);
}
