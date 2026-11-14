using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs.Budgets;

/// <summary>
/// Measures each scenario's allocated bytes and time per operation and compares them with
/// <see cref="Budgets"/>. Allocations are counted on the test thread
/// (GC.GetAllocatedBytesForCurrentThread), after a warm-up, averaged over the scenario's iterations.
/// </summary>
[TestClass]
public sealed class BudgetTests
{
    private const double Headroom = 1.15;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Scenarios_StayWithinBudgets()
    {
#if DEBUG
        Assert.Inconclusive("The budgets are measured with -c Release; run dotnet test -c Release.");
#endif
        var table = new StringBuilder();
        table.AppendLine(CultureInfo.InvariantCulture, $"{"scenario",-42} {"bytes/op",10} {"budget",10} {"µs/op",9} {"ceiling",9}  note");
        var failures = new List<string>();
        foreach (var scenario in Scenarios.All)
        {
            var (bytes, microseconds) = Measure(scenario);
            var note = "";
            if (!Budgets.All.TryGetValue(scenario.Name, out var budget))
            {
                failures.Add($"{scenario.Name}: no budget; add it to Budgets.cs");
                note = $"no budget; suggest {Suggest(bytes)}";
            }
            else
            {
                if (bytes > budget.BytesPerOperation)
                {
                    failures.Add($"{scenario.Name}: {bytes} bytes/op > budget {budget.BytesPerOperation}");
                    note = "OVER BUDGET";
                }
                else if (Suggest(bytes) < budget.BytesPerOperation / Headroom)
                {
                    note = $"improved: lower the budget to {Suggest(bytes)}";
                }

                if (microseconds > budget.MicrosecondsPerOperation)
                {
                    failures.Add($"{scenario.Name}: {microseconds:F1} µs/op > ceiling {budget.MicrosecondsPerOperation}");
                    note += " TOO SLOW";
                }
            }

            table.AppendLine(CultureInfo.InvariantCulture,
                $"{scenario.Name,-42} {bytes,10} {budget?.BytesPerOperation.ToString(CultureInfo.InvariantCulture) ?? "-",10} {microseconds,9:F1} {budget?.MicrosecondsPerOperation.ToString(CultureInfo.InvariantCulture) ?? "-",9}  {note}");
        }

        foreach (var name in Budgets.All.Keys.Except(Scenarios.All.Select(s => s.Name)))
        {
            failures.Add($"{name}: budget without a scenario; remove it from Budgets.cs");
        }

        TestContext.WriteLine(table.ToString());
        Assert.IsEmpty(failures, $"{string.Join(Environment.NewLine, failures)}{Environment.NewLine}{table}");
    }

    /// <summary>Today's number plus the headroom, rounded up to a multiple of 8 bytes.</summary>
    private static long Suggest(long bytes) => (long)Math.Ceiling(bytes * Headroom / 8) * 8;

    private static (long BytesPerOperation, double MicrosecondsPerOperation) Measure(Scenario scenario)
    {
        var operation = scenario.Setup();
        var i = 0;
        for (; i < Scenarios.WarmupIterations; i++)
        {
            operation(i);
        }

        System.GC.Collect();
        System.GC.WaitForPendingFinalizers();
        var allocated0 = System.GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        for (var end = i + scenario.Iterations; i < end; i++)
        {
            operation(i);
        }

        stopwatch.Stop();
        var allocated = System.GC.GetAllocatedBytesForCurrentThread() - allocated0;
        return (allocated / scenario.Iterations, stopwatch.Elapsed.TotalMicroseconds / scenario.Iterations);
    }
}
