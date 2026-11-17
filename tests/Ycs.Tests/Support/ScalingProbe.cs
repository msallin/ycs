using System;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Asserts a growth rate instead of a wall-clock bound: the time at four times the size against the time at the base size,
/// measured in the same test on the same machine, so a loaded CI slows both alike. Linear work gives about 4, quadratic about 16.
/// </summary>
internal static class ScalingProbe
{
    public const double DefaultMaxRatio = 8;

    /// <summary>
    /// <paramref name="prepare"/> builds the scenario of a size outside the clock and returns the part to time. One warm-up,
    /// then the best of five at each size, up to three attempts.
    /// </summary>
    public static void AssertNotQuadratic(Func<int, Action> prepare, int size, TestContext context, double maxRatio = DefaultMaxRatio)
    {
        prepare(size / 4)();

        // Load on the machine inflates one measurement now and then; quadratic work exceeds the limit in every attempt.
        var worst = 0.0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var small = Best(prepare, size / 4);
            var large = Best(prepare, size);

            // A floor keeps a base time near the clock's resolution from inflating the ratio.
            var ratio = large.TotalMilliseconds / Math.Max(small.TotalMilliseconds, 1.0);
            context.WriteLine($"size {size / 4}: {small.TotalMilliseconds:F1} ms, size {size}: {large.TotalMilliseconds:F1} ms, ratio {ratio:F1} (limit {maxRatio})");
            if (ratio <= maxRatio)
            {
                return;
            }

            worst = Math.Max(worst, ratio);
        }

        Assert.Fail($"ratio {worst:F1} at {size / 4} to {size} in three attempts (limit {maxRatio})");
    }

    private static TimeSpan Best(Func<int, Action> prepare, int size)
    {
        var best = TimeSpan.MaxValue;
        for (var run = 0; run < 5; run++)
        {
            var timed = prepare(size);

            // The heap the earlier tests left would be collected inside the clock otherwise.
            System.GC.Collect();
            var stopwatch = Stopwatch.StartNew();
            timed();
            stopwatch.Stop();
            best = stopwatch.Elapsed < best ? stopwatch.Elapsed : best;
        }

        return best;
    }
}
