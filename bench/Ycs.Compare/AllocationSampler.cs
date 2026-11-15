using System.Collections.Concurrent;
using System.Diagnostics.Tracing;

namespace Ycs.Compare;

/// <summary>
/// Where a workload's managed allocations go, by type: the runtime's allocation ticks (one event per about 100 KB
/// allocated, naming the type of the allocation that crossed it), counted while <see cref="Active"/> is set (the
/// timed bodies). Opt-in with <c>COMPARE_ALLOC_TYPES=1</c> on a child; printed to stderr, so the JSON on stdout is
/// unchanged. The events arrive on a dispatch thread, a little after the allocation: <see cref="Settle"/> waits for
/// them before the flag changes. A sample, not an exact account: a type's share of the ticks estimates its share of
/// the bytes.
/// </summary>
internal sealed class AllocationSampler : EventListener
{
    private const int GCKeyword = 0x1;
    private const int AllocationTickEventId = 10;

    private readonly ConcurrentDictionary<string, (long Bytes, int Ticks)> _byType = new();
    private volatile bool _active;

    public bool Active
    {
        set => _active = value;
    }

    public static AllocationSampler StartIfRequested() =>
        Environment.GetEnvironmentVariable("COMPARE_ALLOC_TYPES") == "1" ? new AllocationSampler() : null;

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
        {
            EnableEvents(source, EventLevel.Verbose, (EventKeywords)GCKeyword);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (!_active || e.EventId != AllocationTickEventId || e.PayloadNames == null)
        {
            return;
        }

        var type = "?";
        long amount = 0;
        for (var i = 0; i < e.PayloadNames.Count; i++)
        {
            switch (e.PayloadNames[i])
            {
                case "TypeName":
                    type = e.Payload![i] as string ?? "?";
                    break;
                case "AllocationAmount64":
                    amount = Convert.ToInt64(e.Payload![i]);
                    break;
            }
        }

        _byType.AddOrUpdate(type, (amount, 1), (_, v) => (v.Bytes + amount, v.Ticks + 1));
    }

    public static void Settle() => Thread.Sleep(300);

    public void Print(string caseName, int runs)
    {
        var total = _byType.Values.Sum(v => v.Bytes);
        Console.Error.WriteLine($"allocation ticks of {caseName} (timed runs: {runs}), {total / (double)(1 << 20) / Math.Max(runs, 1):F1} MiB per run sampled:");
        foreach (var (type, (bytes, ticks)) in _byType.OrderByDescending(kv => kv.Value.Bytes).Take(25))
        {
            Console.Error.WriteLine($"  {bytes * 100.0 / Math.Max(total, 1),5:F1}%  {ticks,6} ticks  {type}");
        }
    }
}
