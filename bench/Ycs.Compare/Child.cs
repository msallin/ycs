using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Ycs.Compare;

/// <summary>
/// One workload on one library in its own process: <see cref="WarmUps"/> warm-up runs, then <c>runs</c> timed runs,
/// each on a fresh document after a forced full GC (a <see cref="Case.HeavyOn"/> workload: one timed run, no warm-up).
/// The first warm-up's time is reported as the cold time. What the last run's finished document holds is measured
/// against the heap before its setup. Prints one JSON object.
/// </summary>
internal static class Child
{
    /// <summary>
    /// Tiered compilation needs more than one run to reach steady state (README.md, "Method").
    /// </summary>
    private const int WarmUps = 3;

    public static int Run(string lib, string caseName, string fixturesDir, string outDir, int runs)
    {
        var c = Catalog.Find(caseName);
        var make = lib switch { "ycs" => c.Ycs, "yrs" => c.Yrs, _ => throw new ArgumentException(lib) };
        var fixtures = new Fixtures(fixturesDir);
        var load0 = File.ReadAllText("/proc/loadavg").Split(' ')[0];
        var heavy = c.HeavyOn(lib);
        var first = heavy ? 1 : 1 - WarmUps;
        runs = heavy ? 1 : runs;
        double cold = 0;
        var times = new List<double>();
        var allocs = new List<long>();
        int v1 = 0, v2 = 0;
        long managed0 = 0, native0 = 0, managed1 = 0, native1 = 0;
        for (var r = first; r <= runs; r++)
        {
            if (r == runs)
            {
                // The last run's finished document is the one measured; the previous run's is disposed by now.
                FullGc();
                managed0 = System.GC.GetTotalMemory(forceFullCollection: true);
                native0 = NativeHeap.BytesInUse();
            }

            using var p = make(fixtures);
            FullGc();
            var a0 = System.GC.GetTotalAllocatedBytes(precise: true);
            var sw = Stopwatch.StartNew();
            p.Body();
            sw.Stop();
            var a1 = System.GC.GetTotalAllocatedBytes(precise: true);
            if (r == first)
            {
                cold = sw.Elapsed.TotalMilliseconds;
            }

            if (r <= 0)
            {
                continue;
            }

            times.Add(sw.Elapsed.TotalMilliseconds);
            allocs.Add(a1 - a0);
            if (r == runs)
            {
                var stem = Path.Combine(outDir, $"{caseName.Replace('/', '_')}.{lib}");
                File.WriteAllText(stem + ".content", p.Content());
                var state = p.StateV1();
                File.WriteAllBytes(stem + ".v1", state);
                v1 = state.Length;
                v2 = p.StateV2().Length;
                state = null;
                FullGc();
                managed1 = System.GC.GetTotalMemory(forceFullCollection: true);
                native1 = NativeHeap.BytesInUse();
                System.GC.KeepAlive(p);
            }
        }

        using var stdout = Console.OpenStandardOutput();
        using var w = new Utf8JsonWriter(stdout);
        w.WriteStartObject();
        w.WriteString("lib", lib);
        w.WriteString("case", caseName);
        w.WriteString("inputs", Inputs.Hash());
        w.WriteStartArray("ms");
        times.ForEach(w.WriteNumberValue);
        w.WriteEndArray();
        w.WriteNumber("coldMs", cold);
        w.WriteStartArray("alloc");
        allocs.ForEach(w.WriteNumberValue);
        w.WriteEndArray();
        w.WriteNumber("heldManaged", managed1 - managed0);
        w.WriteNumber("heldNative", native1 - native0);
        w.WriteNumber("v1", v1);
        w.WriteNumber("v2", v2);
        w.WriteString("load0", load0);
        w.WriteString("load1", File.ReadAllText("/proc/loadavg").Split(' ')[0]);
        w.WriteEndObject();
        return 0;
    }

    private static void FullGc()
    {
        System.GC.Collect(System.GC.MaxGeneration, System.GCCollectionMode.Forced, blocking: true, compacting: true);
        System.GC.WaitForPendingFinalizers();
        System.GC.Collect(System.GC.MaxGeneration, System.GCCollectionMode.Forced, blocking: true, compacting: true);
    }
}

/// <summary>
/// The bytes in use in the C heap (glibc <c>mallinfo2</c>: <c>uordblks + hblkhd</c>). yrs allocates through the system
/// allocator, so this counts its live allocations exactly, unlike the resident set, which keeps freed pages.
/// </summary>
internal static class NativeHeap
{
    public static long BytesInUse()
    {
        var info = MallInfo2();
        return (long)(info.Uordblks + info.Hblkhd);
    }

    [DllImport("libc", EntryPoint = "mallinfo2")]
    private static extern Info MallInfo2();

    [StructLayout(LayoutKind.Sequential)]
    private struct Info
    {
        public nuint Arena;
        public nuint Ordblks;
        public nuint Smblks;
        public nuint Hblks;
        public nuint Hblkhd;
        public nuint Usmblks;
        public nuint Fsmblks;
        public nuint Uordblks;
        public nuint Fordblks;
        public nuint Keepcost;
    }
}
