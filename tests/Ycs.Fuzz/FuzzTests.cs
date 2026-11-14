using System.Globalization;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs.Fuzz;

/// <summary>
/// Applies damaged updates and checks that Ycs fails cleanly. Deterministic by default; set
/// YCS_FUZZ_ITERATIONS (and YCS_FUZZ_SEED, a number or "random") for a soak.
/// </summary>
[TestClass]
public sealed class FuzzTests
{
    private const int DefaultIterations = 20_000;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Seeds_UnmutatedUpdates_Apply()
    {
        using var harness = new Harness();
        var failures = Seeds.All
            .Select(seed => (seed, outcome: harness.Run(FuzzInput.From(seed, seed.Update))))
            .Where(r => r.outcome.Verdict != Verdict.Applied)
            .Select(r => $"{r.seed}: {r.outcome.Signature} {r.outcome.Detail}")
            .ToList();

        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }

    [TestMethod]
    public void MutatedUpdates_FailCleanly()
    {
        var iterations = Environment.GetEnvironmentVariable("YCS_FUZZ_ITERATIONS") is { Length: > 0 } n
            ? int.Parse(n, CultureInfo.InvariantCulture)
            : DefaultIterations;
        var seedSetting = Environment.GetEnvironmentVariable("YCS_FUZZ_SEED");
        var randomSeed = seedSetting is null or "" ? 1
            : seedSetting == "random" ? Environment.TickCount
            : int.Parse(seedSetting, CultureInfo.InvariantCulture);
        TestContext.WriteLine($"YCS_FUZZ_SEED={randomSeed} YCS_FUZZ_ITERATIONS={iterations}, {Seeds.All.Count} seeds");

        var random = new Random(randomSeed);
        var seeds = Seeds.All;
        var known = KnownFailures.All.Select(k => k.Signature).ToHashSet(StringComparer.Ordinal);
        var verdicts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var rejections = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var firstBySignature = new Dictionary<string, (int Iteration, Seed Seed, FuzzInput Input, Outcome Outcome)>(StringComparer.Ordinal);

        using var harness = new Harness();
        for (var i = 0; i < iterations; i++)
        {
            var seed = seeds[random.Next(seeds.Count)];
            var input = FuzzInput.From(seed, Mutator.Mutate(seed.Update, random));
            var outcome = harness.Run(input);
            Count(verdicts, outcome.Verdict.ToString());
            if (outcome.ExceptionType is not null)
            {
                Count(rejections, outcome.ExceptionType);
            }

            if (outcome.IsViolation)
            {
                Count(verdicts, outcome.Signature);
                firstBySignature.TryAdd(outcome.Signature, (i, seed, input, outcome));
            }
        }

        var report = new StringBuilder();
        report.AppendLine("verdicts:");
        foreach (var (verdict, count) in verdicts)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"  {count,8}  {verdict}");
        }

        report.AppendLine("exception types:");
        foreach (var (type, count) in rejections)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"  {count,8}  {type}{(Harness.AllowedExceptionTypes.Contains(type) ? "" : "  (not allowed)")}");
        }

        var unknown = firstBySignature.Where(kvp => !known.Contains(kvp.Key)).OrderBy(kvp => kvp.Key, StringComparer.Ordinal).ToList();
        foreach (var (signature, (iteration, seed, input, outcome)) in unknown)
        {
            var minimal = Minimizer.Minimize(harness, input, signature, signature == "Hang" ? 20 : 2000);
            report.AppendLine(CultureInfo.InvariantCulture, $"NEW VIOLATION {signature} ({outcome.Detail}) at iteration {iteration}, seed {seed}");
            report.AppendLine(CultureInfo.InvariantCulture, $"  minimal: {KnownFailures.Describe(minimal)}");
        }

        TestContext.WriteLine(report.ToString());
        Assert.IsEmpty(unknown, $"{unknown.Count} violation(s) not in KnownFailures; pin each one there with its minimal input:{Environment.NewLine}{report}");
    }

    public static IEnumerable<object[]> Pinned => KnownFailures.All.Select(k => new object[] { k.Name });

    [TestMethod]
    [DynamicData(nameof(Pinned))]
    public void PinnedCase_FailsCleanly(string name)
    {
        var pinned = KnownFailures.All.Single(k => k.Name == name);
        using var harness = new Harness();
        var outcome = harness.Run(pinned.Input);

        if (outcome.Signature == pinned.Signature)
        {
            Assert.Inconclusive($"KNOWN FAILURE {name}: {pinned.Reason} ({outcome.Signature}; {outcome.Detail})");
        }

        Assert.IsFalse(outcome.IsViolation, $"{name} now fails differently: {outcome.Signature} ({outcome.Detail})");
        Assert.Fail($"{name} is listed in KnownFailures but now fails cleanly ({outcome.Verdict}); remove the entry.");
    }

    private static void Count(SortedDictionary<string, int> counts, string key) =>
        counts[key] = counts.GetValueOrDefault(key) + 1;
}
